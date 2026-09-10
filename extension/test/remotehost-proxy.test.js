"use strict";
// Run with CONSTRUCT_VSCODE_PROXY_AGENT=/path/to/@vscode/proxy-agent to also
// exercise the actual VS Code patch. The deterministic override regression always runs.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const https = require('node:https');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');

for (const mode of ['override', 'vscode']) {
  test(`pinned hostname requests survive ${mode} proxy patch without leaking credentials`, {
    skip: mode === 'vscode' && !process.env.CONSTRUCT_VSCODE_PROXY_AGENT,
  }, async t => {
    const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'construct-proxy-tls-'));
    t.after(() => fs.rmSync(temp, { recursive: true }));
    execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=construct.test',
      '-keyout', path.join(temp, 'key.pem'), '-out', path.join(temp, 'cert.pem')], { stdio: 'ignore' });
    const cert = fs.readFileSync(path.join(temp, 'cert.pem'));
    const pin = new crypto.X509Certificate(cert).fingerprint256;
    let credentials = [];
    const server = https.createServer({ key: fs.readFileSync(path.join(temp, 'key.pem')), cert }, (req, res) => {
      credentials.push(req.headers.authorization);
      res.setHeader('Content-Type', 'application/json');
      res.end('{"name":"alice","role":"admin"}');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    t.after(() => new Promise(resolve => { server.closeAllConnections(); server.close(resolve); }));
    // Preserve a hostname through the real HTTP/proxy code; only DNS is localised.
    const originals = { ...https, request(options, cb) {
      return https.request({ ...options, lookup: (_host, _opts, done) => _opts.all ? done(null, [{ address: '127.0.0.1', family: 4 }]) : done(null, '127.0.0.1', 4) }, cb);
    }};
    const patch = mode === 'vscode'
      ? require(process.env.CONSTRUCT_VSCODE_PROXY_AGENT).createHttpPatch({
        getProxySupport: () => 'override', addCertificatesV1: () => false,
      }, originals, (_flags, _req, _opts, _url, done) => done('DIRECT'))
      : { request(options, cb) { return originals.request({ ...options, agent: https.globalAgent }, cb); } };
    const patched = { ...https, __vscodeOriginal: originals, ...patch };
    const filename = path.resolve(__dirname, '../src/remotehost.js');
    const module = { exports: {} };
    vm.runInNewContext(fs.readFileSync(filename, 'utf8'), {
      module, exports: module.exports, require: name => name === 'https' ? patched : require(name),
      URL, Buffer, setTimeout, clearTimeout, process, console, __dirname: path.dirname(filename),
    }, { filename });
    const rh = module.exports;
    const baseUrl = `https://construct.test:${server.address().port}`;
    const client = pin => rh.createClient({ baseUrl, pin, auth: { kind: 'token', token: 'PRIVATE-FIXTURE' }, timeoutMs: 3000 });
    assert.equal((await client(pin).whoami()).name, 'alice');
    assert.deepEqual(credentials, ['Bearer PRIVATE-FIXTURE']);
    credentials = [];
    await assert.rejects(client('11'.repeat(32)).whoami(), /fingerprint mismatch/i);
    await assert.rejects(client('').whoami(), /has not been confirmed/i);
    assert.deepEqual(credentials, []);
    // The shared patched module must remain intact for other extensions/requests.
    assert.equal(patched.request, patch.request);
  });
}
