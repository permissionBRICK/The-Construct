#!/usr/bin/env bash
# Plain-Bash tests for bin/construct-agent-sign-in.py (plan 11.8, S34): each coding agent's own
# sign-in under a pseudo-terminal, its screen as JSON lines, input from stdin, stop, the signed-in
# check, and the Antigravity sign-in shared with T3 Code's home.
# Run: bash test/agent-sign-in.test.sh
#
# The CLIs are fakes that print what the real ones print and read the terminal; no network.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/construct-agent-sign-in.py"
tmp="$(mktemp -d)"
trap 'pkill -KILL -f "${tmp}/bin/" 2>/dev/null; rm -rf "${tmp}"' EXIT

pass=0
fail=0
ok() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1))
    printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "${name}"
  fi
}

mkdir -p "${tmp}/bin" "${tmp}/home" "${tmp}/run"
export HOME="${tmp}/home" CONSTRUCT_SIGN_IN_RUN_DIR="${tmp}/run" T3CODE_HOME="${tmp}/home/.t3"
export PATH="${tmp}/bin:/usr/bin:/bin" FAKE_LOG="${tmp}/log"

# Claude Code: a link (as an OSC 8 hyperlink, in colour), then a prompt without a newline; the right
# code signs in. Its pid goes to the log, so a test can see it is gone.
cat >"${tmp}/bin/claude" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  --version) echo "2.1.300 (Claude Code)"; exit 0 ;;
  "auth status") [[ -s "${HOME}/.claude/.credentials.json" ]]; exit $? ;;
  "auth login")
    echo "pid $$" >>"${FAKE_LOG}"
    # The link with the redirect to its own port goes to BROWSER; the screen shows the one for a pasted code.
    [[ -n "${BROWSER:-}" ]] && "${BROWSER}" "https://claude.example/oauth?code=true&redirect_uri=http%3A%2F%2Flocalhost%3A45678%2Fcallback&state=abc"
    printf 'Opening browser to sign in\xe2\x80\xa6\r\n'
    printf "If the browser didn't open, visit: \e]8;;https://claude.example/oauth?code=true&state=abc\a\e[1mhttps://claude.example/oauth?code=true&state=abc\e[0m\e]8;;\a\r\n"
    printf 'Paste code here if prompted > '
    IFS= read -r code
    printf 'got %s\n' "${code}" >>"${FAKE_LOG}"
    if [[ "${code}" == good-code ]]; then
      mkdir -p "${HOME}/.claude"; echo '{"claudeAiOauth":{}}' >"${HOME}/.claude/.credentials.json"
      echo "Login successful."; exit 0
    fi
    echo "OAuth error: invalid code"; exit 1 ;;
esac
exit 2
EOF

# Codex: the device code flow, done once the code was "entered" elsewhere.
cat >"${tmp}/bin/codex" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  --version) echo "codex-cli 0.170.0"; exit 0 ;;
  "login status") [[ -s "${HOME}/.codex/auth.json" ]] && { echo "Logged in using ChatGPT"; exit 0; }; echo "Not logged in"; exit 1 ;;
  login)
    echo "browser login" >>"${FAKE_LOG}"
    printf 'Starting local login server on http://localhost:1455.\n'
    "${BROWSER}" "https://auth.openai.example/oauth/authorize?redirect_uri=http%3A%2F%2F127.0.0.1%3A1455%2Fauth%2Fcallback&state=s1"
    # Links a client must not take for a loopback redirect: a low port, plain http, a space.
    "${BROWSER}" "https://auth.openai.example/oauth/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A80%2Fcb"
    "${BROWSER}" "http://auth.openai.example/oauth/authorize?redirect_uri=http%3A%2F%2F127.0.0.1%3A1455%2Fauth%2Fcallback"
    "${BROWSER}" "https://auth.openai.example/a b"
    sleep "${FAKE_CODEX_WAIT:-0.5}"
    mkdir -p "${HOME}/.codex"; echo '{}' >"${HOME}/.codex/auth.json"
    echo "Successfully logged in"; exit 0 ;;
  "login --device-auth")
    printf '1. Open this link in your browser and sign in to your account\n   \e[94mhttps://auth.openai.example/codex/device\e[0m\n\n'
    printf '2. Enter this one-time code \e[90m(expires in 15 minutes)\e[0m\n   \e[94mABCD-12345\e[0m\n'
    sleep "${FAKE_CODEX_WAIT:-0.5}"
    mkdir -p "${HOME}/.codex"; echo '{}' >"${HOME}/.codex/auth.json"
    echo "Successfully logged in"; exit 0 ;;
