#!/usr/bin/env bash
# Tests for `construct config` (bin/construct-config.sh): the allowlisted per-VM
# settings an agent may change. Run: bash test/construct-config.test.sh
#
# Everything runs against a config.env in a temp directory; nothing touches the
# real /etc/construct.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="${ROOT}/bin/construct"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

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

cfgdir="${tmp}/etc"
cfg="${cfgdir}/config.env"
out="${tmp}/out"
err="${tmp}/err"

seed() {
  rm -rf "${cfgdir}"
  mkdir -p "${cfgdir}"
  printf 'AGENT_NAME=dev\nGIT_USER_NAME='"'"'Jane Doe'"'"'\nT3CODE=true\nCONSTRUCT_SERVICE_URL=https://host.example:7462\n' >"${cfg}"
  chmod 0600 "${cfg}"
}
# config <args...>: runs the CLI through the dispatcher; stdout → $out, stderr → $err, returns its status.
config() { CONFIG_FILE="${cfg}" bash "${CLI}" config "$@" >"${out}" 2>"${err}"; }
status_of() { config "$@"; printf '%s' "$?"; }
line_of() { grep "^$1=" "${cfg}"; }
unchanged() { cmp -s "${tmp}/before" "${cfg}"; }
no_leftovers() { test -z "$(find "${cfgdir}" -name '.config.env.*')"; }

# ── help ─────────────────────────────────────────────────────────────────────
seed
ok "--help exits 0" test "$(status_of --help)" = 0
ok "--help names the key" grep -q '^  t3-proxy-url' "${out}"
ok "--help names the config.env key" grep -q 'T3CODE_PROXY_URL' "${out}"
ok "--help documents the exit codes" grep -q 'unknown key or invalid value' "${out}"
ok "help is an alias" test "$(status_of help)" = 0
ok "no command prints the usage to stderr and exits 1" sh -c "test \"\$(CONFIG_FILE='${cfg}' bash '${CLI}' config 2>&1 >/dev/null | head -1)\" = 'Usage: construct config list' && ! CONFIG_FILE='${cfg}' bash '${CLI}' config 2>/dev/null"
ok "an unknown command is a usage error" test "$(status_of frobnicate)" = 1

# ── get / set / unset ────────────────────────────────────────────────────────
seed
ok "get of an unset key succeeds" test "$(status_of get t3-proxy-url)" = 0
ok "get of an unset key prints nothing" test ! -s "${out}"
cp "${cfg}" "${tmp}/before"
ok "set accepts an https origin with a port" test "$(status_of set t3-proxy-url https://t3.example.net:8443)" = 0
ok "set confirms the stored value" test "$(cat "${out}")" = "t3-proxy-url=https://t3.example.net:8443"
ok "set writes T3CODE_PROXY_URL" test "$(line_of T3CODE_PROXY_URL)" = "T3CODE_PROXY_URL=https://t3.example.net:8443"
ok "set keeps every other line byte for byte" test "$(grep -v '^T3CODE_PROXY_URL=' "${cfg}")" = "$(cat "${tmp}/before")"
ok "set keeps the file mode" test "$(stat -c %a "${cfg}")" = 600
ok "set leaves no temp file" no_leftovers
ok "get prints the value" sh -c "CONFIG_FILE='${cfg}' bash '${CLI}' config get t3-proxy-url | grep -qx 'https://t3.example.net:8443'"
ok "set replaces the value" test "$(status_of set t3-proxy-url http://t3.example.net)" = 0
ok "a replaced value stays one line" test "$(grep -c '^T3CODE_PROXY_URL=' "${cfg}")" = 1
ok "plain http is accepted" test "$(line_of T3CODE_PROXY_URL)" = "T3CODE_PROXY_URL=http://t3.example.net"
config set t3-proxy-url 'https://t3.example.net:8443/'
ok "one trailing slash is dropped" test "$(line_of T3CODE_PROXY_URL)" = "T3CODE_PROXY_URL=https://t3.example.net:8443"
config set t3-proxy-url 'https://[2001:db8::7]:8443'
ok "an IPv6 literal is stored quoted (config-set.sh's renderer)" test "$(line_of T3CODE_PROXY_URL)" = "T3CODE_PROXY_URL='https://[2001:db8::7]:8443'"
config get t3-proxy-url
ok "get unquotes it" test "$(cat "${out}")" = 'https://[2001:db8::7]:8443'
config list
ok "list shows the key and value" test "$(cat "${out}")" = 't3-proxy-url=https://[2001:db8::7]:8443'
ok "the stored file is still sourceable" bash -c "set -e; . '${cfg}'; test \"\${T3CODE_PROXY_URL}\" = 'https://[2001:db8::7]:8443'"
ok "unset succeeds" test "$(status_of unset t3-proxy-url)" = 0
ok "unset removes the line" sh -c "! grep -q '^T3CODE_PROXY_URL=' '${cfg}'"
ok "unset keeps the other lines" test "$(cat "${cfg}")" = "$(cat "${tmp}/before")"
ok "unset keeps the file mode" test "$(stat -c %a "${cfg}")" = 600
cp "${cfg}" "${tmp}/before"
ok "unset of an absent key succeeds" test "$(status_of unset t3-proxy-url)" = 0
ok "unset of an absent key leaves the file alone" unchanged
config list
ok "list shows an unset key as empty" test "$(cat "${out}")" = 't3-proxy-url='

