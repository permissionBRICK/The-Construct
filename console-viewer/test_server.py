import asyncio
import importlib.util
from pathlib import Path
import time
import unittest
from aiohttp import ClientSession, CookieJar, WSMsgType, web
from aiohttp.test_utils import TestClient, TestServer

spec = importlib.util.spec_from_file_location('viewer', Path(__file__).with_name('server.py'))
viewer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(viewer)


class ProtocolTests(unittest.TestCase):
    def test_only_bounded_input_instructions_are_accepted(self):
        self.assertTrue(viewer.validate_input('3.key,5.65507,1.1;5.mouse,2.10,2.20,1.0;'))
        self.assertTrue(viewer.validate_input('4.sync,3.123;'))
        for message in ('6.select,3.rdp;', '7.connect,4.HOST;', '4.blob,1.1,3.abc;', '3.key,99999999.x;', '3.key', 'x.key;', '3.key,1.1,1.1;x'):
            self.assertFalse(viewer.validate_input(message), message)

    def test_config_is_data_not_shell(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            p = Path(directory) / 'config'
            p.write_text("A='hello world'\nB='$(do-not-run)'\n# comment\n")
            self.assertEqual(viewer.read_config(p), {'A': 'hello world', 'B': '$(do-not-run)'})


class GatewayTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.gateway = viewer.Gateway.__new__(viewer.Gateway)
        self.gateway.tickets = {'id': dict(token='secret-link', name='test-vm', expires=time.monotonic() + 60, active=False)}
        self.gateway.api_url = 'https://trusted-host:7462'
        self.gateway.config = {'CONSTRUCT_VMCONNECT_CERT_FINGERPRINT': 'sha256:trusted-fingerprint'}
        self.calls, self.params = [], {}
        self.ended = asyncio.Event()

        async def host_api(method, path):
            self.calls.append((method, path))
            if path.endswith('/connection'):
                return dict(username='vm-only-user', password='PRIVATE-HOST-PASSWORD', domain='HOST', vmId='native-id', certificateFingerprint='sha256:trusted-fingerprint')
            if path.endswith('/sessions'):
                return dict(sessionId='host-session')
            return {}
        self.gateway.api = host_api

        async def guacd(reader, writer):
            try:
                self.assertEqual(await viewer.read_instruction(reader), ['select', 'rdp'])
                names = ['VERSION_1_5_0', 'hostname', 'password', 'preconnection-blob', 'cert-fingerprints']
                writer.write(viewer.instruction('args', *names)); await writer.drain()
                for unused in range(4):
                    await viewer.read_instruction(reader)
                values = await viewer.read_instruction(reader)
                self.params.update(zip(names, values[1:]))
                writer.write(viewer.instruction('ready', 'connection-id'))
                writer.write(viewer.instruction('sync', '123'))
                await writer.drain()
                await reader.read()
            finally:
                writer.close()
                await writer.wait_closed()
                self.ended.set()
        self.daemon = await asyncio.start_server(guacd, '127.0.0.1', 0)
        self.gateway.guacd_host = '127.0.0.1'
        self.gateway.guacd_port = self.daemon.sockets[0].getsockname()[1]
        app = web.Application(middlewares=[viewer.headers])
        app.router.add_post('/redeem', self.gateway.redeem)
        app.router.add_get('/ws/{ident}', self.gateway.websocket)
        self.client = TestClient(TestServer(app), cookie_jar=CookieJar(unsafe=True))
        await self.client.start_server()
        self.origin = str(self.client.make_url('/')).rstrip('/')

    async def asyncTearDown(self):
        await self.client.close()
        self.daemon.close()
        await self.daemon.wait_closed()

    async def redeem(self):
        return await self.client.post('/redeem', headers={'Origin': self.origin}, json={'ticket': 'id.secret-link'})

    async def test_redeem_requires_ticket_and_same_origin(self):
        wrong_origin = await self.client.post('/redeem', headers={'Origin': 'https://attacker'}, json={'ticket': 'id.secret-link'})
        self.assertEqual(wrong_origin.status, 403)
        wrong_ticket = await self.client.post('/redeem', headers={'Origin': self.origin}, json={'ticket': 'id.wrong'})
        self.assertEqual(wrong_ticket.status, 410)
        result = await self.redeem()
        self.assertEqual(result.status, 200)
        self.assertNotIn('secret-link', await result.text())
        self.assertTrue(result.cookies['console-ticket']['httponly'])
        self.assertEqual(result.cookies['console-ticket']['path'], '/ws/id')
        self.assertEqual(self.calls, [])

    async def test_display_input_and_disconnect_keep_credentials_out_of_browser(self):
        await self.redeem()
        ws = await self.client.ws_connect('/ws/id', protocols=['guacamole'], headers={'Origin': self.origin})
        first = await ws.receive(timeout=2)
        display = await ws.receive(timeout=2)
        self.assertEqual(first.data, '0.,13.connection-id;')
        self.assertIn('sync', display.data)
        self.assertNotIn('PASSWORD', first.data + display.data)
        self.assertEqual(self.params['password'], 'PRIVATE-HOST-PASSWORD')
        self.assertEqual(self.params['hostname'], 'trusted-host')
        self.assertEqual(self.params['preconnection-blob'], 'native-id')
        await ws.send_str('0.,4.ping,3.123;')
        self.assertEqual((await ws.receive(timeout=2)).data, '0.,4.ping,3.123;')
        await ws.close()
        await asyncio.wait_for(self.ended.wait(), 2)
        await asyncio.sleep(.02)
        self.assertIn(('DELETE', '/api/v1/vms/test-vm/console/sessions/host-session'), self.calls)
        self.assertFalse(self.gateway.tickets['id']['active'])

    async def test_expired_ticket_cannot_connect_or_start_a_host_session(self):
        await self.redeem()
        self.gateway.tickets['id']['expires'] = time.monotonic() - 1
        response = await self.client.get('/ws/id', headers={'Origin': self.origin})
        self.assertEqual(response.status, 410)
        self.assertEqual(self.calls, [])

    async def test_hard_expiry_closes_an_existing_stream(self):
        await self.redeem()
        self.gateway.tickets['id']['expires'] = time.monotonic() + .1
        ws = await self.client.ws_connect('/ws/id', protocols=['guacamole'], headers={'Origin': self.origin})
        await ws.receive(timeout=2)
        await ws.receive(timeout=2)
        self.assertEqual((await ws.receive(timeout=2)).type, WSMsgType.CLOSE)
        self.assertIn(('DELETE', '/api/v1/vms/test-vm/console/sessions/host-session'), self.calls)

    async def test_protocol_configuration_injection_closes_the_connection(self):
        await self.redeem()
        ws = await self.client.ws_connect('/ws/id', protocols=['guacamole'], headers={'Origin': self.origin})
        await ws.receive(timeout=2)
        await ws.receive(timeout=2)
        await ws.send_str('6.select,3.ssh;')
        self.assertEqual((await ws.receive(timeout=2)).type, WSMsgType.CLOSE)


if __name__ == '__main__':
    unittest.main()
