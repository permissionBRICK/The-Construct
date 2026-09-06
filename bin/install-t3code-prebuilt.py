#!/usr/bin/env python3
"""Install the last validated pair for the selected channel without compilers or a service restart."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request

REPOSITORY = 'permissionBRICK/construct-t3-builds'
BASE = f'https://github.com/{REPOSITORY}/releases'
SERVER = 't3code-server-linux-x64.tar.gz'
DESKTOP = 'T3Code-Construct-Setup.exe'


def read_manifest(manifest, channel="stable"):
    if manifest.get('formatVersion') != 1 or manifest.get('channel') != channel:
        raise ValueError('Unsupported prebuilt manifest format/channel')
    pattern = r'\d+\.\d+\.\d+-nightly\.\d+\.\d+' if channel == 'nightly' else r'\d+\.\d+\.\d+'
    if not re.fullmatch(pattern, manifest.get('version', '')):
        raise ValueError('Invalid channel version')
    for key in ('buildHash', 'patchHash'):
        if not re.fullmatch(r'[a-f0-9]{64}', manifest.get(key, '')):
            raise ValueError(f'Invalid {key}')
    tag = f't3-{manifest["version"]}-{manifest["buildHash"]}'
    if manifest.get('releaseTag') != tag or manifest.get('target') != 'ubuntu-24.04-linux-x64+windows-x64':
        raise ValueError('Unsupported release identity/target')
    for name in (SERVER, DESKTOP):
        asset = manifest['assets'][name]
        if asset.get('url') != f'{BASE}/download/{tag}/{name}':
            raise ValueError('Asset URL does not identify the matched immutable release')
        if not re.fullmatch(r'[a-f0-9]{64}', asset.get('sha256', '')) or not isinstance(asset.get('size'), int) or asset['size'] <= 0:
            raise ValueError('Invalid asset checksum/size')
    if manifest.get('sha256') != manifest['assets'][DESKTOP]['sha256'] or manifest.get('downloadUrl') != manifest['assets'][DESKTOP]['url']:
        raise ValueError('Desktop handoff does not match the paired release')
    return manifest


def download(url, path):
    request = urllib.request.Request(url, headers={'User-Agent': 'The-Construct'})
    with urllib.request.urlopen(request, timeout=120) as response, open(path, 'wb') as output:
        shutil.copyfileobj(response, output)


def atomic_json(path, value):
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(value, indent=2) + '\n')
    temporary.replace(path)


def manifest_url(channel, fetch, temporary):
    if channel == 'stable':
        return f'{BASE}/latest/download/manifest.json'
    # GitHub's latest endpoint excludes prereleases. Follow a complete nightly
    # release and then its immutable URLs, never a mixture of channel assets.
    page = 1
    while True:
        listing = temporary / 'releases.json'
        fetch(f'https://api.github.com/repos/{REPOSITORY}/releases?per_page=100&page={page}', listing)
        releases = json.loads(listing.read_text())
        candidates = [r for r in releases if not r['draft'] and r['prerelease']
                      and re.fullmatch(r't3-\d+\.\d+\.\d+-nightly\.\d+\.\d+-[a-f0-9]{64}', r['tag_name'])
                      and {SERVER, DESKTOP, 'manifest.json', 'SHA256SUMS'} <= {a['name'] for a in r['assets']}]
        if candidates:
            latest = max(candidates, key=lambda r: r['published_at'])
            return f'{BASE}/download/{latest["tag_name"]}/manifest.json'
        if len(releases) < 100:
            raise ValueError('No validated nightly T3 pair has been published')
        page += 1


def install(fetch=download):
    channel = os.environ.get('T3CODE_CHANNEL', 'stable')
    if channel not in ('stable', 'nightly'):
        raise ValueError('Invalid T3CODE_CHANNEL')
    if platform.machine() != 'x86_64' or platform.system() != 'Linux':
        raise ValueError('Prebuilt T3 requires Linux x64; select T3CODE_BUILD_SOURCE=local on other targets')
    cache = Path(os.environ.get('T3CODE_PREBUILT_CACHE', '/var/cache/construct/t3code-prebuilt'))
    artifacts = Path(os.environ.get('T3CODE_ARTIFACT_ROOT', '/var/lib/construct/t3code-desktop'))
    launcher = Path(os.environ.get('T3CODE_LAUNCHER', '/usr/local/bin/t3'))
    status = Path(os.environ.get('T3CODE_STATUS_PATH', '/etc/construct/t3code-desktop-status'))
    for directory in (cache, artifacts, launcher.parent, status.parent):
        directory.mkdir(parents=True, exist_ok=True)
    with (cache / '.install.lock').open('w') as lock, tempfile.TemporaryDirectory(prefix='.download-', dir=cache) as scratch:
        fcntl.flock(lock, fcntl.LOCK_EX)
        temporary = Path(scratch)
        fetch(manifest_url(channel, fetch, temporary), temporary / 'manifest.json')
        manifest = read_manifest(json.loads((temporary / 'manifest.json').read_text()), channel)
        destination = cache / manifest['buildHash']
        receipt = destination / '.construct-prebuilt-manifest.json'
        current = False
        if receipt.exists() and (destination / 'bin/t3').is_file() and (destination / 'bin/node').is_file():
            try:
                previous = json.loads(receipt.read_text())
                current = previous['buildHash'] == manifest['buildHash'] and previous['assets'][SERVER] == manifest['assets'][SERVER]
            except (ValueError, KeyError):
                pass
        if not current:
            asset = manifest['assets'][SERVER]
            print(f'Downloading prebuilt patched T3 {manifest["version"]} ({asset["size"] // 1048576} MiB)...', flush=True)
            archive = temporary / SERVER
            fetch(asset['url'], archive)
            with archive.open('rb') as f:
                digest = hashlib.file_digest(f, 'sha256').hexdigest()
            if digest != asset['sha256'] or archive.stat().st_size != asset['size']:
                raise ValueError('Prebuilt server checksum/size mismatch; previous install kept')
            staged = temporary / 'runtime'
            staged.mkdir()
            with tarfile.open(archive, 'r:gz') as package:
                package.extractall(staged, filter='data')
            subprocess.run([staged / 'bin/t3', '--help'], check=True, stdout=subprocess.DEVNULL, timeout=30)
            atomic_json(staged / '.construct-prebuilt-manifest.json', manifest)
            if destination.exists():
                # Never replace files a running process may still have loaded.
                raise ValueError('Incomplete existing prebuilt runtime; remove its inactive cache entry and retry')
            staged.replace(destination)
        else:
            print(f'Prebuilt T3 {manifest["version"]} is cached; skipping download and extraction.', flush=True)
        subprocess.run([destination / 'bin/t3', '--help'], check=True, stdout=subprocess.DEVNULL, timeout=30)
        manifest['installationMode'] = 'prebuilt'
        atomic_json(artifacts / 'server-manifest.json', manifest)
        atomic_json(artifacts / 'manifest.json', manifest)
        pending = launcher.with_name(launcher.name + '.prebuilt-tmp')
        pending.unlink(missing_ok=True)
        pending.symlink_to(destination / 'bin/t3')
        pending.replace(launcher)
        pending_status = status.with_name(status.name + '.tmp')
        pending_status.write_text(f'T3CODE_SERVER_READY=yes\nT3CODE_DESKTOP_READY=yes\n'
                                  f'T3CODE_DESKTOP_VERSION={manifest["version"]}\nT3CODE_DESKTOP_CHANNEL={channel}\n'
                                  f'T3CODE_BUILD_KEY={manifest["buildHash"]}\nT3CODE_INSTALLATION_MODE=prebuilt\n')
        pending_status.replace(status)
    print('Prebuilt server and matching Windows download are ready.')


if __name__ == '__main__':
    try:
        install()
    except Exception as error:
        raise SystemExit(f'Prebuilt T3 installation failed: {error}. No local build was started. '
                         'Retry later or explicitly select T3CODE_BUILD_SOURCE=local.')
