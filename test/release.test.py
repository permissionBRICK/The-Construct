#!/usr/bin/env python3
"""scripts/release.py against temporary git repositories; GitHub is a fake."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location('release', Path(__file__).parents[1] / 'scripts/release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

groups_spec = importlib.util.spec_from_file_location('changed_groups', Path(__file__).parent / 'helpers/changed-groups.py')
changed_groups = importlib.util.module_from_spec(groups_spec)
groups_spec.loader.exec_module(changed_groups)

ENV = dict(os.environ, GIT_AUTHOR_NAME='t', GIT_AUTHOR_EMAIL='t@example.invalid', GIT_COMMITTER_NAME='t',
           GIT_COMMITTER_EMAIL='t@example.invalid', GIT_CONFIG_GLOBAL='/dev/null', GIT_CONFIG_NOSYSTEM='1')


class FakeGitHub:
    def __init__(self, pulls):
        self.pulls, self.created, self.statuses = pulls, [], []

    def pull(self, number):
        return dict(self.pulls[number])

    def create_pull(self, base, head, title, body):
        self.created.append(dict(base=base, head=head, title=title, body=body))
        return 'https://example.invalid/pull/99'

    def set_status(self, sha, state, context, description):
        self.statuses.append((sha, state, context))


class Repo:
    """origin (bare) with main and nightly; nightly carries PR merge commits like GitHub makes them."""

    def __init__(self, directory):
        self.origin = Path(directory) / 'origin.git'
        self.root = Path(directory) / 'work'
        subprocess.run(['git', 'init', '-q', '--bare', '-b', 'main', str(self.origin)], check=True, env=ENV)
        subprocess.run(['git', 'clone', '-q', str(self.origin), str(self.root)], check=True, env=ENV, capture_output=True)
        self.git('switch', '-q', '-c', 'main')
        self.write('app.txt', 'one\n')
        self.write('lib.txt', 'base\n')
        self.git('add', '.'); self.git('commit', '-qm', 'base')
        self.git('push', '-q', 'origin', 'main')
        self.git('switch', '-q', '-c', 'nightly')

    def git(self, *args, check=True):
        return subprocess.run(['git', '-C', str(self.root), *args], check=check, env=ENV, capture_output=True, text=True).stdout.strip()

    def write(self, name, text):
        (self.root / name).write_text(text)

    def merge_pr(self, number, files, onto='nightly'):
        """A PR branch from onto, merged with a merge commit; returns the merge commit."""
        self.git('switch', '-q', onto)
        self.git('switch', '-q', '-c', f'pr{number}')
        for name, text in files.items():
            self.write(name, text)
        self.git('add', '.'); self.git('commit', '-qm', f'change {number}')
        self.git('switch', '-q', onto)
        self.git('merge', '-q', '--no-ff', '-m', f'Merge pull request #{number} from someone/pr{number}', f'pr{number}')
        self.git('push', '-q', 'origin', onto)
        self.git('fetch', '-q', 'origin')
        return self.git('rev-parse', 'HEAD')


PULLS = {
    1: dict(title='Fix the app', body='', labels=['bug']),
    2: dict(title='Add a library call', body='Details.\n\n## Release notes\n\nThe library can\nnow call home.\n\n## Testing\nran it', labels=['enhancement']),
    3: dict(title='Rename the app', body='', labels=[]),
}


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.repo = Repo(self.dir.name)
        self.repo.merge_pr(1, {'app.txt': 'two\n'})
        self.repo.merge_pr(2, {'lib.txt': 'call\n'})
        self.repo.merge_pr(3, {'app.txt': 'three\n'})
        self.root = self.repo.root
        self.github = FakeGitHub(PULLS)

    def tearDown(self):
        self.dir.cleanup()

    def test_versions_follow_labels_and_tags(self):
        self.assertEqual(release.bump((1, 2, 3), {'bug'}), (1, 2, 4))
        self.assertEqual(release.bump((1, 2, 3), {'enhancement', 'bug'}), (1, 3, 0))
        self.assertEqual(release.bump((1, 2, 3), {'breaking', 'enhancement'}), (2, 0, 0))
        self.assertEqual(release.current_version(self.root), (1, 0, 0))
        self.repo.git('tag', 'v1.4.0', 'origin/main'); self.repo.git('tag', 'v1.10.0', 'origin/main'); self.repo.git('tag', 'vbad')
        self.assertEqual(release.current_version(self.root), (1, 10, 0))

    def test_status_lists_pending_prs_with_labels_files_and_shared_files(self):
        text = release.status(self.root, self.github, 'origin/main', 'origin/nightly')
        self.assertIn('3 PR(s)', text)
        self.assertIn('next version v1.1.0', text)
        self.assertIn('#1 Fix the app [bug]', text)
        self.assertIn('#3 Rename the app [no labels]', text)
        self.assertIn('    lib.txt', text)
        self.assertIn('shares files with earlier pending #1', text)
        self.assertEqual(text.count('shares files'), 1)

    def test_notes_group_by_label_and_use_release_notes_section(self):
        prs = release.describe(release.pending(self.root, 'origin/main', 'origin/nightly'), self.github)
        self.assertEqual(release.notes(prs),
                         '### Enhancements\n\n- The library can now call home. (#2)\n\n'
                         '### Fixes\n\n- Fix the app (#1)\n\n### Other changes\n\n- Rename the app (#3)')

    def test_cut_everything_merges_nightly_and_opens_the_release_pr(self):
        url = release.cut(self.root, self.github, 'origin/main', 'origin/nightly')
        self.assertEqual(url, 'https://example.invalid/pull/99')
        self.assertEqual(self.repo.git('ls-remote', 'origin', 'refs/heads/release/v1.1.0').split()[0], self.repo.git('rev-parse', 'HEAD'))
        self.assertEqual((self.root / 'VERSION').read_text(), '1.1.0\n')
        self.assertEqual(self.repo.git('merge-base', '--is-ancestor', 'origin/nightly', 'HEAD', check=False), '')
        pr = self.github.created[0]
        self.assertEqual((pr['base'], pr['head'], pr['title']), ('main', 'release/v1.1.0', 'Release v1.1.0'))
        self.assertIn('- Fix the app (#1)', pr['body'])
        self.assertIn('## Held back\n\nNothing.', pr['body'])
        # Merged into main, nothing is pending and the next build reports the new version.
        self.repo.git('switch', '-q', 'main'); self.repo.git('merge', '-q', '--no-ff', 'release/v1.1.0'); self.repo.git('push', '-q', 'origin', 'main'); self.repo.git('fetch', '-q')
        self.assertEqual(release.pending(self.root, 'origin/main', 'origin/nightly'), [])
        self.assertEqual(release.current_version(self.root), (1, 1, 0))

    def test_cut_only_cherry_picks_and_lists_what_is_held_back(self):
        release.cut(self.root, self.github, 'origin/main', 'origin/nightly', only=[1])
        self.assertEqual((self.root / 'app.txt').read_text(), 'two\n')
        self.assertEqual((self.root / 'lib.txt').read_text(), 'base\n')
        self.assertEqual((self.root / 'VERSION').read_text(), '1.0.1\n')
        body = self.github.created[0]['body']
        self.assertIn('- Fix the app (#1)', body)
        self.assertIn('## Held back\n\n- Add a library call (#2)\n- Rename the app (#3)', body)
        # After the release PR merges, the cherry-picked PR is no longer pending.
        self.repo.git('switch', '-q', 'main'); self.repo.git('merge', '-q', '--no-ff', 'release/v1.0.1'); self.repo.git('push', '-q', 'origin', 'main'); self.repo.git('fetch', '-q')
        self.assertEqual([pr['number'] for pr in release.pending(self.root, 'origin/main', 'origin/nightly')], [2, 3])

    def test_cut_exclude_and_dry_run_change_nothing(self):
        head = self.repo.git('rev-parse', 'HEAD')
        text = release.cut(self.root, self.github, 'origin/main', 'origin/nightly', exclude=[3], dry_run=True)
        self.assertTrue(text.startswith('release/v1.1.0\n'))
        self.assertIn('- Rename the app (#3)', text.split('## Held back')[1])
        self.assertEqual((self.repo.git('rev-parse', 'HEAD'), self.github.created), (head, []))
        with self.assertRaises(SystemExit):
            release.cut(self.root, self.github, 'origin/main', 'origin/nightly', only=[7])

    def test_conflicting_pick_stops_and_a_second_run_continues(self):
        # #3 changes the line #1 changed; without #1 the pick conflicts.
        with self.assertRaisesRegex(SystemExit, 'conflicts'):
            release.cut(self.root, self.github, 'origin/main', 'origin/nightly', only=[2, 3])
        self.assertEqual(self.repo.git('rev-parse', '--abbrev-ref', 'HEAD'), 'release/v1.1.0')
        self.repo.write('app.txt', 'three\n'); self.repo.git('add', 'app.txt')
        subprocess.run(['git', '-C', str(self.root), '-c', 'core.editor=true', 'cherry-pick', '--continue'], check=True, env=ENV, capture_output=True)
        release.cut(self.root, self.github, 'origin/main', 'origin/nightly', only=[2, 3])
        log = self.repo.git('log', '--format=%s', 'origin/main..HEAD').splitlines()
        self.assertEqual(log, ['Release v1.1.0', 'Merge pull request #3 from someone/pr3', 'Merge pull request #2 from someone/pr2'])
        self.assertEqual(len(self.github.created), 1)

    def test_verify_posts_local_checks_for_head(self):
        head = self.repo.git('rev-parse', 'HEAD')
        self.assertTrue(release.verify(self.root, self.github, run=lambda *a, **k: SimpleNamespace(returncode=0)))
        self.assertFalse(release.verify(self.root, self.github, run=lambda *a, **k: SimpleNamespace(returncode=1)))
        self.assertEqual(self.github.statuses, [(head, 'success', 'local-checks'), (head, 'failure', 'local-checks')])
        self.repo.write('app.txt', 'dirty\n')
        with self.assertRaises(SystemExit):
            release.verify(self.root, self.github, run=lambda *a, **k: SimpleNamespace(returncode=0))
        self.assertEqual(len(self.github.statuses), 2)


class ChangedGroupsTests(unittest.TestCase):
    rows = changed_groups.table((Path(__file__).parents[1] / 'docs/local-checks.md').read_text())

    def groups(self, *paths):
        return changed_groups.groups(paths, self.rows)

    def test_paths_map_to_the_groups_of_every_matching_row(self):
        self.assertEqual(self.groups('scripts/release.py'), ['release'])
        self.assertEqual(self.groups('companion/src/App.cs', 'lib/AgentVm.Common.ps1'), ['companion', 'service'])
        self.assertEqual(self.groups('extension/vm/t3-pairing.sh'), ['agent', 'companion', 'panel', 't3'])
        self.assertEqual(self.groups('extension/extension.js'), ['panel'])
        self.assertEqual(self.groups('install.ps1'), ['agent', 'companion', 'ui'])
        self.assertEqual(self.groups('service/src/Constructd.Core/Logic/ReleaseChannel.cs'), ['release', 'service'])

    def test_documentation_selects_nothing_and_an_unknown_path_selects_all(self):
        self.assertEqual(self.groups('docs/host-release.md', 'README.md'), [])
        self.assertEqual(self.groups('docs/x.md', 'test/oom-guard.test.sh'), ['all'])

    def test_a_single_star_stays_within_a_directory(self):
        self.assertEqual(self.groups('assets/icons/x.md'), [])
        self.assertEqual(self.groups('keys/README.md'), ['all'])
        self.assertTrue(changed_groups.pattern('bin/*t3*').match('bin/install-t3.sh'))
        self.assertFalse(changed_groups.pattern('bin/*t3*').match('bin/lib/t3.sh'))


if __name__ == '__main__':
    unittest.main()