esac
exit 2
EOF

# OpenCode 2: a list that redraws itself in place (cursor up, erase below) when an arrow key moves it; OpenRouter's
# key after a start in which the terminal still echoes, read with echo off (the real prompt shows a mask).
cat >"${tmp}/bin/opencode" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  --version) echo "opencode ${FAKE_OC_VERSION:-v2.3.2}"; exit 0 ;;
  "auth list --standalone --format json") cat "${HOME}/oc-auth.json" 2>/dev/null || echo '[]'; exit 0 ;;
  "auth login openrouter --standalone"|"auth login --provider openrouter")
    echo "args $*" >>"${FAKE_LOG}"
    sleep 1
    printf '\xe2\x97\x86  Enter your OpenRouter API key\r\n'
    IFS= read -rs key
    printf 'key %s\n' "${key}" >>"${FAKE_LOG}"
    # A CLI that echoes the key after all: the screen must not show it.
    [[ -n "${FAKE_OC_ECHO:-}" ]] && printf 'Saved %s\r\n' "${key}"
    echo '[{"id":"openrouter"}]' >"${HOME}/oc-auth.json"
    printf '\xe2\x97\x87  Connected to OpenRouter\r\n'; exit 0 ;;
  "auth login --standalone")
    printf '\e[?25l\xe2\x97\x86  Select login method\r\n\xe2\x94\x82  \xe2\x97\x8f Sign in with ChatGPT\r\n\xe2\x94\x82  \xe2\x97\x8b API key\r\n\xe2\x94\x94\r\n'
    IFS= read -rsn3 key
    [[ "${key}" == $'\e[B' ]] || { echo "unexpected key"; exit 1; }
    printf '\e[3A\e[J\xe2\x94\x82  \xe2\x97\x8b Sign in with ChatGPT\r\n\xe2\x94\x82  \xe2\x97\x8f API key\r\n\xe2\x94\x94\r\n'
    IFS= read -rs line
    echo '[{"id":"openai"}]' >"${HOME}/oc-auth.json"
    printf '\xe2\x97\x87  Connected\r\n'; exit 0 ;;
esac
exit 2
EOF

# Antigravity: its ACP terminal sign-in writes the token file, with file storage only.
cat >"${tmp}/bin/agy" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  --version) echo "1.5.13"; exit 0 ;;
  "acp --login")
    echo "storage=${AGY_ACP_FORCE_FILE_STORAGE:-}" >>"${FAKE_LOG}"
    "${BROWSER}" "https://accounts.google.example/o/oauth2/auth?redirect_uri=https%3A%2F%2Fantigravity.example%2Foauth-callback&state=xyz"
    printf 'Open this link in a browser to log in with Google:\n  https://accounts.google.example/o/oauth2/auth?state=xyz\nThen paste the authorization code, or the full URL of the page you land on, and press Enter:\n'
    IFS= read -r code
    mkdir -p "${HOME}/.gemini/antigravity-cli"; printf 'token-for-%s' "${code}" >"${HOME}/.gemini/antigravity-cli/antigravity-oauth-token"
    echo "Signed in."; exit 0 ;;
esac
exit 2
EOF
chmod +x "${tmp}/bin/"*

