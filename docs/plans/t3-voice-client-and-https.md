# T3 Code: client-side voice capture + HTTPS serving

Status: MERGED to main 2026-09-02 (omniloop `wf_7MJmzOCU_Sr8`, Opus devs + Codex reviewers, both
tracks approved in round 2). Track A merge `0e786a3`, Track B merge `bf7d9fc`. The T3 source
branch `feat/construct-voice-client` in `/root/repos/t3code` (v0.0.37 + patch) is kept for reference;
the shipped artifact is `patches/t3code-construct.patch`.

Deviations accepted at merge: the host downloads the CA from `/etc/construct/t3code-ca.crt`
(0644 copy; the 0700 TLS dir is unreadable for a seed-user login); `setup-t3-https.sh --teardown`
removes the proxy without clearing the saved preference when T3 is switched off; readiness is
`T3CODE_PUBLIC_BASE_URL` (written only when the proxy came up), never `T3CODE_HTTPS`. Not applied
to `cloud/http.ts` `localOrigin` (loopback endpoint the relay dials). Field test on a real host is
still pending: certificate trust dialog (non-elevated), Desktop-app mic, browser mic over https.

## Why

Today the patched T3 server (`patches/t3code-construct.patch`, `apps/server/src/voiceInput.ts`)
spawns `rec` on the VM to get microphone audio. `rec` is Construct's shim
(`extension/vm/construct-rec-shim.sh`): it connects to a loopback port in 8767..8774 that the
**VS Code control-panel extension** reverse-forwards from the host, where the extension runs
ffmpeg. So T3 voice input only works while a VS Code window with the extension is open.

Everything else already lives on the VM and is independent of VS Code: the Claude OAuth token
(`~/.claude/.credentials.json`), the Anthropic speech WebSocket, transcript assembly, the level
ring, the composer insertion logic. Only the **audio source** is VS Code-bound.

Decision (project owner, 2026-09-02): T3 clients capture the microphone **themselves** (browser
`getUserMedia` or the Electron desktop app) and push PCM to the server; the `rec` host-bridge
path stays untouched as the fallback and for every other `rec` caller on the VM (Claude Code
CLI `/voice`, the VS Code chat mic). Because browsers only expose `getUserMedia` on secure
origins, Construct additionally serves T3 over **HTTPS** with a locally trusted certificate.

Hard facts that shaped the design (verified in the tree, do not re-derive):

- Effect RPC has no client-to-server streams; the server side is `RpcServer.toHttpEffectWebsocket`
  with `RpcSerialization.layerJson`. Client audio therefore goes over a **unary RPC per chunk**
  (`Schema.Uint8ArrayFromBase64` exists in effect 4 beta; 100 ms of 16 kHz mono S16LE is 3200 bytes).
- The desktop app loads its UI from a custom scheme registered `secure: true`
  (`apps/desktop/src/electron/ElectronProtocol.ts`), so `window.isSecureContext` is true and
  `getUserMedia` is available. The default Electron session has no permission handler (only the
  preview `BrowserSession` sets one), so media is granted without a prompt.
- `http://agent-vm.mshome.net:5177` is NOT a secure context: browsers hide `getUserMedia`.
- T3 auth uses **DPoP**; the server reconstructs the request URL with Effect's
  `HttpServerRequest.toURL`, which honours `Host` and `x-forwarded-proto: https`. A TLS proxy must
  forward exactly those headers or every DPoP proof fails with `url_mismatch`.
- `t3 serve` has no TLS option (only `--tailscale-serve`). HTTPS = reverse proxy in the VM.
- Startup pairing URL: `apps/server/src/serverRuntimeStartup.ts` `resolveStartupBrowserTarget`
  builds `http://<bind-host>:<port>`; `t3 auth pairing create` takes `--base-url`. Construct's
  extension passes `http://$(hostname).mshome.net:$T3CODE_PORT` (`extension/src/t3code.js`).

## Shared contract between the tracks

