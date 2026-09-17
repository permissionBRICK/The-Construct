#!/usr/bin/env python3
"""Exercise the embedded updater's real TLS pin and CLI check on a temporary loopback server."""
import hashlib
import http.server
import json
import pathlib
import ssl
import subprocess
import tempfile
import threading

script = pathlib.Path(__file__).parents[1] / 'host/update-construct-host.sh'
library = script.read_text().split("<<'PY'\n", 1)[1].split('\nPY\n', 1)[0].rsplit('\ntry:\n    op,', 1)[0]
module = {}
exec(compile(library, str(script), 'exec'), module)

with tempfile.TemporaryDirectory(prefix='updater-health-') as temp:
    root = pathlib.Path(temp)
    cert, key = root/'cert.pem', root/'key.pem'
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                    '-subj', '/CN=localhost', '-keyout', str(key), '-out', str(cert)],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    cli = root/'admin-cli'
    cli.write_text('#!/bin/sh\n[ "$DOTNET_ENVIRONMENT" = Production ] && [ "$*" = "admin db check --json" ] || exit 1\ncat db-check.json\n')
    cli.chmod(0o755)
    (root/'db-check.json').write_text('{"status":"ok","schemaVersion":600}')
    body = dict(status='maintenance', commit='a'*40, schemaVersion=600)
    requests = []

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append(self.headers.get('Authorization'))
            assert self.path == '/api/v1/health'
            data = json.dumps(body).encode()
            self.send_response(200)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *_):
            pass

    server = http.server.HTTPServer(('127.0.0.1', 0), Handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(cert, key)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        thumb = hashlib.sha1(ssl.PEM_cert_to_DER_cert(cert.read_text())).hexdigest()
        handoff = dict(updateId='a'*32, certificateThumbprint=thumb.upper(), healthTimeoutSeconds=0,
                       healthUrl=f'https://127.0.0.1:{server.server_port}/api/v1/health', healthToken='test-secret',
                       adminCliPath=str(cli), publishDir=str(root))
        module.update(h=handoff, r=dict(updateId='a'*32, healthAttempts=0), record_path=root/'record.json', fence_path=root/'fence.json')

        def health(expected=True, code='health-failed'):
            try:
                module['test_update_health']('a'*40, 600)
                assert expected, 'invalid health was accepted'
            except module['UpdateError'] as error:
                assert not expected and str(error) == code, error

        health()
        assert requests == ['UpdateHandoff test-secret']
        handoff['certificateThumbprint'] = '0'*40
        health(False)
        assert len(requests) == 1, 'credential was sent before checking the leaf pin'
        handoff['certificateThumbprint'] = thumb
        for field, wrong in [('status', 'ok'), ('commit', 'b'*40), ('schemaVersion', 599)]:
            old = body[field]; body[field] = wrong; health(False); body[field] = old
        for invalid in ['{"status":"bad","schemaVersion":600}', '{"status":"ok","schemaVersion":601}']:
            (root/'db-check.json').write_text(invalid); health(False)
        handoff['healthUrl'] = 'https://example.invalid/api/v1/health'
        health(False, 'invalid-health-endpoint')
        assert 'test-secret' not in (root/'record.json').read_text()
    finally:
        server.shutdown(); server.server_close(); thread.join(timeout=5)
print('host-updater-health: pinned TLS, loopback restriction, identity and CLI schema checks passed')