# Runs one session: the input lines go in one by one with a pause, then stdin stays open for $hold
# seconds before it closes. Output to $1.out, exit status to $1.rc.
session() {
  local name="$1" agent="$2" hold="$3"
  shift 3
  {
    sleep 0.8
    for line in "$@"; do printf '%s\n' "${line}"; sleep 0.8; done
    sleep "${hold}"
  } | timeout 60 python3 "${SCRIPT}" --session "${name}" ${SIGN_IN_FLAGS:-} "${agent}" >"${tmp}/${name}.out" 2>"${tmp}/${name}.err"
  printf '%s' "${PIPESTATUS[1]}" >"${tmp}/${name}.rc"
}
last_screen() { jq -rs '[.[] | select(.type == "screen")] | last | .text' "${tmp}/$1.out"; }
end_line() { jq -cs '[.[] | select(.type == "end")] | last' "${tmp}/$1.out"; }
jq_end() { jq -e "$2" <<<"$(end_line "$1")" >/dev/null; }
jqt() { jq -e "$@" >/dev/null; }

# ── Claude Code: link, prompt, the pasted code, signed in ─────────────────────
session claude claude-code 1.5 '{"text":"good-code"}'
ok "claude: starts with the agent" jqt 'select(.type == "start") | .agent == "claude-code"' "${tmp}/claude.out"
ok "claude: the link reads as plain text (no escape left)" bash -c '[[ "$(jq -rs "[.[] | select(.type == \"screen\")] | last | .text" "$1")" == *"visit: https://claude.example/oauth?code=true&state=abc"* ]] && ! grep -q $'"'"'\\u001b'"'"' "$1"' _ "${tmp}/claude.out"
ok "claude: the prompt without a newline is shown" grep -q 'Paste code here if prompted >' "${tmp}/claude.out"
ok "claude: the code reached the CLI" grep -qx 'got good-code' "${FAKE_LOG}"
ok "claude: ends done, signed in, with its version" jq_end claude '.outcome == "done" and .signedIn == true and .version == "2.1.300"'
ok "claude: exit 0" test "$(cat "${tmp}/claude.rc")" = 0
ok "claude: the link it opened is reported with its redirect port" jqt -s '[.[] | select(.type == "browser")] == [{"type": "browser", "url": "https://claude.example/oauth?code=true&redirect_uri=http%3A%2F%2Flocalhost%3A45678%2Fcallback&state=abc", "port": 45678}]' "${tmp}/claude.out"
ok "claude: no pid file left" test -z "$(ls -A "${tmp}/run")"

# Control characters typed into the reply never reach the terminal as such.
rm -f "${HOME}/.claude/.credentials.json"
session ctrl claude-code 1.5 '{"text":"bad\u001b[A\u0003code\r"}'
ok "input: control characters are dropped" grep -qx 'got bad\[Acode' "${FAKE_LOG}"
ok "input: a wrong code ends failed, not signed in" jq_end ctrl '.outcome == "failed" and .signedIn == false'
ok "input: exit 3" test "$(cat "${tmp}/ctrl.rc")" = 3

# ── Codex: the device code, done when the CLI is ──────────────────────────────
session codex codex 1.5
ok "codex: link and one-time code on the screen" bash -c '[[ "$1" == *"https://auth.openai.example/codex/device"* && "$1" == *"ABCD-12345"* ]]' _ "$(last_screen codex)"
ok "codex: ends done and signed in" jq_end codex '.outcome == "done" and .signedIn == true and .version == "0.170.0"'
ok "codex: no browser sign-in without --browser" bash -c '! grep -q "browser login" "$1"' _ "${FAKE_LOG}"
rm -f "${HOME}/.codex/auth.json"
SIGN_IN_FLAGS=--browser session codex-browser codex 1.5
ok "codex --browser: its browser sign-in, not the device code" grep -qx 'browser login' "${FAKE_LOG}"
ok "codex --browser: the loopback redirect's port, and links without one" jqt -s '[.[] | select(.type == "browser") | .port] == [1455, null]' "${tmp}/codex-browser.out"
ok "codex --browser: plain http and links with spaces are not reported" jqt -s '[.[] | select(.type == "browser")] | length == 2' "${tmp}/codex-browser.out"
ok "codex --browser: ends done and signed in" jq_end codex-browser '.outcome == "done" and .signedIn == true'
ok "codex --browser: the links file is gone" test -z "$(ls -A "${tmp}/run")"

