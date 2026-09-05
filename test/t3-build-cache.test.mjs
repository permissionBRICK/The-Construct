// Exercise the real cache driver with a tiny build recipe and fake network tools.
// No live services, apt, compilers, or upstream downloads are touched.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 't3-build-cache-'));
const repo = path.resolve(import.meta.dirname, '..');
const put = (file, text) => { fs.mkdirSync(path.dirname(file), {recursive:true}); fs.writeFileSync(file, text); };
try {
  const fixture = path.join(root, 'repo');
  const driver = path.join(fixture, 'bin/build-t3code.sh');
  put(driver, fs.readFileSync(path.join(repo, 'bin/build-t3code.sh')));
  const realRecipe = fs.readFileSync(path.join(repo, 'bin/t3code-build-recipe.sh'), 'utf8');
  put(path.join(fixture, 'bin/t3code-build-recipe.sh'), realRecipe.split('t3_recipe_prepare_source() {')[0] + `
t3_recipe_prepare_source() { echo prepare >> "$TEST_LOG"; }
t3_recipe_compile() {
  [[ "$(command -v node)" == "$T3_NODE_DIR/bin/node" ]]
  [[ "$npm_config_cache" == "$COMPILER_CACHE/npm" ]]
  echo compile >> "$TEST_LOG"
  [[ "\${TEST_FAIL_COMPILE:-}" != yes ]] || return 1
  mkdir -p apps/server/dist node_modules
  echo 'server' > apps/server/dist/bin.mjs
}
t3_recipe_package() {
  [[ "$(command -v node)" == "$T3_NODE_DIR/bin/node" ]]
  [[ "$npm_config_cache" == "$COMPILER_CACHE/npm" ]]
  echo package >> "$TEST_LOG"
  [[ "\${TEST_FAIL_PACKAGE:-}" != yes ]] || return 1
  mkdir -p "$output_dir" "$COMPILER_CACHE/resource-monitor"
  echo cached > "$COMPILER_CACHE/resource-monitor/object"
  echo 'installer' > "$output_dir/setup.exe"
}
`);
  for (const f of ['bin/apply-t3code-source.mjs', 'extension/vm/construct-t3park-patch.mjs', 'extension/vm/construct-t3-opencode-monitor-patch.mjs']) put(path.join(fixture,f), '// fixture\n');
  for (const channel of ['release','nightly']) {
    put(path.join(fixture,`patches/t3code-${channel}/source-transforms.json`), '{}\n');
    put(path.join(fixture,`patches/t3code-${channel}/overlays/source.ts`), 'source\n');
  }
  const fake = path.join(root,'tools');
  for (const [name, body] of Object.entries({
    node: 'echo v22.22.0',
    npm: 'echo Unexpected profile npm invocation >&2; exit 99',
    uname: 'echo x86_64',
    git: 'if [[ "$1" == clone ]]; then mkdir -p "${@: -1}/.git"; else echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; fi',
    pnpm: 'exit 1',
    'dpkg-query': 'exit 1',
    rustup: 'exit 1',
    'apt-get': 'echo Unexpected apt invocation >&2; exit 99',
    curl: 'echo Unexpected network invocation >&2; exit 99',
    // The disk policy itself is covered by t3-build-diskcheck.test.sh.
    df: "printf 'Filesystem 1024-blocks Used Available Capacity Mounted on\\nfixture 99000000 0 99000000 0%% /\\n'",
  })) { put(path.join(fake,name), '#!/bin/bash\n'+body+'\n'); fs.chmodSync(path.join(fake,name),0o755); }
  const runtime = path.join(root, 'runtimes/node-v26.8.1-linux-x64/bin');
  for (const [name, body] of Object.entries({
    node: 'if [[ "$1" == --version ]]; then echo v26.8.1; else exec "$TEST_REAL_NODE" "$@"; fi',
    npm: '[[ "$npm_config_cache" == "$T3CODE_COMPILER_CACHE/npm" ]] || exit 98; echo npm >> "$TEST_LOG"; echo "${TEST_VERSION:-1.0.0}"',
  })) { put(path.join(runtime,name), '#!/bin/bash\n'+body+'\n'); fs.chmodSync(path.join(runtime,name),0o755); }
  const nativeCache = path.join(root, 'home/.npm/_npx/existing/node_modules/addon.node');
  put(nativeCache, 'profile ABI 127 sentinel');
  const log = path.join(root,'calls');
  const env = {...process.env, PATH: `${fake}:${process.env.PATH}`, HOME:path.join(root,'home'),
    TEST_REAL_NODE:process.execPath, T3CODE_NODE_ROOT:path.join(root,'runtimes'),
    REPO_DIR:fixture, TEST_LOG:log, T3CODE_CHANNEL:'stable', CONSTRUCT_VERSION:'aaaaaaa', T3CODE_CACHE_ROOT:path.join(root,'sources'),
    T3CODE_COMPILER_CACHE:path.join(root,'compiler'), T3CODE_ARTIFACT_ROOT:path.join(root,'artifacts'),
    T3CODE_STATUS_PATH:path.join(root,'status'), T3CODE_LAUNCHER:path.join(root,'t3'),
    T3CODE_BUILD_MIN_FREE_GIB:'1'};
  delete env._FUNCS_ONLY;
  const run = (mode, extra={}, success=true) => {
    const r = spawnSync('bash',[driver],{env:{...env,T3CODE_BUILD_MODE:mode,...extra},encoding:'utf8'});
    assert.equal(r.status === 0, success, `${mode}: ${r.stdout}\n${r.stderr}`);
    assert.equal(fs.readFileSync(nativeCache, 'utf8'), 'profile ABI 127 sentinel');
    assert.equal(spawnSync('node', ['--version'], {env, encoding:'utf8'}).stdout.trim(), 'v22.22.0');
    return r;
  };
  const calls = name => fs.readFileSync(log,'utf8').split('\n').filter(x=>x===name).length;
  const server = () => JSON.parse(fs.readFileSync(path.join(root,'artifacts/server-manifest.json')));
  run('server');
  assert.equal(calls('compile'),1); assert.equal(calls('package'),0);
  assert.equal(fs.existsSync(path.join(root,'artifacts/manifest.json')),false);
  const original = server();
  run('server', {CONSTRUCT_VERSION:'bbbbbbb'});
  assert.equal(calls('compile'),1,'unchanged provision must skip compilation');
  fs.appendFileSync(driver,'\n# unrelated driver maintenance\n');
  run('server'); assert.equal(calls('compile'),1,'driver changes must not invalidate artifacts');
  const relocated = path.join(root,'relocated'); fs.cpSync(fixture,relocated,{recursive:true});
  run('server',{REPO_DIR:relocated}); assert.equal(calls('compile'),1,'absolute repository path must not affect hash');
  const npmBefore = calls('npm');
  run('desktop',{TEST_VERSION:'2.0.0', T3CODE_CHANNEL:'nightly'});
  assert.equal(calls('npm'),npmBefore,'packaging must not resolve the channel again');
  assert.equal(calls('compile'),1); assert.equal(calls('package'),1);
  assert.equal(JSON.parse(fs.readFileSync(path.join(root,'artifacts/manifest.json'))).version,'1.0.0');
  run('desktop'); assert.equal(calls('package'),1,'cached installer must skip packaging');
  run('server'); run('desktop'); assert.equal(calls('package'),1,'server-only run must retain cached installer');
  fs.writeFileSync(path.join(root,'artifacts/T3Code-Construct-Setup.exe'),'corrupt');
  run('desktop'); assert.equal(calls('package'),2,'corrupt installer must be rebuilt');
  fs.appendFileSync(path.join(fixture,'patches/t3code-release/overlays/source.ts'),'changed\n');
  run('desktop',{},false); assert.equal(calls('package'),2,'stale prepared server must refuse packaging');
  run('server',{TEST_FAIL_COMPILE:'yes'},false);
  assert.equal(server().patchHash,original.patchHash,'failed compile must retain previous server record');
  assert.equal(fs.readFileSync(fs.realpathSync(env.T3CODE_LAUNCHER),'utf8').trim(),'server');
  run('server'); assert.notEqual(server().patchHash,original.patchHash);
  assert.equal(fs.existsSync(path.join(root,'compiler/resource-monitor/object')),true,'compiler cache survives recipe changes');
  const active = fs.realpathSync(env.T3CODE_LAUNCHER);
  run('desktop',{TEST_FAIL_PACKAGE:'yes'},false);
  assert.equal(fs.realpathSync(env.T3CODE_LAUNCHER),active,'packaging failure must not change the active server');
  assert.equal(JSON.parse(fs.readFileSync(path.join(root,'artifacts/manifest.json'))).patchHash,original.patchHash,'failed package must not publish a new manifest');
  run('desktop');
  const beforeVersionChange = calls('compile');
  run('server',{TEST_VERSION:'2.0.0'}); assert.equal(calls('compile'),beforeVersionChange+1);
  assert.notEqual(server().buildHash, original.buildHash, 'version bump must invalidate the service restart key');
  run('server',{TEST_VERSION:'2.0.0',T3CODE_CHANNEL:'nightly'}); assert.equal(server().channel,'nightly');
  fs.rmSync(fs.realpathSync(env.T3CODE_LAUNCHER));
  const beforeMissing = calls('compile');
  run('server',{TEST_VERSION:'2.0.0',T3CODE_CHANNEL:'nightly'});
  assert.equal(calls('compile'),beforeMissing+1,'missing server executable must be rebuilt');
  const beforeRecipe = calls('compile');
  fs.appendFileSync(path.join(fixture,'bin/t3code-build-recipe.sh'),'\n# new artifact recipe\n');
  const previousLauncher = fs.realpathSync(env.T3CODE_LAUNCHER);
  run('all',{TEST_FAIL_PACKAGE:'yes'},false);
  assert.equal(fs.realpathSync(env.T3CODE_LAUNCHER),previousLauncher,'combined build failure must not activate the new server');
  run('all'); assert.equal(calls('compile'),beforeRecipe+1,'packaging retry must not recompile');
  assert.match(fs.readFileSync(env.T3CODE_STATUS_PATH,'utf8'),/T3CODE_DESKTOP_READY=yes/);
  const beforePinned = calls('npm');
  run('server', {T3CODE_SOURCE_VERSION:'3.0.0', T3CODE_INVENTORY:'nightly'});
  assert.equal(server().version, '3.0.0');
  assert.equal(server().channel, 'stable', 'nightly inventory must not switch the stable upstream channel');
  assert.equal(server().inventory, 'nightly');
  run('desktop', {T3CODE_SOURCE_VERSION:'4.0.0', T3CODE_INVENTORY:'release'});
  assert.equal(calls('npm'), beforePinned, 'pinned CI build must not re-resolve upstream');
  assert.equal(JSON.parse(fs.readFileSync(path.join(root,'artifacts/manifest.json'))).inventory, 'nightly');
  run('server', {T3CODE_SOURCE_VERSION:'3.0.0', T3CODE_INVENTORY:'../invalid'}, false);
  console.log('PASS: cache reuse, hash isolation, two-stage build, pinned packaging, failures, and compiler-cache retention');
} finally { fs.rmSync(root,{recursive:true,force:true}); }
