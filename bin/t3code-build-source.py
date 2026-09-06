#!/usr/bin/env python3
"""Resolve a public build checkout; keep server and deferred desktop on one commit."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

REPOSITORY = 'https://github.com/permissionBRICK/construct-t3-builds.git'


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def resolve_source(installed=False):
    artifacts = Path(os.environ.get('T3CODE_ARTIFACT_ROOT', '/var/lib/construct/t3code-desktop'))
    manifest_path = artifacts / 'server-manifest.json'
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
    if installed and manifest.get('installationMode') == 'prebuilt':
        cache = Path(os.environ.get('T3CODE_PREBUILT_CACHE', '/var/cache/construct/t3code-prebuilt'))
        build = manifest.get('buildHash', '')
        if not re.fullmatch(r'[a-f0-9]{64}', build):
            raise ValueError('Invalid installed build identity')
        runtime = cache / build
        if (runtime / 'extension/vm/construct-t3park-patch.mjs').is_file():
            return runtime
    cache = Path(os.environ.get('T3CODE_BUILD_REPO_CACHE', '/var/cache/construct/t3code-builds'))
    cache.mkdir(parents=True, exist_ok=True)
    with (cache / '.fetch.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        pinned = installed or os.environ.get('T3CODE_BUILD_MODE') == 'desktop'
        commit = manifest.get('buildRepositoryCommit') if pinned else None
        if os.environ.get('T3CODE_BUILD_MODE') == 'desktop' and not commit:
            raise ValueError('Prepared server has no build repository commit; provision it again first')
        repository = os.environ.get('T3CODE_BUILD_REPOSITORY', REPOSITORY)
        if not commit:
            commit = run('git', 'ls-remote', repository, 'refs/heads/main').split()[0]
        if not re.fullmatch(r'[a-f0-9]{40}', commit):
            raise ValueError('Invalid build repository commit')
        destination = cache / commit
        if not destination.exists():
            with tempfile.TemporaryDirectory(prefix='.fetch-', dir=cache) as temporary:
                checkout = Path(temporary) / 'source'
                run('git', 'init', '--quiet', str(checkout))
                run('git', '-C', str(checkout), '-c', 'core.autocrlf=false', 'fetch', '--quiet', '--depth=1', repository, commit)
                run('git', '-C', str(checkout), '-c', 'core.autocrlf=false', 'checkout', '--quiet', '--detach', 'FETCH_HEAD')
                if run('git', '-C', str(checkout), 'rev-parse', 'HEAD') != commit:
                    raise ValueError('Fetched build commit does not match requested commit')
                checkout.rename(destination)
        return destination


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--installed', action='store_true')
    args = parser.parse_args()
    print(resolve_source(args.installed))