# ── OpenCode 2: --standalone, an arrow key, a list that redraws ───────────────
session opencode opencode 1.5 '{"key":"down"}' '{"text":""}'
screen="$(last_screen opencode)"
ok "opencode: the redrawn list shows once, the choice moved" bash -c '[[ "$(grep -c "Sign in with ChatGPT" <<<"$1")" == 1 && "$1" == *"● API key"* && "$1" == *"○ Sign in with ChatGPT"* ]]' _ "${screen}"
ok "opencode: ends done and signed in" jq_end opencode '.outcome == "done" and .signedIn == true and .version == "2.3.2"'

# ── OpenCode with an OpenRouter key: typed only once the terminal no longer echoes, never shown ──
rm -f "${HOME}/oc-auth.json"
: >"${FAKE_LOG}"
key='sk-or-v1-0000fake0000test'
SIGN_IN_FLAGS='--provider openrouter' session openrouter opencode 2.5 "{\"secret\":\"${key}\"}"
ok "openrouter: OpenCode 2 goes straight to OpenRouter" grep -qx 'args auth login openrouter --standalone' "${FAKE_LOG}"
ok "openrouter: the key reached the prompt once it read with echo off" grep -qx "key ${key}" "${FAKE_LOG}"
ok "openrouter: ends done and signed in" jq_end openrouter '.outcome == "done" and .signedIn == true'
ok "openrouter: the key is on no screen" bash -c '! grep -q "$1" "$2"' _ "${key}" "${tmp}/openrouter.out"
rm -f "${HOME}/oc-auth.json"
FAKE_OC_ECHO=1 SIGN_IN_FLAGS='--provider openrouter' session openrouter-echo opencode 2.5 "{\"secret\":\"${key}\"}"
ok "openrouter: a key the CLI echoes is blotted out" bash -c '! grep -q "$1" "$2" && [[ "$3" == *"Saved ••••"* ]]' _ "${key}" "${tmp}/openrouter-echo.out" "$(last_screen openrouter-echo)"
: >"${FAKE_LOG}"
rm -f "${HOME}/oc-auth.json"
FAKE_OC_VERSION=1.19.6 SIGN_IN_FLAGS='--provider openrouter' session openrouter-v1 opencode 2.5 "{\"secret\":\"${key}\"}"
ok "openrouter: OpenCode 1 takes --provider" grep -qx 'args auth login --provider openrouter' "${FAKE_LOG}"
python3 "${SCRIPT}" --session p1 --provider openrouter codex </dev/null >/dev/null 2>&1
ok "usage: a provider only for OpenCode" test "$?" = 2
python3 "${SCRIPT}" --session p1 --provider anthropic opencode </dev/null >/dev/null 2>&1
ok "usage: only OpenRouter" test "$?" = 2
rm -f "${HOME}/oc-auth.json"

# ── Antigravity: file storage, and the token shared with T3 Code's homes ──────
other="${T3CODE_HOME}/userdata/providers/antigravity/0123abcd"
mkdir -p "${other}"
session agy antigravity 1.5 '{"text":"4/0Acode"}'
t3dir="${T3CODE_HOME}/userdata/providers/antigravity/$(printf '%s' antigravity | sha256sum | cut -d' ' -f1)/antigravity-acp"
ok "antigravity: signs in with file storage" grep -qx 'storage=1' "${FAKE_LOG}"
ok "antigravity: ends done and signed in" jq_end agy '.outcome == "done" and .signedIn == true and .version == "1.5.13"'
ok "antigravity: its link returns to a hosted page: no port" jqt -s '[.[] | select(.type == "browser")] | length == 1 and (.[0] | has("port") | not)' "${tmp}/agy.out"
ok "antigravity: T3 Code's home has the same token" cmp -s "${HOME}/.gemini/antigravity-cli/antigravity-oauth-token" "${t3dir}/antigravity-oauth-token"
ok "antigravity: and so has another T3 instance's home" cmp -s "${HOME}/.gemini/antigravity-cli/antigravity-oauth-token" "${other}/antigravity-acp/antigravity-oauth-token"
ok "antigravity: the copy is private" bash -c '[[ "$(stat -c %a "$1/antigravity-oauth-token")" == 600 && "$(stat -c %a "$1")" == 700 ]]' _ "${t3dir}"

