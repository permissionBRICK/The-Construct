import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('viewer_open', Path(__file__).with_name('open.py'))
opener = importlib.util.module_from_spec(spec)
spec.loader.exec_module(opener)


class ForwardReuseTests(unittest.TestCase):
    def test_second_link_reuses_existing_console_forward_without_creating_another(self):
        output = 'ID PORT TARGET STATUS LABEL URL\na 6080 client open Guest console http://localhost:18816/\n'
        with patch.object(opener.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, output, '')) as run:
            self.assertEqual(opener.expose_viewer('6080'), 'http://localhost:18816/')
            self.assertEqual(run.call_count, 1)
            self.assertEqual(run.call_args.args[0], ['construct', 'expose', '--list'])

    def test_wrong_port_and_queued_forward_do_not_get_reused(self):
        output = 'a 6081 client open Guest console http://localhost:18816/\nb 6080 client queued Guest console -\n'
        with patch.object(opener.subprocess, 'run', side_effect=[subprocess.CompletedProcess([], 0, output, ''), subprocess.CompletedProcess([], 0, 'http://localhost:6080/\n', '')]) as run:
            self.assertEqual(opener.expose_viewer(6080), 'http://localhost:6080/')
            self.assertEqual(run.call_count, 2)

    def test_failed_forward_creation_is_reported(self):
        with patch.object(opener.subprocess, 'run', side_effect=[subprocess.CompletedProcess([], 0, '', ''), subprocess.CompletedProcess([], 6, '', 'No Construct client attached')]):
            with self.assertRaisesRegex(opener.ExposeError, 'No Construct client attached') as failure:
                opener.expose_viewer(6080)
            self.assertEqual(failure.exception.code, 6)


class LocalConnectionTests(unittest.TestCase):
    def test_connection_stdin_default_route_and_size_bound(self):
        import io
        with patch.object(opener.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, 'default via 172.20.0.1 dev eth0\n', '')):
            self.assertEqual(opener.read_connection(io.StringIO('{"hostAddress":"","password":"PRIVATE"}')),
                             {'hostAddress': '172.20.0.1', 'password': 'PRIVATE'})
        for data in ('[]', 'not json', 'x' * 4097):
            with self.assertRaises(ValueError): opener.read_connection(io.StringIO(data))


class LocalPostTests(unittest.IsolatedAsyncioTestCase):
    async def test_main_posts_connection_over_control_socket_without_printing_it(self):
        import io
        import json
        class Reply:
            status = 200
            async def __aenter__(self): return self
            async def __aexit__(self, *args): pass
            async def json(self): return {'fragment': 'fresh-ticket', 'minutes': 5}
        class Session:
            def __init__(self, **kwargs): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *args): pass
            def post(self, url, json):
                posted.update(json)
                return Reply()
        posted, output = {}, io.StringIO()
        with patch.object(opener, 'ClientSession', Session), patch.object(opener, 'UnixConnector'), patch.object(opener, 'expose_viewer', return_value='http://localhost:6080/'), patch.object(opener.sys, 'argv', ['open.py', 'primary', '--minutes', '5', '--connection-stdin']), patch.object(opener.sys, 'stdin', io.StringIO('{"hostAddress":"192.168.1.1","password":"PRIVATE"}')), patch.object(opener.sys, 'stdout', output):
            await opener.main()
        self.assertEqual(posted['connection']['password'], 'PRIVATE')
        self.assertEqual(posted['name'], 'primary')
        self.assertEqual(output.getvalue(), 'http://localhost:6080/#fresh-ticket\n')