| Item | Owner | Value |
|---|---|---|
| `T3CODE_PUBLIC_BASE_URL` in `/etc/construct/config.env` | written by Track B, honoured by Track A | e.g. `https://agent-vm.mshome.net:5178` (no path). Absent/empty = today's behaviour. The `t3code-serve` unit already loads `config.env` as `EnvironmentFile`, so `t3 serve` sees it as a process env var. |
| `T3CODE_HTTPS` / `T3CODE_HTTPS_PORT` in `config.env` | Track B | `true`/`false`, default `true` when `T3CODE=true`; port default `5178`. Plain HTTP on `T3CODE_PORT` (5177) stays available for local tooling (t3park token mint etc.). |
| `CONSTRUCT_T3_VOICE_INPUT` | unchanged | still gates the `voiceInput` capability. |
| `patches/t3code-construct.patch` | **only Track A edits it** | Track B must not touch the patch file. |

---

## Track A: client-side capture in T3 (repo `/root/repos/t3code-voice`, branch `feat/construct-voice-client`)

The worktree is checked out at upstream tag `v0.0.37` with the current Construct patch applied
as the baseline commit `4bebb1bd` ("construct: apply t3code-construct.patch baseline"). Dependencies are
installed (`pnpm install`; `pnpm-lock.yaml` got modified by that, which is expected: the patch adds
`ws`. The lockfile is NOT part of the shipped patch, `bin/build-t3code.sh` installs with
`--no-frozen-lockfile`).

Deliverable is a **regenerated patch** committed into `/root/repos/construct-voice`
(branch `feat/t3-voice-client`), plus the docs there. See "Track A deliverables".

### A1. Contracts (`packages/contracts/src/voiceInput.ts`, `rpc.ts`, `environment.ts`)

