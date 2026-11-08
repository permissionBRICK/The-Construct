import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const t3 = require('../extension/src/t3code.js');
const host = require('../extension/src/host.js');
const root = resolve(import.meta.dirname, '..');
const scratch = mkdtempSync(join(tmpdir(), 't3-live-approval-'));
try {
  const config = join(scratch, 'config.env');
  const bin = join(scratch, 'bin'); mkdirSync(bin);
  const cli = join(bin, 'construct');
  for (const supported of [true, false]) {
    writeFileSync(cli, supported ? `#!/bin/sh\nexec bash '${root}/bin/construct' "$@"\n` : '#!/bin/sh\nexit 2\n', { mode: 0o755 });
    writeFileSync(config, 'AGENT_NAME=test\nOTHER=retained\n');
    for (const enabled of [true, false]) {
      const script = t3.buildAutoApprovalScript(enabled)
        .replace('CONFIG_FILE=/etc/construct/config.env', `CONFIG_FILE='${config}'`)
        .replace('renderer=/opt/construct/repo/bin/config-set.sh', `renderer='${root}/bin/config-set.sh'`);
      execFileSync('bash', ['-c', script], { env: { ...process.env, CONFIG_FILE: config, PATH: `${bin}:${process.env.PATH}` } });
      assert.equal(readFileSync(config, 'utf8'), `AGENT_NAME=test\nOTHER=retained\nT3CODE_AUTO_APPROVE=${enabled}\n`);
    }
  }
  assert.deepEqual(host.patchReprovisionChanges({ t3codeAutoApprove: false }, { t3codeAutoApprove: true }), []);
  const instance = { name: 'selected-vm' };
  const cfg = { host: 'selected-host' };
  const calls = [];
  const transport = { isReachable: async opts => { assert.equal(opts.instance, instance); return true; },
    runRemoteScript: async (script, opts) => { assert.equal(opts.cfg, cfg); calls.push(script); return { code: 0 }; } };
  for (const enabled of [true, false]) assert.deepEqual(await t3.setAutoApprovalOnVm(enabled, { instance, cfg, _ssh: transport }), {});
  assert.deepEqual(calls, [t3.buildAutoApprovalScript(true), t3.buildAutoApprovalScript(false)]);
  const offline = await t3.setAutoApprovalOnVm(true, { _ssh: { isReachable: async () => false } });
  assert.match(offline.error, /offline/);
  const failure = await t3.setAutoApprovalOnVm(false, { _ssh: { isReachable: async () => true, runRemoteScript: async () => ({ code: 1, stderr: 'write failed' }) } });
  assert.match(failure.error, /Save again to retry.*write failed/);
  assert.throws(() => t3.buildAutoApprovalScript('true'));
  console.log('Live T3 approval: enable/disable, old-CLI fallback, preserved config, selected VM, offline/error reporting, no reprovision passed');
} finally { rmSync(scratch, { recursive: true, force: true }); }