# The copy follows no symlink on either side and never reuses a temporary file's permissions.
share() { python3 -c 'import importlib.util, sys
spec = importlib.util.spec_from_file_location("s", sys.argv[1]); m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
try:
    print(m.share_antigravity_sign_in())
except OSError as e:
    print("refused", e.errno)' "${SCRIPT}"; }
export -f share
export SCRIPT
token="${HOME}/.gemini/antigravity-cli/antigravity-oauth-token"
printf 'outside' >"${tmp}/outside"
chmod 644 "${tmp}/outside"
rm -f "${t3dir}/antigravity-oauth-token"
printf 'stale' >"${t3dir}/antigravity-oauth-token.construct"
chmod 666 "${t3dir}/antigravity-oauth-token.construct"
ln -s "${tmp}/outside" "${t3dir}/antigravity-oauth-token"
ok "antigravity copy: a symlinked target is replaced, not followed" bash -c '[[ "$(share)" == True && ! -L "$1/antigravity-oauth-token" && "$(cat "$2")" == outside ]]' _ "${t3dir}" "${tmp}/outside"
ok "antigravity copy: an old temporary file does not keep its permissions" bash -c '[[ "$(stat -c %a "$1/antigravity-oauth-token")" == 600 && ! -e "$1/antigravity-oauth-token.construct" ]]' _ "${t3dir}"
mv "${other}/antigravity-acp" "${tmp}/elsewhere"
ln -s "${tmp}/elsewhere" "${other}/antigravity-acp"
rm -f "${tmp}/elsewhere/antigravity-oauth-token"
ok "antigravity copy: a symlinked directory on the way is refused" bash -c '[[ "$(share)" == refused* && ! -e "$1/antigravity-oauth-token" ]]' _ "${tmp}/elsewhere"
rm "${other}/antigravity-acp"
mv "${token}" "${tmp}/real-token"
ln -s "${tmp}/real-token" "${token}"
rm -f "${t3dir}/antigravity-oauth-token"
ok "antigravity copy: a symlinked token is refused" bash -c '[[ "$(share)" == refused* && ! -e "$1/antigravity-oauth-token" ]]' _ "${t3dir}"
rm "${token}"
mv "${HOME}/.gemini/antigravity-cli" "${tmp}/agy-cli"
ln -s "${tmp}/agy-cli" "${HOME}/.gemini/antigravity-cli"
mv "${tmp}/real-token" "${tmp}/agy-cli/antigravity-oauth-token"
ok "antigravity copy: a symlinked directory above the token is refused" bash -c '[[ "$(share)" == refused* && ! -e "$1/antigravity-oauth-token" ]]' _ "${t3dir}"
rm "${HOME}/.gemini/antigravity-cli"
mv "${tmp}/agy-cli" "${HOME}/.gemini/antigravity-cli"
ok "antigravity copy: copies again once the paths are plain" bash -c '[[ "$(share)" == True ]] && cmp -s "$1" "$2/antigravity-oauth-token"' _ "${token}" "${t3dir}"

# ── Ending a session ──────────────────────────────────────────────────────────
rm -f "${HOME}/.claude/.credentials.json"
: >"${FAKE_LOG}"
session closed claude-code 0
pid="$(sed -n 's/^pid //p' "${FAKE_LOG}" | tail -n 1)"
ok "end of stdin: the session ends stopped" jq_end closed '.outcome == "stopped" and .signedIn == false'
ok "end of stdin: the CLI is gone" bash -c '[[ -n "$1" ]] && ! kill -0 "$1" 2>/dev/null' _ "${pid}"

: >"${FAKE_LOG}"
timeout 60 python3 "${SCRIPT}" --session held claude-code < <(sleep 30) >"${tmp}/held.out" 2>/dev/null &
runner=$!
for _ in $(seq 1 50); do [[ -s "${tmp}/run/held.pid" ]] && break; sleep 0.1; done
ok "stop: the session has a pid file" test -s "${tmp}/run/held.pid"
python3 "${SCRIPT}" --stop --session held
wait "${runner}"
pid="$(sed -n 's/^pid //p' "${FAKE_LOG}" | tail -n 1)"
ok "stop: the CLI is gone" bash -c '[[ -n "$1" ]] && ! kill -0 "$1" 2>/dev/null' _ "${pid}"
ok "stop: the session ends stopped" jq_end held '.outcome == "stopped"'
ok "stop: no pid file left" test ! -e "${tmp}/run/held.pid"

# A new session stops one an engine restart left behind.
: >"${FAKE_LOG}"
timeout 60 python3 "${SCRIPT}" --session orphan claude-code < <(sleep 30) >"${tmp}/orphan.out" 2>/dev/null &
runner=$!
for _ in $(seq 1 50); do [[ -s "${tmp}/run/orphan.pid" ]] && break; sleep 0.1; done
FAKE_CODEX_WAIT=0 session next codex 1
wait "${runner}"
ok "one at a time: a new sign-in stops the old one" jq_end orphan '.outcome == "stopped"'

# A CLI that reads nothing: long replies pile up without blocking the loop, so the deadline and the end
# of stdin still end the sign-in.
flood() {
  local name="$1" hold="$2"
  shift 2
  {
    sleep 0.5
    python3 -c 'import json; line = json.dumps({"text": "x" * 4096}); print("\n".join([line] * 100))'
    sleep "${hold}"
  } | env "$@" FAKE_CODEX_WAIT=30 timeout 20 python3 "${SCRIPT}" --session "${name}" codex >"${tmp}/${name}.out" 2>"${tmp}/${name}.err"
}
flood flood 6 CONSTRUCT_SIGN_IN_TIMEOUT_SEC=2
ok "unread input: the deadline still ends the sign-in" jq_end flood '.outcome == "stopped" and (.text | test("too long"))'
flood flood-eof 0
ok "unread input: the end of stdin still ends the sign-in" jq_end flood-eof '.outcome == "stopped"'

# Nobody finishes it: it ends by itself.
rm -f "${HOME}/.claude/.credentials.json"
CONSTRUCT_SIGN_IN_TIMEOUT_SEC=1 session slow claude-code 4
ok "timeout: ends stopped with a note" jq_end slow '.outcome == "stopped" and (.text | test("too long"))'

# ── Not installed, usage ──────────────────────────────────────────────────────
mv "${tmp}/bin/agy" "${tmp}/agy.off"
session missing antigravity 0
ok "missing CLI: exit 127" test "$(cat "${tmp}/missing.rc")" = 127
ok "missing CLI: says so" jq_end missing '.outcome == "failed" and (.text | test("agy is not installed"))'
python3 "${SCRIPT}" --session 'a b' claude-code </dev/null >/dev/null 2>&1
ok "usage: a bad session id is refused" test "$?" = 2
python3 "${SCRIPT}" --session ok gemini </dev/null >/dev/null 2>&1
ok "usage: an unknown agent is refused" test "$?" = 2

# ── --status: each installed agent and whether it is signed in ────────────────
rm -f "${HOME}/oc-auth.json"
status="$(python3 "${SCRIPT}" --status)"
ok "status: installed agents only, each checked" jqt '. == {"claude": false, "codex": true, "opencode": false}' <<<"${status}"
mv "${tmp}/agy.off" "${tmp}/bin/agy"
status="$(python3 "${SCRIPT}" --status)"
ok "status: antigravity by its token file" jqt '.antigravity == true' <<<"${status}"

# Nothing a session showed went to stderr.
ok "no output on stderr" bash -c '! grep -l . "$1"/*.err' _ "${tmp}"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" == 0 ]]