- `VoiceInputStartInput`: add `source: Schema.optional(Schema.Literals(["client", "host"]))`.
  Absent means `host` (older clients keep today's behaviour).
- New `VoiceInputAudioInput = { sessionId: string, chunk: Uint8ArrayFromBase64 }` and
  `VoiceInputAudioResult = { accepted: boolean }`; new `WS_METHODS.voiceInputAudio = "voiceInput.audio"`
  unary RPC, error `EnvironmentAuthorizationError`, same auth scope as start/stop in
  `apps/server/src/auth/RpcAuthorization.ts`.
- Capabilities: keep `voiceInput`; add `voiceInputClientAudio: Schema.optionalKey(Schema.Boolean)`
  (true on patched servers) so a new client can tell whether the server accepts pushed audio.

### A2. Server (`apps/server/src/voiceInput.ts`, `ws.ts`, `environment/ServerEnvironment.ts`)

- Introduce an audio-source abstraction inside `voiceInput.ts` with two implementations:
  - **host**: exactly today's `spawn("rec", ...)` code path, same error text ("The host microphone
    bridge is unavailable ..."), server-side level events as today.
  - **client**: push-based. `pushVoiceAudio(sessionId, chunk)` looks the session up; only a
    client-source session accepts (`{accepted:false}` otherwise, never throws). Chunks that arrive
    before the Anthropic socket is open are dropped. First-chunk timeout after `listening`: 8 s ->
    fail fatally with "No microphone audio arrived from the client." No server-side level events
    for the client source (the client computes them locally).
- `startVoiceInput(sessionId, source)`; everything downstream (silence timer 15 s, max 120 s,
  keep-alives, transcript assembly, `stopped` event, one-active-session rule) is shared.
- Register the new RPC handler in `ws.ts` next to start/stop.
- `ServerEnvironment.ts`: `voiceInputClientAudio: true` alongside the existing `voiceInput` gate.

### A3. Web client (`apps/web`)

- New `apps/web/src/voice/clientAudioCapture.ts` (pure, testable helpers separated from browser calls):
  - `canCaptureClientAudio(): boolean` = `navigator.mediaDevices?.getUserMedia` exists AND
    `window.isSecureContext`.
  - `startClientAudioCapture({ onChunk, onLevel, onError }) -> { stop() }`:
    `getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } })`,
    `new AudioContext({ sampleRate: 16000 })`; if the browser refuses 16 kHz, decimate in the worklet
    from the actual rate (implement a simple linear resampler, unit-tested). An `AudioWorklet` module
    loaded from a Blob URL converts Float32 -> Int16LE and posts ~100 ms chunks (1600 samples).
    Level = same formula as the server (`clamp((rms - 0.006) * 10, 0, 1)`), throttled ~75 ms.
    Map `NotAllowedError` -> "Microphone access was denied", `NotFoundError` -> "No microphone found".
    `stop()` stops tracks, disconnects nodes, closes the context.
- `apps/web/src/state/voiceInput.ts`: pass `source` on start; add a `sendVoiceAudio` command
  (unary `voiceInput.audio`). Bound in-flight sends (e.g. max 8 outstanding; drop the oldest
  pending chunk rather than growing unbounded) so a slow link cannot balloon memory.
- Source selection = a persisted client setting `voiceInputSource: "auto" | "client" | "host"`
  (default `auto`) exposed in the existing client settings UI (find where other client
  settings live under `apps/web/src/components/settings/` and follow that pattern).
  - `auto`: client when `canCaptureClientAudio()` and the server reports `voiceInputClientAudio`,
    else host.
  - `client`: if not capturable, do not start; toast explaining why (insecure origin or no mic API).
  - `host`: today's behaviour.
- `ChatComposer.tsx`: keep the existing mic button, level ring, Ctrl+T tap/hold and the
  cursor-safe insertion logic **unchanged in behaviour**. For the client source: start capture in
  parallel with the RPC start (permission prompts take time), begin pushing on `listening`,
  feed local levels into `setVoiceLevel`, and on capture error stop the session + toast. Tooltip
  on the button states which source will be used ("this device" vs "Construct host bridge").
- Desktop app: verify (read the window/session creation code) that the main window's session grants
  the `media` permission. If some handler denies it, add an explicit allow for `media` mirroring
  the preview session's pattern; otherwise change nothing in `apps/desktop`.

### A4. Public base URL in the T3 server (small, same patch)

Honour `process.env.T3CODE_PUBLIC_BASE_URL` (trimmed, must parse as http(s) URL, path ignored):

- `resolveStartupBrowserTarget` in `apps/server/src/serverRuntimeStartup.ts`: use it as `baseTarget`
  in serve mode (the startup pairing URL then is the https one).
- `t3 auth pairing create` (`apps/server/src/cli/auth.ts`): default `--base-url` to it when the flag
  is omitted (explicit flag still wins).
- Any other place the headless server presents its own base URL to humans or clients (startup
  "listening on"/"open" logs; advertised endpoints if the serve path builds any, see
  `apps/server/src/cloud/http.ts` `localOrigin`): prefer the public URL when set. Investigate, apply
  where applicable, list what you changed in the commit message.

### A5. Quality gate (must all pass before `request_review`)

- `pnpm --filter @t3tools/contracts --filter @t3tools/server --filter @t3tools/web typecheck`
  (and `@t3tools/desktop` if touched); `pnpm lint`.
- Vitest unit tests (co-located `*.test.ts`, existing style) for: server source switching and
  `pushVoiceAudio` accept/reject + first-chunk timeout; contract schema encode/decode of an audio
  chunk; Float32 -> Int16 conversion, chunking, resampler, level formula; source-resolution logic.
  Run `pnpm --filter @t3tools/server test` and `pnpm --filter @t3tools/web test` (or the repo's
  per-package equivalent).
- `pnpm build` for server + web succeeds (the worklet Blob approach must survive bundling).
- Both bundle patches still apply to a COPY of the freshly built server bundle:
  `cp apps/server/dist/bin.mjs /tmp/bin.mjs && node /root/repos/construct/extension/vm/construct-t3park-patch.mjs apply --bundle /tmp/bin.mjs && node /root/repos/construct/extension/vm/construct-t3-opencode-monitor-patch.mjs apply --bundle /tmp/bin.mjs`.

### Track A deliverables

1. Commits on `feat/construct-voice-client` in `/root/repos/t3code-voice`.
2. Regenerated patch: from the t3 worktree run
   `git diff v0.0.37 -- . ':(exclude)pnpm-lock.yaml' > /root/repos/construct-voice/patches/t3code-construct.patch`
   and prove it applies on a pristine tree:
   `git -C /root/repos/t3code worktree add /tmp/t3-patchcheck v0.0.37 && git -C /tmp/t3-patchcheck apply --check /root/repos/construct-voice/patches/t3code-construct.patch; git -C /root/repos/t3code worktree remove --force /tmp/t3-patchcheck`.
3. In `/root/repos/construct-voice`: update `docs/control-panel.md` (the "Voice input" bullet under
   "The shared patch adds", and the "Microphone passthrough" section: T3 no longer needs the
   extension; sources; browser needs HTTPS or the desktop app), `README.md` feature line if it
   claims otherwise, and `docs/provisioning.md` row for `T3CODE_LIMIT_RESUME` wording. Commit there
   with a conventional message. Do NOT edit `bin/`, `systemd/`, `*.ps1`, or `extension/` in the
   construct repo; that is Track B.

---

## Track B: HTTPS for T3 (repo `/root/repos/construct-https`, branch `feat/t3-https`)

### B1. VM side: `bin/setup-t3-https.sh` (new, idempotent, sourced-for-tests friendly)

Follow the conventions of `bin/install-ai-tools.sh` (`step`/`note`/`warn`, `_FUNCS_ONLY=true`
early return so tests can source the pure functions, `config-set.sh` for config.env writes).

- Inputs: `CONFIG_FILE` (default `/etc/construct/config.env`), `T3CODE_PORT`, `T3CODE_HTTPS`,
  `T3CODE_HTTPS_PORT` (default 5178), `CONSTRUCT_EXTERNAL_HOST` (may be empty).
- Ensure `nginx` and `openssl` are installed (apt, `DEBIAN_FRONTEND=noninteractive`; offline apt
  must degrade to a warning, never fail provisioning).
- Certificates under `/etc/construct/tls/` (0700 dir, keys 0600):
  - `ca.key`/`ca.crt`: "Construct Local CA (<hostname>)", 3650 days, created once, reused.
  - `t3.key`/`t3.crt`: leaf signed by the CA, 825 days, `extendedKeyUsage=serverAuth`, SANs:
    `DNS:<hostname>.mshome.net`, `DNS:<hostname>`, `DNS:localhost`, `IP:127.0.0.1`,
    plus `CONSTRUCT_EXTERNAL_HOST` as DNS or IP when set, plus the VM's primary IPv4 if determinable.
    Regenerate the leaf when the SAN set changed (persist the SAN list in `t3.sans`) or it expires
    within 60 days. Keep SAN building and the regenerate decision as pure functions.
- nginx site `/etc/nginx/sites-available/construct-t3` + symlink in `sites-enabled`:
  `listen 0.0.0.0:${T3CODE_HTTPS_PORT} ssl;` and `listen [::]:${T3CODE_HTTPS_PORT} ssl;`,
  `proxy_pass http://127.0.0.1:${T3CODE_PORT};`, `proxy_http_version 1.1;`,
  `proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection $connection_upgrade;`
  (with the standard `map $http_upgrade $connection_upgrade` block), **`proxy_set_header Host $http_host;`**,
  **`proxy_set_header X-Forwarded-Proto https;`**, `X-Forwarded-For`, `proxy_read_timeout 1h;`
  `proxy_send_timeout 1h;` (long-lived WebSockets), `client_max_body_size 0;` (attachment
  uploads), `proxy_buffering off;`. Render the config from a pure function so a test can assert
  every one of those directives is present. `nginx -t` before `systemctl reload/restart nginx`;
  enable nginx. Do not touch the distro default site except when it would fail to start nginx.
- Write `T3CODE_HTTPS`, `T3CODE_HTTPS_PORT`, `T3CODE_PUBLIC_BASE_URL=https://<host>:<port>` into
  config.env, where `<host>` is `CONSTRUCT_EXTERNAL_HOST` when set else `<hostname>.mshome.net`
  (same rule as the pairing script in `extension/src/t3code.js`).
- Write `/etc/construct/t3code-https-status` (`T3CODE_HTTPS_READY=yes`, port, CA sha256
  fingerprint, CA path) for the host handoff.
- Disable path (`T3CODE_HTTPS=false`): remove the site, reload nginx, set
  `T3CODE_PUBLIC_BASE_URL=` (empty) and `T3CODE_HTTPS=false`, remove the status file. Keep the CA.

### B2. Wiring on the VM

- `bin/install-ai-tools.sh` `install_t3code`: run the https setup so that config.env holds
  `T3CODE_PUBLIC_BASE_URL` **before** `systemctl restart t3code-serve` (the server reads it at
  start), and the proxy is (re)loaded after. The unchanged-build early return must still reconcile
  HTTPS (a toggle flip must not be skipped).
- `bin/provision.sh`: new `T3CODE_HTTPS` with the same keep-saved semantics as `T3CODE_CHANNEL`
  (empty keeps the saved value; first-time default `true`), logged with the other T3 keys, passed
  into the installer env; the `T3CODE=false` branch also runs the disable path.
- `extension/src/t3code.js` install script builder (panel toggle path) gets the same call.
- `bin/export-config.sh` / `bin/restore-config.sh`: include `/etc/construct/tls/` so the CA
  survives a reinstall (otherwise Windows needs a new trust import). Follow the existing
  `vscode-serve-web.token` precedent.
- `bin/print-connection-info.sh` and the login banner (`bin/update-login-banner.sh`) show the https
  URL when enabled.

### B3. Host side (Windows)

- `Provision-AgentVM.ps1`: new `[string]$T3CodeHttps = ""` parameter (empty keeps saved), threaded
  into the `$envPrefix` like `T3CODE_CHANNEL`. `Update-T3Code.ps1` maps a `t3codeHttps` setting.
- In the `$Action -eq 'provision'` T3 handoff block: if `/etc/construct/t3code-https-status` reports
  ready, `Invoke-ScpFrom` `/etc/construct/tls/ca.crt` to
  `%LOCALAPPDATA%\The-Construct\artifacts\t3code\construct-t3-ca.crt` and trust it:
  elevated -> `Import-Certificate -CertStoreLocation Cert:\LocalMachine\Root` (silent);
  not elevated -> `Cert:\CurrentUser\Root` (Windows shows ONE confirmation dialog; say so in the
  `Write-Step` text). Skip when a cert with that thumbprint is already in either Root store.
  Best-effort: never fail provisioning; warn instead. Put the decision logic in a pure helper in
  `lib/AgentVm.Common.ps1` (e.g. `Get-T3CaImportPlan -Elevated -PresentInMachine -PresentInUser`)
  and unit-test it in `test/host-lib.test.ps1`. **ASCII-only inside PowerShell strings** (WinPS 5.1
  reads BOM-less files as CP1252; the AST test enforces this).
- Elevation check pattern already exists (`lib/AgentVm.Common.ps1` around line 1771).

### B4. Control-panel extension

- `extension/src/probe.js`: emit `T3CODE_HTTPS` and `T3CODE_HTTPS_PORT` (and `T3CODE_PUBLIC_BASE_URL`);
  `toState` builds the T3 detail/URL from them (https when enabled, else today's http).
- `extension/src/t3code.js`: both pairing-script variants use `https://<host>:${T3CODE_HTTPS_PORT}`
  when `cfgget T3CODE_HTTPS` is `true`, else the http form; prefer `T3CODE_PUBLIC_BASE_URL` when set.
  `baseUrl(cfg)` fallback takes the probed state into account. Update the "byte-identical" comment
  block honestly (this is a deliberate feature change) and the tests in
  `extension/test/t3code.test.js` and `extension/test/probe.test.js`.
- `extension/ARCHITECTURE.md`: update the `t3code.js` / `probe.js` lines.

### B5. Docs

- `docs/provisioning.md`: rows for `T3CODE_HTTPS`, `T3CODE_HTTPS_PORT`, `T3CODE_PUBLIC_BASE_URL`.
- `docs/control-panel.md` T3 section: HTTPS URL, where the CA is, the one-time CurrentUser prompt
  when not elevated, Firefox needs `security.enterprise_roots.enabled` (own trust store), browser
  microphone capture needs the https origin. Keep the existing voice paragraphs; Track A rewrites
  those, so limit edits there to the HTTPS facts to reduce merge conflicts.
- `README.md` T3 line mentions HTTPS.

### B6. Quality gate

- `bash -n` on every touched script; new `test/t3-https.test.sh` (nginx config directives, SAN
  builder incl. external host as DNS vs IP, regenerate decision, config.env key writes via a temp
  file, disable path) following `test/partial-streaming.test.sh` style; existing
  `test/*.test.sh` that cover touched files still pass.
- `node extension/test/t3code.test.js`, `node extension/test/probe.test.js` (and the other
  plain-node suites) pass.
- `pwsh -File test/host-lib.test.ps1` passes (includes the ASCII guard).
- Do NOT edit `patches/t3code-construct.patch`.

---

## Review focus (both reviewers)

- Correctness against this plan and the hard facts above (DPoP headers, secure context, the
  unchanged `rec` path, older clients defaulting to `host`).
- Security: keys 0600, no secrets in logs, base64 chunk size bounds, the unary audio RPC cannot
  be used to write into someone else's session (session ids are random UUIDs minted by the
  client; verify the server rejects unknown ids quietly).
- Honest failure modes: every new failure is surfaced to the user with a reason.
- Tests actually exercise the pure functions; run the quality gate yourself before approving.
