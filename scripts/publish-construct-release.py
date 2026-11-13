#!/usr/bin/env python3
"""Publish complete immutable assets; call only under the workflow's shared lock."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.request


def api(path):
    request = urllib.request.Request('https://api.github.com/repos/' + os.environ['GITHUB_REPOSITORY'] + path,
                                    headers={'Authorization': 'Bearer ' + os.environ['GH_TOKEN'], 'User-Agent': 'Construct-release'})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise RuntimeError(f'Release metadata request failed: HTTP {error.code}') from None


def should_promote(candidate, latest, is_ancestor, main_head):
    if not latest or latest == candidate:
        return True
    if is_ancestor(candidate, latest):
        return False
    if is_ancestor(latest, candidate):
        return True
    # Rewritten history: only the current main tip may replace an unrelated release.
    return candidate == main_head


CHANNEL_TAG = 'channel-nightly'


def gh(*args):
    subprocess.run(['gh', 'release', *args], check=True)


def publish(output, channel='stable', notes_file=None):
    """Stable (main): the release becomes latest, as before the channels existed. Nightly: a
    prerelease, and its manifest replaces the one on the channel-nightly release."""
    commit = os.environ['GITHUB_SHA']
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid commit')
    manifest = json.loads((output / 'manifest.json').read_text())
    if manifest['commit'] != commit or manifest['repository'] != os.environ['GITHUB_REPOSITORY']:
        raise ValueError('Release identity mismatch')
    if manifest.get('channel', 'stable') != channel:
        raise ValueError('Manifest channel does not match the published channel')
    expected = {'payload': f'construct-host-{commit[:7]}-win-x64.zip',
                'frameworkDependent': f'construct-host-{commit[:7]}-win-x64-fdd.zip',
                'source': f'construct-source-{commit}.zip'}
    # The Linux service zip rides along when the package carries one (older packages have none).
    if 'linuxAsset' in manifest:
        expected['linux'] = f'construct-host-{commit[:7]}-linux-x64.zip'
    if manifest.get('releaseTag') != 'host-' + commit or any(manifest.get(key + 'Asset') != name for key, name in expected.items()):
        raise ValueError('Invalid release asset identity')
    if 'companionReleaseTag' in manifest and not re.fullmatch(r'companion-[0-9a-f]{40}', str(manifest['companionReleaseTag'])):
        raise ValueError('Invalid Companion release tag')
    assets = [output / name for name in ('manifest.json', *expected.values(), 'SHA256SUMS')]
    if not all(p.is_file() for p in assets):
        raise ValueError('Incomplete release')
    for key, name in expected.items():
        asset = output / name
        if asset.stat().st_size != manifest[key + 'SizeBytes'] or hashlib.sha256(asset.read_bytes()).hexdigest() != manifest[key + 'Sha256']:
            raise ValueError('Release asset checksum mismatch')
    tag = 'host-' + commit
    def known(sha):
        return subprocess.run(['git', 'cat-file', '-e', sha + '^{commit}'], capture_output=True).returncode == 0
    def ancestor(a, b):
        # A release commit the repository no longer contains (history was rewritten) is
        # unrelated to every current commit; only the branch tip may then replace it.
        if not (known(a) and known(b)):
            return False
        code = subprocess.run(['git', 'merge-base', '--is-ancestor', a, b]).returncode
        if code not in (0, 1):
            raise RuntimeError('Cannot establish release ancestry')
        return code == 0
    branch = 'main' if channel == 'stable' else os.environ['GITHUB_REF_NAME']
    branch_head = subprocess.check_output(['git', 'ls-remote', 'origin', 'refs/heads/' + branch], text=True).split()[0]
    if channel == 'stable':
        latest = api('/releases/latest')
        latest_tag = latest['tag_name'] if latest else ''
        current = latest_tag[5:] if re.fullmatch(r'host-[0-9a-f]{40}', latest_tag) else None
        if latest and current is None:
            raise ValueError('Latest release is not a Construct commit release; refusing to replace it')
    else:
        channel_release = api('/releases/tags/' + CHANNEL_TAG)
        current = None
        if channel_release and any(a['name'] == 'manifest.json' for a in channel_release['assets']):
            text = subprocess.check_output(['gh', 'release', 'download', CHANNEL_TAG, '-p', 'manifest.json', '-O', '-'], text=True)
            current = json.loads(text).get('commit')
    promote = should_promote(commit, current, ancestor, branch_head)
    existing = api('/releases/tags/' + tag)
    if existing and not existing['draft']:
        # Retries never overwrite already-published bits for a commit.
        names = {asset['name'] for asset in existing['assets']}
        if not all(p.name in names for p in assets):
            raise ValueError('Published release is incomplete; cannot rewrite an immutable release')
    else:
        if not existing:
            if channel == 'stable':
                gh('create', tag, '--draft', '--target', commit, '--title', 'Construct ' + commit[:7], '--notes',
                   'Source and Windows host artifacts from the same main commit. See docs/host-release.md.')
            else:
                gh('create', tag, '--draft', '--prerelease', '--target', commit,
                   '--title', f'Construct {manifest["version"].split("+")[0]} nightly {commit[:7]}',
                   *(['--notes-file', str(notes_file)] if notes_file else ['--notes', 'Nightly build.']))
        gh('upload', tag, '--clobber', *map(str, assets))
        gh('edit', tag, '--draft=false', '--latest=false')
    if promote and channel == 'stable':
        gh('edit', tag, '--latest')
        tag_version(commit, manifest)
    elif promote:
        if not channel_release:
            gh('create', CHANNEL_TAG, '--prerelease', '--latest=false', '--target', commit, '--title', 'Nightly channel',
               '--notes', 'manifest.json here is the newest nightly build. See docs/release-flow.md.')
        gh('upload', CHANNEL_TAG, '--clobber', str(output / 'manifest.json'))
    print(f'Published {tag}; channel={channel}; current={promote}')


def tag_version(commit, manifest):
    """A stable build cut by scripts/release.py (VERSION present) also gets the tag vX.Y.Z."""
    version = Path('VERSION').read_text().strip() if Path('VERSION').is_file() else None
    if not version or manifest.get('version') != f'{version}+{commit}' or api('/git/ref/tags/v' + version):
        return
    subprocess.run(['gh', 'api', '--method', 'POST', 'repos/' + os.environ['GITHUB_REPOSITORY'] + '/git/refs',
                    '-f', 'ref=refs/tags/v' + version, '-f', 'sha=' + commit], check=True, capture_output=True)
    gh('edit', 'host-' + commit, '--title', 'Construct v' + version)


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--channel', choices=['stable', 'nightly'], default='stable')
    parser.add_argument('--notes-file', type=Path)
    args = parser.parse_args()
    publish(args.output, args.channel, args.notes_file)
