#!/usr/bin/env python3
"""Add the pinned source archive to an already-built host release (no tests)."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile


def package(root, output, commit, repository):
    if not re.fullmatch(r'[0-9a-f]{40}', commit) or not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Invalid release identity')
    if subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip() != commit:
        raise ValueError('Release must package the checked-out commit')
    manifest_path = output / 'manifest.json'
    manifest = json.loads(manifest_path.read_text())
    if manifest['commit'] != commit or manifest['releaseTag'] != 'host-' + commit or manifest['repository'] != repository:
        raise ValueError('Host package identity does not match source')
    for prefix, expected in [('payload', f'construct-host-{commit[:7]}-win-x64.zip'),
                             ('frameworkDependent', f'construct-host-{commit[:7]}-win-x64-fdd.zip')]:
        if manifest[prefix + 'Asset'] != expected:
            raise ValueError('Invalid host asset identity')
        asset = output / expected
        if asset.stat().st_size != manifest[prefix + 'SizeBytes'] or hashlib.sha256(asset.read_bytes()).hexdigest() != manifest[prefix + 'Sha256']:
            raise ValueError('Host asset checksum mismatch')
    source = output / f'construct-source-{commit}.zip'
    prefix = repository.split('/')[1] + '-main/'
    subprocess.run(['git', '-C', str(root), 'archive', '--format=zip', '--prefix=' + prefix,
                    '-o', str(source.resolve()), commit], check=True)
    # A local marker records what was actually extracted, never a later HEAD lookup.
    with zipfile.ZipFile(source, 'a', compression=zipfile.ZIP_DEFLATED) as archive:
        if prefix + '.construct-revision' not in archive.namelist():
            archive.writestr(prefix + '.construct-revision', commit + '\n')
        elif archive.read(prefix + '.construct-revision').decode().strip() != commit:
            raise ValueError('Source revision was not expanded by git archive')
    with source.open('rb') as stream:
        source_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
    manifest.update(sourceAsset=source.name, sourceSha256=source_hash,
                    sourceSizeBytes=source.stat().st_size,
                    payloadSizeBytes=(output / manifest['payloadAsset']).stat().st_size)
    with (output / 'SHA256SUMS').open('a') as sums:
        sums.write(f'{source_hash}  {source.name}\n')
    manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path('.'))
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--repository', required=True)
    args = parser.parse_args()
    package(args.root, args.output, args.commit, args.repository)
