#!/usr/bin/env python3
"""Release tooling for the nightly and stable channels (docs/release-flow.md).

  status   PRs on nightly that main does not have yet, with labels, touched files and
           files an earlier pending PR also touched.
  cut      Creates release/vX.Y.Z from main, by merging nightly (the normal case) or by
           cherry-picking chosen PR merge commits (--only / --exclude), commits VERSION,
           pushes the branch and opens the release PR. Run it again after resolving a
           conflict: picks already on the branch are skipped.
  verify   Runs `test/run-local-checks.sh all` and posts the `local-checks` commit status.
  notes    Prints the release notes of everything pending (nightly prerelease notes).
  version  Prints the version builds of this checkout report before `+<sha>`.

Every GitHub call goes through the GitHub class; tests replace it.
"""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

MERGE_SUBJECT = re.compile(r'^Merge pull request #(\d+) from ')
PICKED = re.compile(r'^\(cherry picked from commit ([0-9a-f]{40})\)$', re.M)
TAG = re.compile(r'^v(\d+)\.(\d+)\.(\d+)$')
FIRST_VERSION = (1, 0, 0)
# Label -> heading, in the order the notes list them; anything else is "Other changes".
GROUPS = [('breaking', 'Breaking changes'), ('enhancement', 'Enhancements'), ('bug', 'Fixes')]


class GitHub:
    """The only place that talks to GitHub (through the gh CLI)."""

    def __init__(self, root):
        self.root = root

    def _gh(self, *args):
        return subprocess.run(['gh', *args], cwd=self.root, check=True, capture_output=True, text=True).stdout

    def pull(self, number):
        data = json.loads(self._gh('pr', 'view', str(number), '--json', 'title,body,labels'))
        return {'title': data['title'], 'body': data.get('body') or '', 'labels': [label['name'] for label in data['labels']]}

    def create_pull(self, base, head, title, body):
        return self._gh('pr', 'create', '--base', base, '--head', head, '--title', title, '--body', body).strip()

    def set_status(self, sha, state, context, description):
        self._gh('api', '--method', 'POST', 'repos/{owner}/{repo}/statuses/' + sha,
                 '-f', 'state=' + state, '-f', 'context=' + context, '-f', 'description=' + description)


