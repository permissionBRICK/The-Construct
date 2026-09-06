import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('source', Path(__file__).resolve().parents[1] / 'bin/t3code-build-source.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


class Source(unittest.TestCase):
    def test_server_and_deferred_desktop_use_same_commit_after_main_advances(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = root / 'repo'
            p.run('git', 'init', '--quiet', '--initial-branch=main', str(repo))
            def commit(text):
                (repo / 'recipe').write_text(text)
                p.run('git', '-C', str(repo), 'add', '.')
                p.run('git', '-C', str(repo), '-c', 'user.name=Test', '-c', 'user.email=test@example.test', 'commit', '--quiet', '-m', text)
                return p.run('git', '-C', str(repo), 'rev-parse', 'HEAD')
            first = commit('one')
            artifacts = root / 'artifacts'
            artifacts.mkdir()
            with patch.dict(os.environ, T3CODE_BUILD_REPOSITORY=str(repo), T3CODE_BUILD_REPO_CACHE=str(root / 'cache'),
                            T3CODE_ARTIFACT_ROOT=str(artifacts), T3CODE_BUILD_MODE='server'):
                source = p.resolve_source()
                self.assertEqual('one', (source / 'recipe').read_text())
                (artifacts / 'server-manifest.json').write_text(json.dumps(dict(buildRepositoryCommit=first)))
                second = commit('two')
                with patch.dict(os.environ, T3CODE_BUILD_MODE='desktop'):
                    self.assertEqual(source, p.resolve_source())
                self.assertEqual('two', (p.resolve_source() / 'recipe').read_text())
                self.assertTrue(source.exists())
                self.assertEqual(source, p.resolve_source(installed=True))

    def test_desktop_without_prepared_commit_fails(self):
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ, T3CODE_BUILD_MODE='desktop',
                T3CODE_ARTIFACT_ROOT=root, T3CODE_BUILD_REPO_CACHE=root):
            with self.assertRaisesRegex(ValueError, 'provision it again'):
                p.resolve_source()

if __name__ == '__main__':
    unittest.main()
