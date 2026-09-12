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


def publish(output):
    commit = os.environ['GITHUB_SHA']
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid commit')
    manifest = json.loads((output / 'manifest.json').read_text())
    if manifest['commit'] != commit or manifest['repository'] != os.environ['GITHUB_REPOSITORY']:
        raise ValueError('Release identity mismatch')
    expected = {'payload': f'construct-host-{commit[:7]}-win-x64.zip',
                'frameworkDependent': f'construct-host-{commit[:7]}-win-x64-fdd.zip',
                'source': f'construct-source-{commit}.zip'}
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
    latest = api('/releases/latest')
    latest_tag = latest['tag_name'] if latest else ''
    latest_commit = latest_tag[5:] if re.fullmatch(r'host-[0-9a-f]{40}', latest_tag) else None
    if latest and latest_commit is None:
        raise ValueError('Latest release is not a Construct commit release; refusing to replace it')
    def ancestor(a, b):
        code = subprocess.run(['git', 'merge-base', '--is-ancestor', a, b]).returncode
        if code not in (0, 1):
            raise RuntimeError('Cannot establish release ancestry')
        return code == 0
    main_head = subprocess.check_output(['git', 'ls-remote', 'origin', 'refs/heads/main'], text=True).split()[0]
    promote = should_promote(commit, latest_commit, ancestor, main_head)
    existing = api('/releases/tags/' + tag)
    if existing and not existing['draft']:
        # Retries never overwrite already-published bits for a commit.
        names = {asset['name'] for asset in existing['assets']}
        if not all(p.name in names for p in assets):
            raise ValueError('Published release is incomplete; cannot rewrite an immutable release')
    else:
        if not existing:
            subprocess.run(['gh', 'release', 'create', tag, '--draft', '--target', commit,
                            '--title', 'Construct ' + commit[:7], '--notes',
                            'Source and Windows host artifacts from the same main commit. See docs/host-release.md.'], check=True)
        subprocess.run(['gh', 'release', 'upload', tag, '--clobber', *map(str, assets)], check=True)
        subprocess.run(['gh', 'release', 'edit', tag, '--draft=false', '--latest=false'], check=True)
    if promote:
        subprocess.run(['gh', 'release', 'edit', tag, '--latest'], check=True)
    print(f'Published {tag}; latest={promote}')


if __name__ == '__main__':
    import sys
    publish(Path(sys.argv[1]))
