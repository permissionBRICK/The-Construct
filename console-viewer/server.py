#!/usr/bin/env python3
"""VM-scoped browser console. The primary VM credential never reaches the browser."""
import asyncio
import codecs
import hmac
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import ssl
import time
from urllib.parse import quote, urlsplit
from aiohttp import web, ClientSession, ClientTimeout, WSMsgType

STATIC = Path(__file__).parent / 'static'


class HostApiError(RuntimeError):
    def __init__(self, status):
        self.status = status
        super().__init__(f'Host refused console operation (HTTP {status})')


def read_config(path):
    # Construct's config file contains shell-quoted values, not executable code.
    import shlex
    result = {}
    for line in Path(path).read_text().splitlines():
        if '=' not in line or line.lstrip().startswith('#'):
            continue
        key, value = line.split('=', 1)
        parts = shlex.split(value)
        if len(parts) == 1:
            result[key] = parts[0]
    return result


def instruction(*args):
    return (','.join(f'{len(str(arg))}.{arg}' for arg in args) + ';').encode()


async def read_instruction(reader):
    values = []
    while True:
        length = await reader.readuntil(b'.')
        if len(length) > 8 or not length[:-1].isdigit():
            raise ValueError('Invalid gateway response')
        size = int(length[:-1])
        if size > 65536:
            raise ValueError('Oversized gateway response')
        # Handshake arguments are ASCII; non-ASCII input is never accepted as configuration.
        value = (await reader.readexactly(size)).decode('ascii')
        values.append(value)
        separator = await reader.readexactly(1)
        if separator == b';':
            return values
        if separator != b',' or len(values) > 256:
            raise ValueError('Invalid gateway response')


def validate_input(text):
    """Only interactive input, acknowledgement, and keepalive instructions may reach guacd."""
    allowed = {'key', 'mouse', 'sync', 'size', 'ack', 'nop', 'disconnect'}
    pos = 0
    while pos < len(text):
        args = []
        while True:
            dot = text.find('.', pos)
            if dot < 0 or dot - pos > 7 or not text[pos:dot].isdigit():
                return False
            size = int(text[pos:dot])
            pos = dot + 1
            if size > 65536 or pos + size >= len(text):
                return False
            args.append(text[pos:pos + size])
            pos += size
            sep = text[pos]
            pos += 1
            if sep == ';':
                break
            if sep != ',' or len(args) > 12:
                return False
        if not args or args[0] not in allowed:
            return False
    return True


def validate_connection(value):
    if not isinstance(value, dict):
        raise web.HTTPBadRequest(text='Local console connection required')
    patterns = {
        'vmId': r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}',
        'username': r'[A-Za-z0-9_-]{1,20}',
        'domain': r'[A-Za-z0-9_.-]{1,255}',
        'password': r'[^\x00-\x1f\x7f]{1,256}',
        'certificateFingerprint': r'sha256:(?:[0-9a-f]{2}:){31}[0-9a-f]{2}',
    }
    for key, pattern in patterns.items():
        if not isinstance(value.get(key), str) or not re.fullmatch(pattern, value[key]):
            raise web.HTTPBadRequest(text='Invalid local console connection')
    try:
        ipaddress.IPv4Address(value.get('hostAddress', ''))
    except (ValueError, TypeError):
        raise web.HTTPBadRequest(text='Invalid console host address')
    return {key: value[key] for key in (*patterns, 'hostAddress')}


