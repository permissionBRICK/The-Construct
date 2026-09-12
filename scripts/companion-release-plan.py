#!/usr/bin/env python3
"""Decide which Companion release belongs to a Construct commit.

The Companion is rebuilt only when its sources changed since the newest published
Companion release; otherwise the commit points at that existing release. Both release
workflows run this so the host manifest's companionReleaseTag and the Companion build
agree. Prints JSON: {"tag": "companion-<commit40>", "build": true|false, "reason": "..."}.
"""
import argparse
import json
import re
import subprocess
import sys

# Mirrors what the Companion package contains or installs (see docs/companion.md).
COMPANION_PATHS = ['companion', 'extension/media', 'extension/vm', 'extension/src', 'extension/test',
                   'test/fixtures/companion-parity', 'lib/Construct.Companion.ps1', 'lib/Construct.Runtime.ps1',
                   'Install-ConstructCompanion.ps1', 'test/companion-*.test.ps1', 'Auto-Install.ps1',
                   'Update-Construct.ps1', 'Provision-AgentVM.ps1', '.github/workflows/companion-release.yml',
                   'scripts/companion-release-plan.py']


def list_releases():
    text = subprocess.check_output(['gh', 'release', 'list', '--limit', '500', '--exclude-drafts', '--exclude-pre-releases',
                                    '--json', 'tagName,publishedAt'], text=True)
    return json.loads(text)


def newest_companion(releases):
    rows = [r for r in releases if re.fullmatch(r'companion-[0-9a-f]{40}', r.get('tagName', '')) and r.get('publishedAt')]
    return max(rows, key=lambda r: r['publishedAt'])['tagName'] if rows else None


def changed_since(root, base, commit):
    if subprocess.run(['git', '-C', str(root), 'cat-file', '-e', base + '^{commit}'], capture_output=True).returncode != 0:
        subprocess.run(['git', '-C', str(root), 'fetch', '--quiet', 'origin', base], capture_output=True)
    diff = subprocess.run(['git', '-C', str(root), 'diff', '--name-only', base, commit, '--', *COMPANION_PATHS],
                          capture_output=True, text=True)
    if diff.returncode != 0:
        return None
    return [line for line in diff.stdout.splitlines() if line.strip()]


def plan(root, commit, releases):
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid commit')
    own = 'companion-' + commit
    if any(r.get('tagName') == own for r in releases):
        return {'tag': own, 'build': False, 'reason': 'already-published'}
    newest = newest_companion(releases)
    if newest is None:
        return {'tag': own, 'build': True, 'reason': 'no-companion-release'}
    changed = changed_since(root, newest[len('companion-'):], commit)
    if changed is None:
        return {'tag': own, 'build': True, 'reason': 'base-unavailable'}
    if changed:
        return {'tag': own, 'build': True, 'reason': 'changed:' + ','.join(changed[:20])}
    return {'tag': newest, 'build': False, 'reason': 'unchanged'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', default='.')
    parser.add_argument('--commit', required=True)
    parser.add_argument('--releases', help='JSON file with gh release list output (tests)')
    args = parser.parse_args()
    releases = json.load(open(args.releases)) if args.releases else list_releases()
    result = plan(args.root, args.commit, releases)
    json.dump(result, sys.stdout)
    print()
