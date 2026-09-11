#!/usr/bin/env python3
"""Real Git + PowerShell askpass against a local TLS/Basic-auth git-http-backend.
No external network, host credential stores or surviving child processes.
"""
import base64
import http.server
import os
from pathlib import Path
import subprocess
import ssl
import socket
import select
import tempfile
import time
import threading
import urllib.parse

ROOT = Path(__file__).resolve().parents[1]
TOKEN = 'native-fixture:/@ secret'
AUTH = 'Basic ' + base64.b64encode(('fixture-user:' + TOKEN).encode()).decode()
PROXY_TOKEN = 'proxy-fixture:/@ secret'
PROXY_AUTH = 'Basic ' + base64.b64encode(('proxy-user:' + PROXY_TOKEN).encode()).decode()

with tempfile.TemporaryDirectory(prefix='construct-git-http-') as tmp:
    path = Path(tmp)
    subprocess.run(['git', 'init', '-q', '--bare', '--initial-branch=main', str(path / 'config.git')], check=True)

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            self.handle_git()

        def do_POST(self):
            self.handle_git()

        def handle_git(self):
            if self.path.startswith('/slow.git'):
                time.sleep(3)
                self.send_response(503)
                self.send_header('Content-Length', '0')
                self.end_headers()
                return
            if self.headers.get('Authorization') != AUTH:
                self.send_response(401)
                self.send_header('WWW-Authenticate', 'Basic realm="fixture"')
                self.send_header('Content-Length', '0')
                self.end_headers()
                return
            url = urllib.parse.urlsplit(self.path)
            body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
            env = dict(os.environ, GIT_PROJECT_ROOT=tmp, GIT_HTTP_EXPORT_ALL='1',
                       PATH_INFO=url.path, QUERY_STRING=url.query, REQUEST_METHOD=self.command,
                       CONTENT_TYPE=self.headers.get('Content-Type', ''), CONTENT_LENGTH=str(len(body)),
                       REMOTE_USER='fixture-user')
            result = subprocess.run(['git', 'http-backend'], input=body, env=env, capture_output=True, check=True)
            headers, data = result.stdout.split(b'\r\n\r\n', 1)
            self.send_response(200)
            for line in headers.decode().split('\r\n'):
                key, value = line.split(':', 1)
                self.send_header(key, value.strip())
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    cert, key = path / 'cert.pem', path / 'key.pem'
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                    '-keyout', str(key), '-out', str(cert), '-subj', '/CN=127.0.0.1',
                    '-addext', 'subjectAltName=IP:127.0.0.1'], capture_output=True, check=True)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(cert, key)
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    proxy_requests = []
    private_configs = []
    temp_root = path / 'temp'
    temp_root.mkdir()

    class ProxyHandler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_CONNECT(self):
            if self.headers.get('Proxy-Authorization') != PROXY_AUTH:
                self.send_response(407)
                self.send_header('Proxy-Authenticate', 'Basic realm="proxy-fixture"')
                self.send_header('Content-Length', '0')
                self.end_headers()
                return
            proxy_requests.append(True)
            for config_file in temp_root.glob('construct-git-*/gitconfig'):
                if 'proxy = ' in config_file.read_text():
                    private_configs.append(config_file.stat().st_mode & 0o777 == 0o600)
            assert self.path == f'127.0.0.1:{server.server_port}'
            with socket.create_connection(('127.0.0.1', server.server_port), timeout=5) as upstream:
                self.send_response(200)
                self.end_headers()
                upstream.settimeout(None)
                peers = [self.connection, upstream]
                while True:
                    ready, _, _ = select.select(peers, [], [], 10)
                    if not ready:
                        return
                    for source in ready:
                        data = source.recv(65536)
                        if not data:
                            return
                        target = upstream if source is self.connection else self.connection
                        target.sendall(data)

    proxy_server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), ProxyHandler)
    proxy_thread = threading.Thread(target=proxy_server.serve_forever)
    proxy_thread.start()
    thread = threading.Thread(target=server.serve_forever)
    thread.start()
    try:
        script = path / 'test.ps1'
        script.write_text(r'''
$ErrorActionPreference = 'Stop'
. (Join-Path $env:FIXTURE_REPO 'lib/AgentVm.Common.ps1')
function Write-Note($message) { }
function Write-Ok($message) { }
$cred = @{ User = 'fixture-user'; Token = $env:FIXTURE_TOKEN }
$url = $env:FIXTURE_URL
$r = Invoke-ConstructCredentialGit -Arguments @('ls-remote', '--', $url)
if ($r.ExitCode -eq 0) { throw 'Anonymous probe unexpectedly authenticated' }
$r = Invoke-ConstructCredentialGit -Arguments @('ls-remote', '--', $url) -Credential $cred
if ($r.ExitCode -ne 0) { throw ('Native askpass did not authenticate: ' + $r.Stderr) }
$bad = @{ User = 'fixture-user'; Token = 'incorrect-fixture' }
$r = Invoke-ConstructCredentialGit -Arguments @('ls-remote', '--', $url) -Credential $bad
if ($r.ExitCode -eq 0 -or $r.Stderr -notmatch 'Authentication failed') { throw 'Wrong credential was not rejected clearly' }
$line = ([uri]$url).Scheme + '://fixture-user:' + [uri]::EscapeDataString($cred.Token) + '@' + ([uri]$url).Authority
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line))
$script:ConstructGitCredentialSession = New-ConstructGitCredentialSession -CredentialsB64 $b64
$clone = Update-ConstructStagingClone -SourceRepo $url
if (-not (Test-Path (Join-Path $clone '.git'))) { throw 'Authenticated config clone failed' }
# Empty repositories have no origin/main yet; create a commit locally so fetch/reset
# can be exercised against the fixture backend without adding write support to HTTP.
& git -C $clone -c user.name=fixture -c user.email=fixture@example.com commit --allow-empty -qm fixture
& git -C $clone push -q $env:FIXTURE_BARE HEAD:main
$clone2 = Update-ConstructStagingClone -SourceRepo $url
if ($clone2 -ne $clone) { throw 'Authenticated config fetch failed' }
$configText = Get-Content (Join-Path $clone '.git/config') -Raw
if ($configText.Contains($cred.Token) -or $configText.Contains([uri]::EscapeDataString($cred.Token))) { throw 'Credential persisted in clone configuration' }
if (Test-Path $env:FIXTURE_HELPER_MARKER) { throw 'Inherited credential helper was invoked' }
if (Test-Path $env:GIT_TRACE) { throw 'Inherited trace destination was used' }
$slow = $url.Replace('/config.git', '/slow.git')
$watch = [Diagnostics.Stopwatch]::StartNew()
$r = Invoke-ConstructCredentialGit -Arguments @('ls-remote', '--', $slow) -TimeoutSeconds 1
if ($r.ExitCode -ne 124 -or $watch.Elapsed.TotalSeconds -gt 2.5) { throw 'Probe timeout did not bound execution' }
Write-Output '9 passed: configured TLS trust with scoped auth header excluded, bounded timeout, real anonymous probe, askpass, rejection, clone, fetch, no persisted token, no inherited helper/trace'
''')
        # A poisoned helper would reveal that the inherited config was consulted.
        config = path / 'global.gitconfig'
        marker = path / 'helper-ran'
        config.write_text('[credential]\n\thelper = "!touch ' + str(marker) + '; exit 1"\n'
                          '[http "https://127.0.0.1:' + str(server.server_port) + '"]\n'
                          '\tsslCAInfo = ' + str(cert) + '\n'
                          '\textraHeader = Authorization: Basic wrong-fixture\n'
                          '\tproxy = http://proxy-user:' + urllib.parse.quote(PROXY_TOKEN, safe='') +
                          '@127.0.0.1:' + str(proxy_server.server_port) + '\n'
                          '\tproxyAuthMethod = basic\n'
                          '\tversion = HTTP/1.1\n')
        env = dict(os.environ, no_proxy='', NO_PROXY='', TMPDIR=str(temp_root), FIXTURE_REPO=str(ROOT), FIXTURE_TOKEN=TOKEN,
                   FIXTURE_URL=f'https://127.0.0.1:{server.server_port}/config.git',
                   FIXTURE_BARE=str(path / 'config.git'), LOCALAPPDATA=str(path / 'home'),
                   FIXTURE_HELPER_MARKER=str(marker), GIT_CONFIG_GLOBAL=str(config))
        # Test trace isolation without tracing the fixture's unrelated direct git commands.
        script.write_text(script.read_text().replace('$r = Invoke-ConstructCredentialGit -Arguments',
            "$env:GIT_TRACE = (Join-Path $env:LOCALAPPDATA 'trace.log')\n$r = Invoke-ConstructCredentialGit -Arguments", 1)
            .replace('& git -C $clone -c', "$savedTrace = $env:GIT_TRACE; $env:GIT_TRACE = ''\n& git -C $clone -c", 1)
            .replace('$clone2 =', '$env:GIT_TRACE = $savedTrace\n$clone2 =', 1))
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(script)], env=env, capture_output=True, text=True, timeout=60)
        output = result.stdout + result.stderr
        if any(secret in output for secret in (TOKEN, PROXY_TOKEN, urllib.parse.quote(TOKEN, safe=''), urllib.parse.quote(PROXY_TOKEN, safe=''))):
            raise RuntimeError('Secret appeared in test output (suppressed)')
        print(output.strip())
        if not result.returncode:
            assert proxy_requests, 'Git-configured authenticated proxy was bypassed'
            assert private_configs and all(private_configs), 'Proxy configuration was not private'
            assert not list(temp_root.glob('construct-git-*')), 'Temporary transport configuration survived'
            print('3 passed: authenticated Git proxy, private transport config, temp cleanup')
        if result.returncode:
            raise SystemExit(result.returncode)
    finally:
        proxy_server.shutdown()
        proxy_server.server_close()
        proxy_thread.join()
        server.shutdown()
        server.server_close()
        thread.join()
