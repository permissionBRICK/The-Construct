#!/usr/bin/env python3
"""Mint a console link via the root-only control socket and expose the viewer."""
import argparse
import asyncio
from pathlib import Path
import subprocess
import sys
from aiohttp import ClientSession, UnixConnector
from server import read_config


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('name')
    parser.add_argument('--minutes', type=int, default=30)
    args = parser.parse_args()
    config = read_config('/etc/construct/console-viewer.env') if Path('/etc/construct/console-viewer.env').exists() else {}
    port = config.get('CONSTRUCT_CONSOLE_PORT', '6080')
    async with ClientSession(connector=UnixConnector(path='/run/construct-console/control.sock')) as http:
        async with http.post('http://localhost/tickets', json={'name': args.name, 'minutes': args.minutes}) as response:
            if response.status != 200:
                raise RuntimeError(await response.text())
            ticket = await response.json()
    exposed = subprocess.run(['construct', 'expose', port, '--label', 'Guest console'], text=True, capture_output=True)
    if exposed.returncode:
        sys.stderr.write(exposed.stderr or exposed.stdout)
        return exposed.returncode
    urls = [line.strip() for line in exposed.stdout.splitlines() if line.strip().startswith(('http://', 'https://'))]
    if len(urls) != 1:
        raise RuntimeError('Construct did not return one viewer URL')
    url = urls[0]
    if config.get('CONSTRUCT_CONSOLE_TLS_CERT'):
        url = url.replace('http://', 'https://', 1)
    print(url.rstrip('/') + '/#' + ticket['fragment'])
    print(f'Console link expires in {ticket["minutes"]} minutes. Anyone with this link can control this VM.', file=sys.stderr)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(asyncio.run(main()))
    except Exception as error:
        print(f'construct vm console: {error}', file=sys.stderr)
        sys.exit(1)
