#!/usr/bin/env python3
"""Mint a console link via the root-only control socket and expose the viewer."""
import argparse
import json
import ipaddress
import asyncio
from pathlib import Path
import subprocess
import sys
from aiohttp import ClientSession, UnixConnector
from server import read_config


class ExposeError(RuntimeError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


def expose_viewer(port):
    # Reuse the advertised open client forward for this gateway. Repeated viewer
    # links must not consume the primary VM's finite forwarding allowance.
    listed = subprocess.run(['construct', 'expose', '--list'], text=True, capture_output=True)
    if listed.returncode == 0:
        for line in reversed(listed.stdout.splitlines()):
            fields = line.split()
            if len(fields) >= 6 and fields[1:4] == [str(port), 'client', 'open'] and fields[4:-1] == ['Guest', 'console']:
                if fields[-1].startswith(('http://', 'https://')):
                    return fields[-1]
    exposed = subprocess.run(['construct', 'expose', str(port), '--label', 'Guest console'], text=True, capture_output=True)
    if exposed.returncode:
        raise ExposeError(exposed.returncode, exposed.stderr or exposed.stdout or 'Construct could not expose the console')
    urls = [line.strip() for line in exposed.stdout.splitlines() if line.strip().startswith(('http://', 'https://'))]
    if len(urls) != 1:
        raise RuntimeError('Construct did not return one viewer URL')
    return urls[0]


def read_connection(stream):
    raw = stream.read(4097)
    if len(raw.encode('utf-8')) > 4096:
        raise ValueError('Console connection exceeds 4 KiB')
    try:
        connection = json.loads(raw)
        if not isinstance(connection, dict):
            raise ValueError()
        if not connection.get('hostAddress'):
            route = subprocess.run(['ip', '-4', 'route', 'show', 'default'], capture_output=True, text=True, check=True)
            fields = route.stdout.splitlines()[0].split()
            connection['hostAddress'] = str(ipaddress.IPv4Address(fields[fields.index('via') + 1]))
    except Exception:
        raise ValueError('Invalid console connection or missing default gateway') from None
    return connection


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('name')
    parser.add_argument('--minutes', type=int, default=1440, help='Link lifetime in minutes (5–1440; default: 1440 / 24 hours)')
    parser.add_argument('--connection-stdin', action='store_true')
    args = parser.parse_args()
    body = {'name': args.name, 'minutes': args.minutes}
    if args.connection_stdin:
        body['connection'] = read_connection(sys.stdin)
    config = read_config('/etc/construct/console-viewer.env') if Path('/etc/construct/console-viewer.env').exists() else {}
    port = config.get('CONSTRUCT_CONSOLE_PORT', '6080')
    async with ClientSession(connector=UnixConnector(path='/run/construct-console/control.sock')) as http:
        async with http.post('http://localhost/tickets', json=body) as response:
            if response.status != 200:
                raise RuntimeError(await response.text())
            ticket = await response.json()
    url = expose_viewer(port)
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
        sys.exit(error.code if isinstance(error, ExposeError) and 0 < error.code < 256 else 1)
