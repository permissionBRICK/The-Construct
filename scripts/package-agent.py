#!/usr/bin/env python3
"""Build the device agent's packages: Windows x64 (the tray app) and Linux x64 (the console host).

Each zip holds a self-contained publish with construct-agent(.exe) at its root and the UI bundle in
ui/. The release workflow runs this into the host release's output directory; package-construct-release.py
then lists both in the channel manifest with their SHA-256 (docs/release-flow.md).

    python3 scripts/package-agent.py --output DIR --commit SHA --version X.Y.Z [--ui-dist ui/dist]
"""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile

PACKAGES = {
    # manifest key: (runtime, project, executable)
    'agentWindows': ('win-x64', 'agent/src/Construct.Agent.Windows/Construct.Agent.Windows.csproj', 'construct-agent.exe'),
    'agentLinux': ('linux-x64', 'agent/src/construct-agent/construct-agent.csproj', 'construct-agent'),
}


def asset_name(key, commit):
    return f'construct-agent-{commit[:7]}-{PACKAGES[key][0]}.zip'


def zip_directory(source, target, executable):
    """A zip of SOURCE with stable order; the executable keeps its exec bit for the Linux unpackers."""
    with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(p for p in source.rglob('*') if p.is_file()):
            if path.is_symlink():
                raise ValueError('A package cannot contain links: ' + str(path))
            relative = path.relative_to(source).as_posix()
            info = zipfile.ZipInfo(relative, date_time=(2020, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o755 if relative == executable else 0o644) << 16
            archive.writestr(info, path.read_bytes())


def build(root, output, commit, version, ui_dist):
    if not re.fullmatch(r'[0-9a-f]{40}', commit) or not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Invalid commit or version')
    output.mkdir(parents=True, exist_ok=True)
    if ui_dist is None:
        subprocess.run(['npm', 'install', '--no-audit', '--no-fund'], cwd=root / 'ui', check=True)
        subprocess.run(['npm', 'run', 'build'], cwd=root / 'ui', check=True)
        ui_dist = root / 'ui' / 'dist'
    if not (ui_dist / 'index.html').is_file():
        raise ValueError('The UI bundle has no index.html: ' + str(ui_dist))
    with tempfile.TemporaryDirectory(prefix='construct-agent-package-') as work:
        for key, (runtime, project, executable) in PACKAGES.items():
            publish = Path(work) / runtime
            subprocess.run(['dotnet', 'publish', str(root / project), '-c', 'Release', '-r', runtime, '--self-contained', 'true',
                            f'-p:InformationalVersion={version}+{commit}', '-p:IncludeSourceRevisionInInformationalVersion=false',
                            '-p:EnableWindowsTargeting=true', '-o', str(publish)], check=True)
            if not (publish / executable).is_file():
                raise ValueError(f'{runtime} publish has no {executable}')
            shutil.copytree(ui_dist, publish / 'ui')
            zip_directory(publish, output / asset_name(key, commit), executable)
            print(output / asset_name(key, commit))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--version', required=True, help='X.Y.Z; the build reports X.Y.Z+<commit>')
    parser.add_argument('--ui-dist', type=Path, help='A built UI bundle; by default ui/ is built here')
    args = parser.parse_args()
    build(args.root, args.output, args.commit, args.version, args.ui_dist)
