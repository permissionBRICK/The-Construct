"""Exercise Desktop's real PowerShell helper and VS Code's generated pairing script.

SSH, the Construct forwarding CLI and T3 are process doubles. No host or live
T3 installation is contacted; all shell/config paths are isolated in a temp dir.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]


class PairingForwardTests(unittest.TestCase):
    def run_pairing(self, client, *, host_exit=0, client_exit=0, tls=True,
                    managed=True, client_url='http://user-pc:18807/', host_url='http://host.example:29991/'):
        with tempfile.TemporaryDirectory() as directory:
            tmp = Path(directory)
            config = tmp / 'config.env'
            config.write_text('T3CODE_PORT=5177\nT3CODE_HTTPS_PORT=5443\n' +
                              ('CONSTRUCT_SERVICE_URL=https://host.example:7462\n' if managed else '') +
                              ('T3CODE_PUBLIC_BASE_URL=https://host.example:5443\n' if tls else ''))
            log = tmp / 'calls.jsonl'
            bindir = tmp / 'bin'
            bindir.mkdir()
            scripts = {
                'construct': '''
                    import json, os, sys
                    args = sys.argv[1:]
                    with open(os.environ['PAIR_LOG'], 'a') as f: f.write(json.dumps(args) + '\\n')
                    target = args[args.index('--to') + 1]
                    code = int(os.environ[target.upper() + '_EXIT'])
                    if code == 0: print(os.environ[target.upper() + '_URL'])
                    sys.exit(code)
                ''',
                't3': '''
                    import json, sys
                    if '--help' in sys.argv: print('--scopes'); sys.exit(0)
                    base = sys.argv[sys.argv.index('--base-url') + 1]
                    print(json.dumps({'pairUrl': base + '/pair#token=TEST-PAIRING'}))
                ''',
                'ssh.exe': '''
                    import base64, os, re, subprocess, sys
                    encoded = re.search("printf %s '([^']+)'", sys.argv[-1]).group(1)
                    script = base64.b64decode(encoded).decode().replace('CONFIG_FILE=/etc/construct/config.env', 'CONFIG_FILE=' + os.environ['PAIR_CONFIG'])
                    result = subprocess.run(['bash', '-c', script])
                    sys.exit(result.returncode)
                ''',
            }
            for name, body in scripts.items():
                path = bindir / name
                path.write_text('#!/usr/bin/env python3\n' + textwrap.dedent(body))
                path.chmod(0o755)
            env = dict(os.environ, PATH=str(bindir) + os.pathsep + os.environ['PATH'],
                       PAIR_CONFIG=str(config), PAIR_LOG=str(log), HOST_EXIT=str(host_exit), CLIENT_EXIT=str(client_exit),
                       HOST_URL=host_url, CLIENT_URL=client_url)
            if client == 'desktop':
                self.assertIsNotNone(shutil.which('pwsh'), 'CI must provide PowerShell')
                command = ['pwsh', '-NoProfile', '-File', str(ROOT / 'Get-ConstructT3PairingLink.ps1')]
                result = subprocess.run(command, env=env, text=True, capture_output=True, timeout=20)
            else:
                script = subprocess.check_output(['node', '-e', 'process.stdout.write(require("./extension/src/t3code").buildPairingScript())'], cwd=ROOT, text=True)
                script = script.replace('CONFIG_FILE=/etc/construct/config.env', 'CONFIG_FILE=' + str(config))
                result = subprocess.run(['bash', '-c', script], env=env, text=True, capture_output=True, timeout=20)
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            return result, calls

    def test_host_allowed_uses_actual_allocated_port(self):
        for client in ('desktop', 'extension'):
            with self.subTest(client=client):
                result, calls = self.run_pairing(client)
                self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
                self.assertEqual(json.loads(result.stdout)['pairUrl'], 'https://host.example:29991/pair#token=TEST-PAIRING')
                self.assertEqual(len(calls), 1)
                self.assertEqual(calls[0][1], '5443')
                self.assertIn('--reuse', calls[0])

    def test_denied_host_uses_actual_client_port_and_tls_localhost(self):
        for client in ('desktop', 'extension'):
            with self.subTest(client=client):
                result, calls = self.run_pairing(client, host_exit=7)
                self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
                self.assertEqual(json.loads(result.stdout)['pairUrl'], 'https://localhost:18807/pair#token=TEST-PAIRING')
                self.assertEqual([call[call.index('--to') + 1] for call in calls], ['host', 'client'])
                self.assertIn('--wait', calls[1])
                self.assertIn('--reuse', calls[1])
                if client == 'desktop': self.assertEqual(json.loads(result.stdout)['scopes'], 'administrative')

    def test_plain_listener_falls_back_without_inventing_tls(self):
        for client in ('desktop', 'extension'):
            with self.subTest(client=client):
                result, calls = self.run_pairing(client, host_exit=7, tls=False)
                self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
                self.assertEqual(json.loads(result.stdout)['pairUrl'], 'http://localhost:18807/pair#token=TEST-PAIRING')
                self.assertTrue(all(call[1] == '5177' for call in calls))

    def test_missing_client_and_invalid_endpoints_never_mint_a_dead_link(self):
        for client in ('desktop', 'extension'):
            for values in ({'host_exit': 7, 'client_exit': 5}, {'host_exit': 7, 'client_url': 'http://pc:70000/'},
                           {'host_exit': 7, 'client_url': 'http://pc:18807/evil'}, {'host_exit': 6}):
                with self.subTest(client=client, values=values):
                    result, calls = self.run_pairing(client, **values)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertNotIn('TEST-PAIRING', result.stdout)
                    if values.get('host_exit') == 6: self.assertEqual(len(calls), 1)

    def test_local_instance_keeps_its_advertised_origin_without_forwarding(self):
        for client in ('desktop', 'extension'):
            with self.subTest(client=client):
                result, calls = self.run_pairing(client, managed=False)
                self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
                self.assertEqual(json.loads(result.stdout)['pairUrl'], 'https://host.example:5443/pair#token=TEST-PAIRING')
                self.assertEqual(calls, [])


if __name__ == '__main__':
    unittest.main()
