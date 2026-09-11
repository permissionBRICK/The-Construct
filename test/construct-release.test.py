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


class ReleaseTests(unittest.TestCase):
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