class Gateway:
    def __init__(self, config, token_path, guacd_host='127.0.0.1', guacd_port=4822):
        self.config = config
        self.token_path = Path(token_path)
        self.local = not config.get('CONSTRUCT_SERVICE_URL')
        self.api_url = self.api_ssl = None
        if not self.local:
            self.api_url = config['CONSTRUCT_SERVICE_URL'].rstrip('/')
            if urlsplit(self.api_url).scheme != 'https':
                raise ValueError('Host API requires HTTPS')
            self.api_ssl = ssl.create_default_context(cafile=config['CONSTRUCT_SERVICE_CA_FILE'])
        self.guacd_host, self.guacd_port = guacd_host, guacd_port
        self.tickets = {}
        self.http = None

    async def api(self, method, path):
        token = self.token_path.read_text().strip()
        scheme = self.config.get('CONSTRUCT_SERVICE_AUTH_SCHEME', 'VmToken')
        async with self.http.request(method, self.api_url + path, json={} if method == 'POST' else None,
                                     ssl=self.api_ssl, headers={'Authorization': f'{scheme} {token}'}) as response:
            if response.status >= 300:
                raise HostApiError(response.status)
            return await response.json() if response.status != 204 else None

    async def mint(self, request):
        body = await request.json()
        name, minutes = body.get('name', ''), body.get('minutes', 1440)
        if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9-]{0,62}', name) or type(minutes) is not int or not 5 <= minutes <= 1440:
            raise web.HTTPBadRequest(text='VM name and lifetime (5–1440 minutes) required')
        self.tickets = {k: v for k, v in self.tickets.items() if v['expires'] > time.monotonic() or v['active']}
        if len(self.tickets) >= 32:
            raise web.HTTPTooManyRequests(text='Too many viewer links')
        connection = None
        if self.local:
            connection = validate_connection(body.get('connection'))
        else:
            if 'connection' in body:
                raise web.HTTPBadRequest(text='Host-managed consoles do not accept local connections')
            capabilities = await self.api('GET', f'/api/v1/vms/{quote(name)}/console/capabilities')
            if capabilities.get('interactive') == 'unsupported':
                raise web.HTTPConflict(text=capabilities.get('interactiveReason', 'Browser console is unavailable on this host.'))
        ident, token = secrets.token_hex(16), secrets.token_urlsafe(32)
        self.tickets[ident] = dict(token=token, name=name, expires=time.monotonic() + minutes * 60, active=False, connection=connection)
        return web.json_response({'fragment': ident + '.' + token, 'minutes': minutes})

    async def redeem(self, request):
        if request.headers.get('Origin') != f'{request.scheme}://{request.host}':
            raise web.HTTPForbidden()
        data = await request.json()
        ident, _, token = str(data.get('ticket', '')).partition('.')
        ticket = self.tickets.get(ident)
        if not ticket or ticket['expires'] <= time.monotonic() or not hmac.compare_digest(ticket['token'], token):
            raise web.HTTPGone(text='This console link has expired. Create a new link with Construct.')
        response = web.json_response({'id': ident, 'name': ticket['name']})
        response.set_cookie('console-ticket', token, path='/ws/' + ident, httponly=True,
                            secure=request.secure, samesite='Strict', max_age=int(ticket['expires'] - time.monotonic()))
        return response

    async def websocket(self, request):
        ident = request.match_info['ident']
        ticket = self.tickets.get(ident)
        if request.headers.get('Origin') != f'{request.scheme}://{request.host}':
            raise web.HTTPForbidden()
        if not ticket or ticket['expires'] <= time.monotonic() or not hmac.compare_digest(ticket['token'], request.cookies.get('console-ticket', '')):
            raise web.HTTPGone()
        if ticket['active']:
            raise web.HTTPConflict(text='Console is already connected')
        ticket['active'] = True
        ticket['phase'], ticket['error'] = 'Opening browser connection', None
        ws = web.WebSocketResponse(protocols=['guacamole'], max_msg_size=65536, heartbeat=20)
        session_path, writer, tasks = None, None, []
        try:
            await ws.prepare(request)
            if ticket.get('connection'):
                connection = dict(ticket['connection'])
            else:
                root = f'/api/v1/vms/{quote(ticket["name"])}/console/sessions'
                ticket['phase'] = 'Creating a console session on the Windows host'
                session = await self.api('POST', root)
                session_path = root + '/' + session['sessionId']
                ticket['phase'] = 'Requesting console access from the host'
                connection = await self.api('POST', session_path + '/connection')
            ticket['phase'] = 'Connecting to the local console gateway'
            reader, writer = await asyncio.wait_for(asyncio.open_connection(self.guacd_host, self.guacd_port), 10)
            params = {
                'VERSION_1_5_0': 'VERSION_1_5_0', 'hostname': connection['hostAddress'] if self.local else urlsplit(self.api_url).hostname, 'port': '2179', 'security': 'vmconnect',
                'username': connection['username'], 'password': connection['password'], 'domain': connection['domain'],
                'preconnection-blob': connection['vmId'], 'ignore-cert': 'false',
                'cert-fingerprints': connection['certificateFingerprint'],
                'width': '1024', 'height': '768', 'dpi': '96', 'disable-audio': 'true',
                'enable-drive': 'false', 'disable-copy': 'true', 'disable-paste': 'true', 'read-only': 'false',
                'server-layout': self.config.get('CONSTRUCT_CONSOLE_KEYBOARD_LAYOUT', 'en-us-qwerty'),
            }
            if not params['cert-fingerprints']:
                raise RuntimeError('VMConnect certificate fingerprint is not configured')
            writer.write(instruction('select', 'rdp'))
            await writer.drain()
            args = await asyncio.wait_for(read_instruction(reader), 15)
            if not args or args[0] != 'args':
                raise RuntimeError('Gateway did not accept RDP')
            for values in [('size', '1024', '768', '96'), ('audio',), ('video',), ('image', 'image/png', 'image/jpeg')]:
                writer.write(instruction(*values))
            writer.write(instruction('connect', *(params.get(arg, '') for arg in args[1:])))
            await writer.drain()
            connection.clear()
            params.clear()
            ticket['phase'] = 'Connecting to Hyper-V VMConnect on this PC (port 2179)' if self.local else 'Connecting to Hyper-V VMConnect on the Windows host (port 2179)'
            ready = await asyncio.wait_for(read_instruction(reader), 30)
            if ready[0] != 'ready':
                raise RuntimeError('Hyper-V console connection failed')
            # Guacamole WebSocketTunnel expects its UUID as the first internal instruction.
            await ws.send_str(instruction('', ready[1]).decode())
            ticket['phase'] = 'Waiting for the guest display'

            async def receive_browser():
                async for message in ws:
                    if message.type != WSMsgType.TEXT:
                        return
                    if re.fullmatch(r'0\.,4\.ping,[0-9]+\.[0-9]+;', message.data):
                        await ws.send_str(message.data)
                        continue
                    if not validate_input(message.data):
                        return
                    writer.write(message.data.encode())
                    await writer.drain()

            async def receive_display():
                decoder = codecs.getincrementaldecoder('utf-8')()
                while chunk := await reader.read(65536):
                    text = decoder.decode(chunk)
                    if text:
                        await ws.send_str(text)

            async def renew():
                while True:
                    remaining = ticket['expires'] - time.monotonic()
                    if remaining <= 0:
                        return
                    await asyncio.sleep(min(20, remaining))
                    if ticket['expires'] <= time.monotonic():
                        return
                    if session_path:
                        await self.api('POST', session_path + '/renew')

            tasks = [asyncio.create_task(fn()) for fn in (receive_browser, receive_display, renew)]
            await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        except Exception as error:
            # Neither host credentials nor guacd error output enter logs or browser errors.
            reason = f'Host returned HTTP {error.status}' if isinstance(error, HostApiError) else 'Timed out' if isinstance(error, asyncio.TimeoutError) else 'Connection failed'
            ticket['error'] = f'{reason}: {ticket["phase"]}.'
            if ws.prepared and not ws.closed:
                await ws.send_str(instruction('error', ticket['error'], '519').decode())
        finally:
            async def cleanup():
                for task in tasks:
                    task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
                if writer:
                    writer.close()
                    try:
                        await asyncio.wait_for(writer.wait_closed(), 5)
                    except Exception:
                        pass
                if session_path:
                    try:
                        await self.api('DELETE', session_path)
                    except Exception:
                        pass  # Host expires and reaps the account independently.
                ticket['active'] = False
                await ws.close()
            # aiohttp may cancel this handler as soon as the browser closes its socket.
            # Account revocation must survive that cancellation.
            await asyncio.shield(cleanup())
        return ws

    async def status(self, request):
        # Same cookie path as the WebSocket. Status contains only fixed stage
        # descriptions, never host credentials, link secrets or raw exceptions.
        ticket = self.tickets.get(request.match_info['ident'])
        if not ticket or ticket['expires'] <= time.monotonic() or not hmac.compare_digest(ticket['token'], request.cookies.get('console-ticket', '')):
            raise web.HTTPGone(text='This console link has expired. Create a new link with Construct.')
        return web.json_response({'phase': ticket.get('phase', 'Opening browser connection'), 'error': ticket.get('error')})


