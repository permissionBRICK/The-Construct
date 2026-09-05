import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('prebuilt', Path(__file__).resolve().parents[1] / 'bin/install-t3code-prebuilt.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


class Prebuilt(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.environ = patch.dict(os.environ, {
            'T3CODE_PREBUILT_CACHE': str(self.root / 'cache'),
            'T3CODE_ARTIFACT_ROOT': str(self.root / 'artifacts'),
            'T3CODE_LAUNCHER': str(self.root / 't3'),
            'T3CODE_STATUS_PATH': str(self.root / 'status'),
        })
        self.environ.start()
        self.addCleanup(self.environ.stop)
        self.archive = self.root / 'source.tar.gz'
        with tarfile.open(self.archive, 'w:gz') as tar:
            for name in ('bin/t3', 'bin/node'):
                data = b'#!/bin/sh\nexit 0\n'
                member = tarfile.TarInfo(name)
                member.size = len(data)
                member.mode = 0o755
                tar.addfile(member, io.BytesIO(data))
        self.manifest = dict(formatVersion=1, channel='stable', version='1.0.0', buildHash='a' * 64,
                             patchHash='b' * 64, target='ubuntu-24.04-linux-x64+windows-x64')
        self.refresh()
        self.calls = []

    def refresh(self):
        m = self.manifest
        m['releaseTag'] = f't3-{m["version"]}-{m["buildHash"]}'
        sha = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        m['assets'] = {name: dict(url=f'{p.BASE}/download/{m["releaseTag"]}/{name}', sha256=sha,
                                 size=self.archive.stat().st_size) for name in (p.SERVER, p.DESKTOP)}
        m['sha256'] = sha
        m['downloadUrl'] = m['assets'][p.DESKTOP]['url']

    def fetch(self, url, destination):
        self.calls.append(url)
        if url.endswith('manifest.json'):
            destination.write_text(json.dumps(self.manifest))
        else:
            destination.write_bytes(self.archive.read_bytes())

    def test_install_then_reuse_downloads_only_manifest(self):
        p.install(self.fetch)
        self.assertEqual(2, len(self.calls))
        first = (self.root / 't3').resolve()
        self.calls.clear()
        p.install(self.fetch)
        self.assertEqual([f'{p.BASE}/latest/download/manifest.json'], self.calls)
        self.assertEqual(first, (self.root / 't3').resolve())
        self.assertEqual('prebuilt', json.loads((self.root / 'artifacts/server-manifest.json').read_text())['installationMode'])

    def test_version_change_installs_and_preserves_previous_runtime(self):
        p.install(self.fetch)
        previous = (self.root / 't3').resolve()
        self.manifest.update(version='1.0.1', buildHash='c' * 64)
        self.refresh()
        p.install(self.fetch)
        self.assertTrue(previous.exists())
        self.assertNotEqual(previous, (self.root / 't3').resolve())

    def test_corrupt_download_keeps_launcher_manifest_and_status(self):
        p.install(self.fetch)
        previous = (self.root / 't3').resolve()
        status = (self.root / 'status').read_bytes()
        manifest = (self.root / 'artifacts/server-manifest.json').read_bytes()
        self.manifest['buildHash'] = 'c' * 64
        self.refresh()
        self.archive.write_bytes(b'corrupt')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            p.install(self.fetch)
        self.assertEqual(previous, (self.root / 't3').resolve())
        self.assertEqual(status, (self.root / 'status').read_bytes())
        self.assertEqual(manifest, (self.root / 'artifacts/server-manifest.json').read_bytes())

    def test_mixed_pair_or_external_url_rejected(self):
        self.manifest['downloadUrl'] = 'https://example.com/installer.exe'
        with self.assertRaisesRegex(ValueError, 'Desktop handoff'):
            p.install(self.fetch)
        self.assertFalse((self.root / 't3').exists())
        self.refresh()
        self.manifest['assets'][p.SERVER]['url'] = f'{p.BASE}/latest/download/{p.SERVER}'
        with self.assertRaisesRegex(ValueError, 'immutable release'):
            p.install(self.fetch)

    def test_unsafe_archive_cannot_write_outside_staging(self):
        with tarfile.open(self.archive, 'w:gz') as tar:
            item = tarfile.TarInfo('../../escaped')
            item.size = 3
            tar.addfile(item, io.BytesIO(b'bad'))
        self.refresh()
        with self.assertRaises(tarfile.FilterError):
            p.install(self.fetch)
        self.assertFalse((self.root / 't3').exists())
        self.assertFalse((self.root / 'cache/escaped').exists())

if __name__ == '__main__':
    unittest.main()