def git(root, *args, check=True):
    result = subprocess.run(['git', '-C', str(root), *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f'git {" ".join(args)} failed: {result.stderr.strip()}')
    return result


def out(root, *args):
    return git(root, *args).stdout.strip()


def parse_version(text):
    match = TAG.match('v' + text.strip().removeprefix('v'))
    return tuple(int(part) for part in match.groups()) if match else None


def current_version(root, ref='HEAD'):
    """The newest released version: the highest vX.Y.Z tag or the VERSION file at ref, else 1.0.0."""
    found = [FIRST_VERSION]
    found += [v for v in (parse_version(t) for t in out(root, 'tag', '--list', 'v*').split()) if v]
    shown = git(root, 'show', f'{ref}:VERSION', check=False)
    if shown.returncode == 0 and parse_version(shown.stdout):
        found.append(parse_version(shown.stdout))
    return max(found)


def bump(version, labels):
    major, minor, patch = version
    if 'breaking' in labels:
        return (major + 1, 0, 0)
    if 'enhancement' in labels:
        return (major, minor + 1, 0)
    return (major, minor, patch + 1)


def text(version):
    return '.'.join(map(str, version))


def pending(root, base, head):
    """PR merge commits on head's first-parent line that base neither contains nor cherry-picked, oldest first."""
    log = out(root, 'log', '--first-parent', '--merges', '--reverse', '--format=%H%x00%s', f'{base}..{head}')
    picked = set(PICKED.findall(out(root, 'log', '--format=%B', base)))
    result = []
    for line in filter(None, log.splitlines()):
        sha, subject = line.split('\0', 1)
        match = MERGE_SUBJECT.match(subject)
        if match and sha not in picked:
            files = out(root, 'diff', '--name-only', f'{sha}^1', sha).splitlines()
            result.append({'number': int(match.group(1)), 'sha': sha, 'files': files})
    return result


def describe(prs, github):
    for pr in prs:
        pr.update(github.pull(pr['number']))
    return prs


def note_line(pr):
    body = pr['body'].replace('\r\n', '\n')
    section = re.search(r'^## Release notes[ \t]*\n(.*?)(?=^#{1,2} |\Z)', body, re.M | re.S)
    line = ' '.join(section.group(1).split()) if section else ''
    return f'- {line or pr["title"]} (#{pr["number"]})'


def notes(prs):
    sections = []
    taken = set()
    for label, heading in GROUPS + [(None, 'Other changes')]:
        rows = [pr for pr in prs if pr['number'] not in taken and (label is None or label in pr['labels'])]
        taken.update(pr['number'] for pr in rows)
        if rows:
            sections.append(f'### {heading}\n\n' + '\n'.join(note_line(pr) for pr in rows))
    return '\n\n'.join(sections) or 'No pull requests.'


def overlaps(prs):
    """Per PR, the earlier pending PRs that touched one of its files (a hint, not a dependency proof)."""
    warnings = {}
    for i, pr in enumerate(prs):
        earlier = [o['number'] for o in prs[:i] if set(o['files']) & set(pr['files'])]
        if earlier:
            warnings[pr['number']] = earlier
    return warnings


def status(root, github, base, head):
    prs = describe(pending(root, base, head), github)
    warnings = overlaps(prs)
    lines = [f'{len(prs)} PR(s) on {head} not on {base}; next version v{text(bump(current_version(root, base), {l for pr in prs for l in pr["labels"]}))}']
    for pr in prs:
        labels = ', '.join(pr['labels']) or 'no labels'
        lines.append(f'#{pr["number"]} {pr["title"]} [{labels}] {pr["sha"][:7]}')
        lines += [f'    {name}' for name in pr['files']]
        if pr['number'] in warnings:
            lines.append('    shares files with earlier pending ' + ', '.join(f'#{n}' for n in warnings[pr['number']]))
    return '\n'.join(lines)


def select(prs, only, exclude):
    known = {pr['number'] for pr in prs}
    unknown = sorted((set(only or []) | set(exclude or [])) - known)
    if unknown:
        raise SystemExit('Not pending on nightly: ' + ', '.join(f'#{n}' for n in unknown))
    if only:
        return [pr for pr in prs if pr['number'] in only]
    return [pr for pr in prs if pr['number'] not in (exclude or [])]


def release_body(version, included, held):
    body = f'Release v{version}.\n\n## Release notes\n\n{notes(included)}\n\n## Held back\n\n'
    body += '\n'.join(f'- {pr["title"]} (#{pr["number"]})' for pr in held) if held else 'Nothing.'
    return body + '\n'


def cut(root, github, base, head, only=None, exclude=None, dry_run=False, remote='origin'):
    prs = describe(pending(root, base, head), github)
    included = select(prs, only, exclude)
    if not included:
        raise SystemExit('Nothing to release.')
    held = [pr for pr in prs if pr not in included]
    version = text(bump(current_version(root, base), {label for pr in included for label in pr['labels']}))
    branch = 'release/v' + version
    body = release_body(version, included, held)
    if dry_run:
        return f'{branch}\n\n{body}'
    if git(root, 'diff', '--quiet', 'HEAD', check=False).returncode != 0:
        raise SystemExit('The working tree has uncommitted changes.')
    if out(root, 'rev-parse', '--abbrev-ref', 'HEAD') != branch:
        if git(root, 'rev-parse', '--verify', '--quiet', 'refs/heads/' + branch, check=False).returncode == 0:
            raise SystemExit(f'{branch} exists; check it out to continue the cut, or delete it.')
        git(root, 'switch', '-q', '-c', branch, base)
    if held:
        done = set(PICKED.findall(out(root, 'log', '--format=%B', f'{base}..HEAD')))
        for pr in included:
            if pr['sha'] in done:
                continue
            if git(root, 'cherry-pick', '-m', '1', '-x', pr['sha'], check=False).returncode != 0:
                raise SystemExit(f'Cherry-pick of #{pr["number"]} ({pr["sha"][:7]}) conflicts. Resolve it as '
                                 'docs/release-flow.md describes, run `git cherry-pick --continue`, then run this cut again.')
    elif git(root, 'merge-base', '--is-ancestor', head, 'HEAD', check=False).returncode != 0:
        if git(root, 'merge', '--no-ff', '-q', '-m', f'Merge {head} for v{version}', head, check=False).returncode != 0:
            raise SystemExit(f'Merging {head} conflicts. Resolve it, run `git commit`, then run this cut again.')
    if not ((root / 'VERSION').is_file() and (root / 'VERSION').read_text().strip() == version):
        (root / 'VERSION').write_text(version + '\n')
        git(root, 'add', 'VERSION')
        git(root, 'commit', '-q', '-m', f'Release v{version}')
    git(root, 'push', '-q', remote, f'HEAD:refs/heads/{branch}')
    return github.create_pull(base.split('/', 1)[-1], branch, f'Release v{version}', body)


def verify(root, github, run=subprocess.run):
    if out(root, 'status', '--porcelain'):
        raise SystemExit('The working tree has uncommitted changes; local-checks would not describe HEAD.')
    sha = out(root, 'rev-parse', 'HEAD')
    passed = run(['bash', 'test/run-local-checks.sh', 'all'], cwd=root).returncode == 0
    github.set_status(sha, 'success' if passed else 'failure', 'local-checks',
                      'run-local-checks.sh all ' + ('passed' if passed else 'failed'))
    return passed


def main(argv=None, github=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('status', 'cut', 'notes'):
        command = commands.add_parser(name)
        command.add_argument('--base', default='origin/main')
        command.add_argument('--head', default='origin/nightly')
    cut_parser = commands.choices['cut']
    picks = cut_parser.add_mutually_exclusive_group()
    picks.add_argument('--only', type=int, nargs='+', metavar='PR')
    picks.add_argument('--exclude', type=int, nargs='+', metavar='PR')
    cut_parser.add_argument('--dry-run', action='store_true')
    commands.add_parser('verify')
    commands.add_parser('version')
    args = parser.parse_args(argv)
    root = args.root
    github = github or GitHub(root)
    if args.command == 'status':
        print(status(root, github, args.base, args.head))
    elif args.command == 'notes':
        print(notes(describe(pending(root, args.base, args.head), github)))
    elif args.command == 'cut':
        print(cut(root, github, args.base, args.head, args.only, args.exclude, args.dry_run))
    elif args.command == 'verify':
        return 0 if verify(root, github) else 1
    elif args.command == 'version':
        print(text(current_version(root)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
