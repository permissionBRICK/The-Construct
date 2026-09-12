#!/usr/bin/env python3
"""Local release publication regressions; deliberately outside Actions."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('publisher', Path(__file__).parents[1] / 'scripts/publish-construct-release.py')
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


def fixture(output, commit):
    manifest = dict(commit=commit, repository='owner/repo', releaseTag='host-' + commit)
    for key, name in [('payload', f'construct-host-{commit[:7]}-win-x64.zip'),
                      ('frameworkDependent', f'construct-host-{commit[:7]}-win-x64-fdd.zip'),
                      ('source', f'construct-source-{commit}.zip')]:
        data = key.encode()
        (output / name).write_bytes(data)
        manifest.update({key + 'Asset': name, key + 'SizeBytes': len(data), key + 'Sha256': hashlib.sha256(data).hexdigest()})
    (output / 'manifest.json').write_text(json.dumps(manifest))
    (output / 'SHA256SUMS').write_text('fixture sums')
    return ['manifest.json', manifest['payloadAsset'], manifest['frameworkDependentAsset'], manifest['sourceAsset'], 'SHA256SUMS']


plan_spec = importlib.util.spec_from_file_location('companion_plan', Path(__file__).parents[1] / 'scripts/companion-release-plan.py')
companion_plan = importlib.util.module_from_spec(plan_spec)
plan_spec.loader.exec_module(companion_plan)
package_spec = importlib.util.spec_from_file_location('packager', Path(__file__).parents[1] / 'scripts/package-construct-release.py')
packager = importlib.util.module_from_spec(package_spec)
package_spec.loader.exec_module(packager)


class CompanionPlanTests(unittest.TestCase):
    def setUp(self):
        import subprocess
        self.dir = tempfile.TemporaryDirectory(); self.root = Path(self.dir.name)
        env = dict(os.environ, GIT_AUTHOR_NAME='t', GIT_AUTHOR_EMAIL='t@example.invalid', GIT_COMMITTER_NAME='t', GIT_COMMITTER_EMAIL='t@example.invalid')
        def git(*args): return subprocess.check_output(['git', '-C', str(self.root), *args], text=True, env=env).strip()
        self.git = git
        git('init', '-q'); (self.root / 'README.md').write_text('a'); git('add', '.'); git('commit', '-qm', 'base'); self.base = git('rev-parse', 'HEAD')
        (self.root / 'README.md').write_text('b'); git('commit', '-qam', 'docs'); self.docs = git('rev-parse', 'HEAD')
        (self.root / 'companion').mkdir(); (self.root / 'companion/App.cs').write_text('x'); git('add', '.'); git('commit', '-qm', 'companion'); self.companion = git('rev-parse', 'HEAD')

    def tearDown(self): self.dir.cleanup()

    def releases(self, *tags):
        return [dict(tagName=t, publishedAt=f'2026-09-1{i}T00:00:00Z') for i, t in enumerate(tags)]

    def test_docs_only_commit_points_at_newest_companion_release(self):
        result = companion_plan.plan(self.root, self.docs, self.releases('host-' + self.docs, 'companion-' + self.base))
        self.assertEqual(result, {'tag': 'companion-' + self.base, 'build': False, 'reason': 'unchanged'})

    def test_companion_change_builds_its_own_release(self):
        result = companion_plan.plan(self.root, self.companion, self.releases('companion-' + self.base))
        self.assertEqual(('companion-' + self.companion, True), (result['tag'], result['build']))
        self.assertIn('companion/App.cs', result['reason'])

    def test_first_release_and_published_rerun(self):
        self.assertEqual(companion_plan.plan(self.root, self.docs, [])['build'], True)
        result = companion_plan.plan(self.root, self.docs, self.releases('companion-' + self.docs))
        self.assertEqual(result, {'tag': 'companion-' + self.docs, 'build': False, 'reason': 'already-published'})

    def test_newest_release_by_publish_time_and_drafts_ignored_by_caller(self):
        older = 'companion-' + self.base; newer = 'companion-' + self.docs
        self.assertEqual(companion_plan.newest_companion([dict(tagName=newer, publishedAt='2026-09-12T00:00:00Z'), dict(tagName=older, publishedAt='2026-09-11T00:00:00Z'), dict(tagName='host-' + self.docs, publishedAt='2026-09-13T00:00:00Z')]), newer)

    def test_package_records_companion_release_tag(self):
        with tempfile.TemporaryDirectory() as out:
            output = Path(out); commit = self.companion
            manifest = dict(commit=commit, repository='owner/repo', releaseTag='host-' + commit)
            for key, name in [('payload', f'construct-host-{commit[:7]}-win-x64.zip'), ('frameworkDependent', f'construct-host-{commit[:7]}-win-x64-fdd.zip')]:
                data = key.encode(); (output / name).write_bytes(data)
                manifest.update({key + 'Asset': name, key + 'SizeBytes': len(data), key + 'Sha256': hashlib.sha256(data).hexdigest()})
            (output / 'manifest.json').write_text(json.dumps(manifest)); (output / 'SHA256SUMS').write_text('')
            with self.assertRaises(ValueError):
                packager.package(self.root, output, commit, 'owner/repo', 'companion-latest')
            packager.package(self.root, output, commit, 'owner/repo', 'companion-' + self.base)
            self.assertEqual(json.loads((output / 'manifest.json').read_text())['companionReleaseTag'], 'companion-' + self.base)


class ReleaseTests(unittest.TestCase):
    def test_malformed_companion_pointer_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp); commit = 'a' * 40; fixture(output, commit)
            manifest = json.loads((output / 'manifest.json').read_text()); manifest['companionReleaseTag'] = 'companion-latest'
            (output / 'manifest.json').write_text(json.dumps(manifest))
            with patch.dict(os.environ, GITHUB_SHA=commit, GITHUB_REPOSITORY='owner/repo', GITHUB_TOKEN='t'), patch.object(publisher, 'api') as api:
                with self.assertRaises(ValueError): publisher.publish(output)
                api.assert_not_called()

    def test_old_rerun_cannot_replace_newer_latest(self):
        ancestry = lambda a, b: (a, b) in [('old', 'new')]
        self.assertFalse(publisher.should_promote('old', 'new', ancestry, 'old'))
        self.assertTrue(publisher.should_promote('new', 'old', ancestry, 'new'))
        self.assertTrue(publisher.should_promote('new', 'new', ancestry, 'new'))

    def test_history_rewrite_requires_current_main(self):
        self.assertFalse(publisher.should_promote('abandoned', 'unrelated', lambda a, b: False, 'main'))
        self.assertTrue(publisher.should_promote('main', 'unrelated', lambda a, b: False, 'main'))

    def test_upload_failure_leaves_latest_untouched_and_release_in_draft(self):
        commit = 'a' * 40
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            names = fixture(output, commit)
            commands = []
            def run(command, **kwargs):
                commands.append(command)
                if command[1:3] == ['release', 'upload']:
                    raise RuntimeError('upload interrupted')
            with patch.dict(os.environ, GITHUB_SHA=commit, GITHUB_REPOSITORY='owner/repo'), \
                 patch.object(publisher, 'api', return_value=None), \
                 patch.object(publisher.subprocess, 'check_output', return_value=commit + '\trefs/heads/main'), \
                 patch.object(publisher.subprocess, 'run', side_effect=run):
                with self.assertRaisesRegex(RuntimeError, 'upload interrupted'):
                    publisher.publish(output)
            self.assertIn('--draft', commands[0])
            self.assertFalse(any(command[1:3] == ['release', 'edit'] for command in commands))

    def test_success_publishes_only_after_upload_and_then_advances_latest(self):
        commit = 'a' * 40
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            names = fixture(output, commit)
            with patch.dict(os.environ, GITHUB_SHA=commit, GITHUB_REPOSITORY='owner/repo'), \
                 patch.object(publisher, 'api', return_value=None), \
                 patch.object(publisher.subprocess, 'check_output', return_value=commit + '\trefs/heads/main'), \
                 patch.object(publisher.subprocess, 'run') as run:
                publisher.publish(output)
            commands = [call.args[0] for call in run.call_args_list]
            self.assertEqual([command[2] for command in commands], ['create', 'upload', 'edit', 'edit'])
            self.assertEqual(commands[0], ['gh','release','create','host-'+commit,'--draft','--target',commit,'--title','Construct '+commit[:7],'--notes','Source and Windows host artifacts from the same main commit. See docs/host-release.md.'])
            self.assertEqual(commands[1], ['gh','release','upload','host-'+commit,'--clobber',*[str(output / name) for name in names]])
            self.assertEqual(commands[2], ['gh','release','edit','host-'+commit,'--draft=false','--latest=false'])
            self.assertEqual(commands[3], ['gh','release','edit','host-'+commit,'--latest'])
            self.assertIn('--latest=false', commands[-2])
            self.assertIn('--latest', commands[-1])

    def test_published_retry_does_not_reupload_assets(self):
        commit = 'a' * 40
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            names = fixture(output, commit)
            release = dict(tag_name='host-' + commit, draft=False, assets=[dict(name=name) for name in names])
            with patch.dict(os.environ, GITHUB_SHA=commit, GITHUB_REPOSITORY='owner/repo'), \
                 patch.object(publisher, 'api', return_value=release), \
                 patch.object(publisher.subprocess, 'check_output', return_value=commit + '\trefs/heads/main'), \
                 patch.object(publisher.subprocess, 'run') as run:
                publisher.publish(output)
            self.assertEqual(len(run.call_args_list), 1)
            self.assertEqual(run.call_args.args[0][1:3], ['release', 'edit'])

    def test_missing_or_corrupt_fdd_never_creates_release(self):
        for scenario in ('missing', 'corrupt', 'wrong-name'):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as directory:
                output = Path(directory)
                names = fixture(output, 'a' * 40)
                if scenario == 'missing':
                    (output / names[2]).unlink()
                elif scenario == 'corrupt':
                    (output / names[2]).write_bytes(b'corrupted')
                else:
                    manifest = json.loads((output / 'manifest.json').read_text())
                    manifest['frameworkDependentAsset'] = '../outside.zip'
                    (output / 'manifest.json').write_text(json.dumps(manifest))
                with patch.dict(os.environ, GITHUB_SHA='a' * 40, GITHUB_REPOSITORY='owner/repo'), patch.object(publisher, 'api') as api:
                    with self.assertRaises(ValueError):
                        publisher.publish(output)
                    api.assert_not_called()


if __name__ == '__main__':
    unittest.main()