# ── validation ───────────────────────────────────────────────────────────────
seed
config set t3-proxy-url https://t3.example.net:8443
cp "${cfg}" "${tmp}/before"
for bad in 'https://t3.example.net:8443/app' 'https://t3.example.net/?x=1' 'https://t3.example.net#frag' \
           'ftp://t3.example.net' 't3.example.net:8443' 'https://t3.example.net:0' 'https://t3.example.net:70000' \
           'https://user@t3.example.net' 'https://t3 example.net' "https://t3.example.net'" '' 'https://' \
           "$(printf 'https://t3.example.net\nT3CODE=false')"; do
  ok "set refuses $(printf '%q' "${bad}") with exit 2" test "$(status_of set t3-proxy-url "${bad}")" = 2
  ok "  ...and leaves config.env unchanged" unchanged
done
ok "the refusal says what is accepted" grep -q 'e.g. https://t3.example.net:8443' "${err}"
for key in T3CODE_PROXY_URL CONSTRUCT_SERVICE_URL service-url t3code ../config AGENT_NAME; do
  ok "set refuses the key ${key} with exit 2" test "$(status_of set "${key}" https://t3.example.net)" = 2
  ok "  ...and leaves config.env unchanged" unchanged
  ok "get refuses the key ${key} with exit 2" test "$(status_of get "${key}")" = 2
  ok "unset refuses the key ${key} with exit 2" test "$(status_of unset "${key}")" = 2
done
ok "an unknown key names the allowed keys" grep -q 'keys: t3-proxy-url' "${err}"
ok "set without a value is a usage error" test "$(status_of set t3-proxy-url)" = 1
ok "set with extra arguments is a usage error" test "$(status_of set t3-proxy-url https://a.example b)" = 1
ok "  ...and leaves config.env unchanged" unchanged
ok "no temp file is left after refusals" no_leftovers

# ── writes ───────────────────────────────────────────────────────────────────
rm -rf "${cfgdir}"
ok "set creates a missing config.env" test "$(status_of set t3-proxy-url https://t3.example.net)" = 0
ok "  ...with only that key" test "$(cat "${cfg}")" = "T3CODE_PROXY_URL=https://t3.example.net"
ok "  ...readable by root only" test "$(stat -c %a "${cfg}")" = 600
ok "an unwritable location fails with exit 1" \
  test "$(CONFIG_FILE=/proc/construct-config-test/config.env bash "${CLI}" config set t3-proxy-url https://t3.example.net >/dev/null 2>&1; printf '%s' "$?")" = 1