@web.middleware
async def headers(request, handler):
    try:
        response = await handler(request)
    except web.HTTPException as exc:
        response = web.Response(status=exc.status, text=exc.text, headers=exc.headers)
    except Exception:
        response = web.Response(status=502, text='Console gateway or host is unavailable.')
    if not response.prepared:
        response.headers.update({'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer',
                                 'X-Content-Type-Options': 'nosniff',
                                 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'none'"})
    return response


async def main():
    config = read_config(os.environ.get('CONFIG_FILE', '/etc/construct/config.env'))
    extra = Path('/etc/construct/console-viewer.env')
    if extra.exists():
        config.update(read_config(extra))
    gateway = Gateway(config, config.get('CONSTRUCT_VM_TOKEN_FILE', '/etc/construct/vm-token'))
    async with ClientSession(timeout=ClientTimeout(total=25)) as gateway.http:
        app = web.Application(middlewares=[headers], client_max_size=4096)
        app.router.add_post('/redeem', gateway.redeem)
        app.router.add_get('/ws/{ident}', gateway.websocket)
        app.router.add_get('/ws/{ident}/status', gateway.status)
        app.router.add_get('/', lambda r: web.FileResponse(STATIC / 'index.html'))
        app.router.add_static('/static/', STATIC)
        control = web.Application(middlewares=[headers], client_max_size=4096)
        control.router.add_post('/tickets', gateway.mint)
        runner, local = web.AppRunner(app, access_log=None), web.AppRunner(control, access_log=None)
        await runner.setup()
        await local.setup()
        socket = Path('/run/construct-console/control.sock')
        socket.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        socket.unlink(missing_ok=True)
        await web.UnixSite(local, str(socket)).start()
        socket.chmod(0o600)
        tls = None
        if config.get('CONSTRUCT_CONSOLE_TLS_CERT'):
            tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            tls.load_cert_chain(config['CONSTRUCT_CONSOLE_TLS_CERT'], config['CONSTRUCT_CONSOLE_TLS_KEY'])
        await web.TCPSite(runner, '0.0.0.0', int(config.get('CONSTRUCT_CONSOLE_PORT', '6080')), ssl_context=tls).start()
        try:
            await asyncio.Event().wait()
        finally:
            await runner.cleanup()
            await local.cleanup()


if __name__ == '__main__':
    asyncio.run(main())