seed
cp "${cfg}" "${tmp}/before"
( for i in 1 2 3 4 5 6 7 8; do config set t3-proxy-url "https://t3-${i}.example.net" & done; wait )
ok "concurrent sets leave exactly one value" test "$(grep -c '^T3CODE_PROXY_URL=' "${cfg}")" = 1
ok "concurrent sets keep the other lines" test "$(grep -v '^T3CODE_PROXY_URL=' "${cfg}")" = "$(cat "${tmp}/before")"
ok "concurrent sets leave no temp file" no_leftovers
ok "the helper is found next to the dispatcher" sh -c "cd / && CONFIG_FILE='${cfg}' CONSTRUCT_REPO_DIR=/nonexistent bash '${CLI}' config get t3-proxy-url >/dev/null"

# ── the panel's live write (extension/vm/t3-proxy-url.sh) ─────────────────────
# The host renders the guest script with a checked value; run it against this
# sandbox: through `construct config` when the VM's CLI has it, and through the
# checkout's config-set.sh when the CLI predates the verb.
if command -v node >/dev/null 2>&1; then
  guest() { # <url> <script file>
    node -e 'process.stdout.write(require(process.argv[1]).buildProxyUrlScript(process.argv[2]))' "${ROOT}/extension/src/t3code.js" "$1" \
      | sed -e "s|^CONFIG_FILE=/etc/construct/config.env$|CONFIG_FILE='${cfg}'; export CONFIG_FILE|" \
            -e "s|^renderer=/opt/construct/repo/bin/config-set.sh$|renderer='${ROOT}/bin/config-set.sh'|" >"$2"
  }
  seed
  guest https://t3.example.net:8443 "${tmp}/guest-set.sh"
  ok "the panel's script stores the address through construct config" \
    sh -c "PATH='${ROOT}/bin:${PATH}' bash '${tmp}/guest-set.sh' >/dev/null && grep -qx 'T3CODE_PROXY_URL=https://t3.example.net:8443' '${cfg}'"
  guest "" "${tmp}/guest-unset.sh"
  ok "the panel's script removes it" \
    sh -c "PATH='${ROOT}/bin:${PATH}' bash '${tmp}/guest-unset.sh' >/dev/null && ! grep -q '^T3CODE_PROXY_URL=' '${cfg}'"
  mkdir -p "${tmp}/oldcli"
  printf '#!/bin/sh\necho "Unknown command" >&2\nexit 1\n' >"${tmp}/oldcli/construct"
  chmod +x "${tmp}/oldcli/construct"
  ok "on a VM whose CLI predates the verb it falls back to config-set.sh" \
    sh -c "PATH='${tmp}/oldcli:${PATH}' bash '${tmp}/guest-set.sh' >/dev/null && grep -qx 'T3CODE_PROXY_URL=https://t3.example.net:8443' '${cfg}'"
  ok "  ...and removes it there too" \
    sh -c "PATH='${tmp}/oldcli:${PATH}' bash '${tmp}/guest-unset.sh' >/dev/null && ! grep -q '^T3CODE_PROXY_URL=' '${cfg}'"
  ok "  ...keeping the other lines" test "$(cat "${cfg}")" = "$(printf 'AGENT_NAME=dev\nGIT_USER_NAME='"'"'Jane Doe'"'"'\nT3CODE=true\nCONSTRUCT_SERVICE_URL=https://host.example:7462')"
fi

# ── lint ─────────────────────────────────────────────────────────────────────
ok "bash -n" bash -n "${ROOT}/bin/construct-config.sh"
if command -v shellcheck >/dev/null 2>&1; then
  ok "shellcheck: construct-config.sh is clean" shellcheck "${ROOT}/bin/construct-config.sh"
  ok "shellcheck: this test is clean" shellcheck "${BASH_SOURCE[0]}"
fi

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
