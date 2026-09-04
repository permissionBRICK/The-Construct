#!/usr/bin/env bash
#
# Serve the T3 Code web GUI over HTTPS with a locally trusted certificate.
#
# WHY: browsers only expose getUserMedia() on a SECURE ORIGIN, and
# http://<vm>.mshome.net:5177 is not one -- so T3's client-side microphone
# capture is invisible in the browser until the same server is reachable over
# https. Construct therefore terminates TLS in the VM with nginx and proxies to
# the unchanged `t3 serve` on 127.0.0.1:${T3CODE_PORT}. Plain HTTP on that port
# stays available for local tooling (the t3park token mint, `t3` CLI calls).
#
# T3 authenticates clients with DPoP proofs bound to the request URL, which the
# server reconstructs from the Host header and x-forwarded-proto. The proxy
# therefore MUST forward `Host: $http_host` and `X-Forwarded-Proto: https`
# verbatim, or every request fails with url_mismatch. Those two headers are the
# load-bearing part of the generated site file.
#
# Idempotent; safe to re-run. Run as root.
#
# Inputs (all via environment, with config.env / defaults as fallback):
#   T3CODE_PORT              plain-HTTP port `t3 serve` listens on (default 5177)
#   T3CODE_HTTPS             enable the TLS proxy       (default true)
#   T3CODE_HTTPS_PORT        TLS listen port            (default 5178)
#   T3CODE_PUBLIC_PORT       port a CLIENT reaches the TLS listener on
#                            (default: T3CODE_HTTPS_PORT). It differs only on a
#                            service-managed VM, where the host service forwards
#                            the listener on a public port of its own choosing
#                            (plan section 4.12) -- the ADVERTISED origin has to
#                            be that port, or T3's pairing links point at a port
#                            nothing listens on. Never changes what nginx binds.
#   CONSTRUCT_EXTERNAL_HOST  client-reachable name/IP   (default $(hostname).mshome.net)
#   CONFIG_FILE              construct config.env       (default /etc/construct/config.env)
#   REPO_DIR                 uploaded repo              (default /opt/construct/repo)
#
# Usage:
#   setup-t3-https.sh              reconcile to the resolved T3CODE_HTTPS setting
#   setup-t3-https.sh --teardown   remove the proxy WITHOUT touching the saved
#                                  T3CODE_HTTPS preference. Used when T3 Code
#                                  itself is switched off: an https listener in
#                                  front of a stopped server only serves 502s,
#                                  but the user's HTTPS choice must survive so
#                                  re-enabling T3 restores it.
#
# Writes back into config.env: T3CODE_HTTPS, T3CODE_HTTPS_PORT and
# T3CODE_PUBLIC_BASE_URL.
#
# TWO DIFFERENT SIGNALS, deliberately:
#   T3CODE_HTTPS           the user's PREFERENCE. Kept as-is across a failed
#                          setup so the next provision retries.
#   T3CODE_PUBLIC_BASE_URL the EFFECTIVE origin -- written only when the proxy
#                          actually came up, and cleared on every failure path
#                          (offline apt, openssl, nginx). Everything that
#                          advertises or opens a URL (the patched T3 server, the
#                          control panel's probe + pairing scripts, the console
#                          banner) keys off THIS, so a failed setup degrades to
#                          plain http instead of pointing at a dead listener.
#                          Empty/absent = today's http behaviour.
#
set -euo pipefail

# Colourised logging helpers, same convention as bin/install-ai-tools.sh.
if [[ -t 1 || -t 2 || -n "${FORCE_COLOR:-}" || -n "${CLICOLOR_FORCE:-}" ]]; then
  _C_STEP=$'\033[1;36m'   # bold cyan - step headers
  _C_OK=$'\033[32m'       # green     - completion / success
  _C_WARN=$'\033[33m'     # yellow    - warnings (run continues)
  _C_ERR=$'\033[31m'      # red       - fatal errors (before exit)
  _C_DIM=$'\033[2m'       # dim       - idempotent "nothing to do" / detail
  _C_RESET=$'\033[0m'
else
  _C_STEP=''; _C_OK=''; _C_WARN=''; _C_ERR=''; _C_DIM=''; _C_RESET=''
fi
step() { printf '%s==> %s%s\n' "${_C_STEP}" "$*" "${_C_RESET}"; }
ok()   { printf '%s%s%s\n'     "${_C_OK}"   "$*" "${_C_RESET}"; }
warn() { printf '%s%s%s\n'     "${_C_WARN}" "$*" "${_C_RESET}" >&2; }
err()  { printf '%s%s%s\n'     "${_C_ERR}"  "$*" "${_C_RESET}" >&2; }
note() { printf '%s%s%s\n'     "${_C_DIM}"  "$*" "${_C_RESET}"; }

REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
TLS_DIR="${T3CODE_TLS_DIR:-/etc/construct/tls}"
STATUS_FILE="${T3CODE_HTTPS_STATUS_FILE:-/etc/construct/t3code-https-status}"
NGINX_AVAILABLE_DIR="${T3CODE_NGINX_AVAILABLE_DIR:-/etc/nginx/sites-available}"
NGINX_ENABLED_DIR="${T3CODE_NGINX_ENABLED_DIR:-/etc/nginx/sites-enabled}"
SITE_NAME="construct-t3"

# The plain-Bash unit tests source this file for its pure helpers alone: skip the
# root precondition and everything that touches the VM.
_FUNCS_ONLY="${CONSTRUCT_T3_HTTPS_FUNCS_ONLY:-false}"

# ── Pure helpers (unit-tested; no filesystem, no network) ────────────────────

# Is the value an IP literal (v4 or v6) rather than a DNS name? Decides whether
# it becomes an `IP:` or a `DNS:` SAN -- a certificate with the address only in
# a DNS SAN is rejected by every browser when you dial the address.
t3_https_is_ip_literal() {
  local v="$1"
  [[ -n "${v}" ]] || return 1
  # IPv6 literals are the only values carrying a colon here (a scope suffix like
  # fe80::1%12 counts too; openssl rejects it, so the caller drops what it can't use).
  [[ "${v}" == *:* ]] && return 0
  [[ "${v}" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]
}

# Host as it appears inside a URL: IPv6 literals must be bracketed
# (https://[2001:db8::1]:5178), names and IPv4 pass through unchanged. Same rule
# print-connection-info.sh uses for its service URLs.
t3_https_url_host() {
  local v="$1"
  if [[ "${v}" == *:* ]]; then printf '[%s]' "${v}"; else printf '%s' "${v}"; fi
}

# The client-reachable host for the public URL: CONSTRUCT_EXTERNAL_HOST when set,
# else <hostname>.mshome.net -- the SAME rule the control panel's pairing script
# (extension/src/t3code.js) applies, so both ends agree on the origin the DPoP
# proofs are bound to.
t3_https_public_host() {
  local external="$1" hostname="$2"
  if [[ -n "${external}" ]]; then printf '%s' "${external}"; else printf '%s.mshome.net' "${hostname}"; fi
}

# The value written to T3CODE_PUBLIC_BASE_URL. No trailing slash, no path: the
# patched T3 server treats it as an origin.
t3_https_public_base_url() {
  local host="$1" port="$2"
  printf 'https://%s:%s' "$(t3_https_url_host "${host}")" "${port}"
}

# The leaf certificate's SAN list, comma-joined in openssl's syntax. Order is
# stable and duplicates are dropped so the persisted list can be compared
# verbatim against the one a later run wants (that comparison is what decides
# whether the leaf is regenerated).
t3_https_san_list() {
  local hostname="$1" external="$2" primary_ip="$3"
  local -a sans=()
  local candidate entry seen
  _t3_san_push() {
    local e="$1"
    for seen in ${sans[@]+"${sans[@]}"}; do
      [[ "${seen}" == "${e}" ]] && return 0
    done
    sans+=("${e}")
  }
  [[ -n "${hostname}" ]] && _t3_san_push "DNS:${hostname}.mshome.net"
  [[ -n "${hostname}" ]] && _t3_san_push "DNS:${hostname}"
  _t3_san_push "DNS:localhost"
  _t3_san_push "IP:127.0.0.1"
  for candidate in "${external}" "${primary_ip}"; do
    [[ -n "${candidate}" ]] || continue
    # A scoped IPv6 literal (fe80::1%12) is not a valid SAN value; skip it rather
    # than emit a certificate request openssl will reject.
    [[ "${candidate}" == *%* ]] && continue
    if t3_https_is_ip_literal "${candidate}"; then entry="IP:${candidate}"; else entry="DNS:${candidate}"; fi
    _t3_san_push "${entry}"
  done
  local out=""
  for entry in ${sans[@]+"${sans[@]}"}; do
    out="${out:+${out},}${entry}"
  done
  printf '%s' "${out}"
}

# Why the leaf has to be reissued -- "" means keep the existing one. Pure so the
# policy (SAN drift, the 60-day renewal window) is testable without openssl:
#   have_sans  the SAN list recorded next to the certificate (t3.sans)
#   want_sans  the list this run would issue
#   not_after  the certificate's expiry as a unix timestamp ("" = no certificate)
#   now        current unix timestamp
t3_https_leaf_regen_reason() {
  local have_sans="$1" want_sans="$2" not_after="$3" now="$4"
  if [[ -z "${not_after}" || ! "${not_after}" =~ ^[0-9]+$ ]]; then
    printf 'missing'; return 0
  fi
  if [[ "${have_sans}" != "${want_sans}" ]]; then
    printf 'sans-changed'; return 0
  fi
  # 60 days of runway: a VM that is only reprovisioned occasionally must never
  # hand the browser an expired certificate between two runs.
  if (( not_after - now < 60 * 86400 )); then
    printf 'expiring'; return 0
  fi
  printf ''
}

# The nginx site. Rendered by a pure function so the unit tests can assert every
# directive that makes T3 work through a proxy:
#   Host / X-Forwarded-Proto  -> DPoP URL reconstruction (else url_mismatch)
#   Upgrade / Connection      -> the RPC WebSocket
#   proxy_read/send_timeout   -> long-lived sockets aren't cut at nginx's 60s default
#   client_max_body_size 0    -> attachment uploads are not capped by the proxy
#   proxy_buffering off       -> streamed responses reach the client as they are produced
t3_https_nginx_site() {
  local https_port="$1" http_port="$2" cert="$3" key="$4" include_ipv6="${5:-true}"
  printf '%s\n' "# Managed by Construct (bin/setup-t3-https.sh) -- regenerated on every provision."
  printf '%s\n' "# Terminates TLS for the T3 Code web GUI and proxies to the local \`t3 serve\`."
  printf '%s\n' ""
  printf '%s\n' "map \$http_upgrade \$connection_upgrade {"
  printf '%s\n' "    default upgrade;"
  printf '%s\n' "    ''      close;"
  printf '%s\n' "}"
  printf '%s\n' ""
  printf '%s\n' "server {"
  printf '    listen 0.0.0.0:%s ssl;\n' "${https_port}"
  if [[ "${include_ipv6}" == "true" ]]; then
    printf '    listen [::]:%s ssl;\n' "${https_port}"
  fi
  printf '%s\n' "    server_name _;"
  printf '%s\n' ""
  printf '    ssl_certificate     %s;\n' "${cert}"
  printf '    ssl_certificate_key %s;\n' "${key}"
  printf '%s\n' "    ssl_protocols TLSv1.2 TLSv1.3;"
  printf '%s\n' "    ssl_prefer_server_ciphers off;"
  printf '%s\n' ""
  printf '%s\n' "    # T3 uploads attachments through the same origin; the proxy must not cap them."
  printf '%s\n' "    client_max_body_size 0;"
  printf '%s\n' ""
  printf '%s\n' "    location / {"
  printf '        proxy_pass http://127.0.0.1:%s;\n' "${http_port}"
  printf '%s\n' "        proxy_http_version 1.1;"
  printf '%s\n' "        proxy_set_header Upgrade \$http_upgrade;"
  printf '%s\n' "        proxy_set_header Connection \$connection_upgrade;"
  printf '%s\n' "        # DPoP proofs are bound to the reconstructed request URL: T3 rebuilds it"
  printf '%s\n' "        # from these two headers, so they must survive the proxy unchanged."
  printf '%s\n' "        proxy_set_header Host \$http_host;"
  printf '%s\n' "        proxy_set_header X-Forwarded-Proto https;"
  printf '%s\n' "        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;"
  printf '%s\n' "        proxy_set_header X-Real-IP \$remote_addr;"
  printf '%s\n' "        proxy_read_timeout 1h;"
  printf '%s\n' "        proxy_send_timeout 1h;"
  printf '%s\n' "        proxy_buffering off;"
  printf '%s\n' "        proxy_request_buffering off;"
  printf '%s\n' "    }"
  printf '%s\n' "}"
}

# Sourced for the pure helpers only (unit tests): stop before anything runs.
if [[ "${_FUNCS_ONLY}" == "true" ]]; then
  return 0 2>/dev/null || exit 0
fi

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo bash ${REPO_DIR}/bin/setup-t3-https.sh"
  exit 1
fi

_teardown=false
case "${1:-}" in
  "") ;;
  --teardown) _teardown=true ;;
  *) err "Unknown argument: $1 (expected --teardown or nothing)"; exit 1 ;;
esac

# ── Settings resolution ──────────────────────────────────────────────────────
# Precedence: explicit environment (from provision.sh / install-ai-tools.sh) >
# value saved in config.env > built-in default. config.env is READ, never
# sourced, so a malformed line elsewhere can't abort us and a passed-in variable
# of the same name isn't clobbered (same idiom as setup-smb-share.sh).
_cfg_unquote() {
  local v="$1"
  if [[ ${#v} -ge 2 && "${v}" == \'*\' ]]; then
    v="${v:1:${#v}-2}"
    v="${v//\'\\\'\'/\'}"
  fi
  printf '%s' "${v}"
}
read_cfg() {
  [[ -f "${CONFIG_FILE}" ]] || return 0
  _cfg_unquote "$(sed -n "s/^$1=//p" "${CONFIG_FILE}" | head -1)"
}

T3CODE_PORT="${T3CODE_PORT:-$(read_cfg T3CODE_PORT)}";             T3CODE_PORT="${T3CODE_PORT:-5177}"
T3CODE_HTTPS="${T3CODE_HTTPS:-$(read_cfg T3CODE_HTTPS)}";          T3CODE_HTTPS="${T3CODE_HTTPS:-true}"
T3CODE_HTTPS_PORT="${T3CODE_HTTPS_PORT:-$(read_cfg T3CODE_HTTPS_PORT)}"; T3CODE_HTTPS_PORT="${T3CODE_HTTPS_PORT:-5178}"
# The ADVERTISED port. STATED only on a service-managed VM, where the host service
# forwards the listener on a public port of its own (plan section 4.12); everywhere else
# it is empty and the listener's own port is advertised -- byte-identical to before.
# The two are kept apart because the DISABLED path below can only advertise a plain-HTTP
# origin when somebody really stated a forwarded port.
T3CODE_PUBLIC_PORT_STATED="${T3CODE_PUBLIC_PORT:-$(read_cfg T3CODE_PUBLIC_PORT)}"
T3CODE_PUBLIC_PORT="${T3CODE_PUBLIC_PORT_STATED:-${T3CODE_HTTPS_PORT}}"
CONSTRUCT_EXTERNAL_HOST="${CONSTRUCT_EXTERNAL_HOST:-$(read_cfg CONSTRUCT_EXTERNAL_HOST)}"
[[ "${T3CODE_HTTPS}" == "true" ]] || T3CODE_HTTPS=false
[[ "${_teardown}" == "true" ]] && T3CODE_HTTPS=false
for _port_var in T3CODE_PORT T3CODE_HTTPS_PORT T3CODE_PUBLIC_PORT; do
  if ! [[ "${!_port_var}" =~ ^[0-9]{1,5}$ ]] || (( ${!_port_var} < 1 || ${!_port_var} > 65535 )); then
    err "${_port_var}=${!_port_var} is not a valid TCP port"
    exit 1
  fi
done
if [[ "${T3CODE_PORT}" == "${T3CODE_HTTPS_PORT}" ]]; then
  err "T3CODE_HTTPS_PORT (${T3CODE_HTTPS_PORT}) must differ from T3CODE_PORT (${T3CODE_PORT})"
  exit 1
fi

cfg() { bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" "$1" "$2"; }
# Clear a key only when it currently carries a value, so a VM that never had
# HTTPS configured keeps its config.env free of the key entirely (an absent
# T3CODE_PUBLIC_BASE_URL is exactly what "today's behaviour" means to the server).
cfg_clear() {
  local key="$1"
  [[ -n "$(read_cfg "${key}")" ]] || return 0
  cfg "${key}" ""
}

SITE_FILE="${NGINX_AVAILABLE_DIR}/${SITE_NAME}"
SITE_LINK="${NGINX_ENABLED_DIR}/${SITE_NAME}"
CA_KEY="${TLS_DIR}/ca.key"
CA_CRT="${TLS_DIR}/ca.crt"
# World-readable copy of the CA certificate for the Windows host to fetch (see
# where it is written, below).
CA_HANDOFF="${T3CODE_CA_HANDOFF:-/etc/construct/t3code-ca.crt}"
LEAF_KEY="${TLS_DIR}/t3.key"
LEAF_CRT="${TLS_DIR}/t3.crt"
LEAF_SANS="${TLS_DIR}/t3.sans"

# nginx -t, then reload (or start when it isn't running yet).
#
# The distro default site is left alone unless it is what stops nginx from coming
# up. "Unless" is PROVEN, not assumed: the symlink is moved aside reversibly and
# the start retried once; it stays removed only when that retry succeeds, and is
# put back byte-for-byte when the failure was something else (our own port
# already in use, a broken unit, ...). Deleting an unrelated site on no evidence
# would be a silent, unrecoverable change to someone else's web server.
nginx_apply() {
  local test_out default_link="${NGINX_ENABLED_DIR}/default" default_target
  if ! test_out="$(nginx -t 2>&1)"; then
    err "nginx rejected the generated ${SITE_NAME} site:"
    printf '%s\n' "${test_out}" >&2
    return 1
  fi
  systemctl enable nginx >/dev/null 2>&1 || true
  if systemctl is-active --quiet nginx; then
    systemctl reload nginx && return 0
  else
    systemctl start nginx && return 0
  fi
  if [[ -L "${default_link}" ]]; then
    default_target="$(readlink "${default_link}" 2>/dev/null || true)"
    warn "nginx did not come up; trying once without the distro default site"
    rm -f "${default_link}"
    if nginx -t >/dev/null 2>&1 && systemctl restart nginx; then
      warn "removed ${default_link} (it was what prevented nginx from starting)"
      return 0
    fi
    if [[ -n "${default_target}" ]]; then
      ln -sfn "${default_target}" "${default_link}"
      warn "the distro default site was not the cause; restored ${default_link}"
    fi
  fi
  err "nginx failed to start; recent status and logs:"
  systemctl --no-pager --full status nginx >&2 || true
  journalctl -u nginx --no-pager -n 30 >&2 || true
  return 1
}

# ── Disabled path ────────────────────────────────────────────────────────────
# Tear the proxy down, settle the public base URL and drop the host handoff file. The
# CA and the leaf are KEPT: re-enabling must not invalidate the trust the user already
# imported on Windows.
#
# THE PUBLIC BASE URL HAS TWO ANSWERS HERE:
#   * no forwarded port stated (every local install, and the teardown that switches T3
#     itself off) -> CLEARED, so the patched T3 server falls back to its plain-http
#     origin on the next start. Byte-identical to before this batch.
#   * a forwarded port stated (a service-managed VM with T3CODE_HTTPS=false, whose
#     plain listener the host service publishes -- bin/provision.sh requests the
#     forward for T3CODE_PORT in that mode) -> the FORWARDED http origin. Without it
#     the VM would advertise http://<publicHost>:5177, the VM-INTERNAL port, which no
#     remote client can reach (plan section 4.12).
if [[ "${T3CODE_HTTPS}" != "true" ]]; then
  if [[ "${_teardown}" == "true" ]]; then
    step "Removing the T3 Code HTTPS proxy (T3 Code itself is off; the HTTPS preference is kept)"
  else
    step "T3 Code HTTPS disabled (T3CODE_HTTPS=${T3CODE_HTTPS})"
    cfg T3CODE_HTTPS false
  fi
  if [[ "${_teardown}" != "true" && -n "${T3CODE_PUBLIC_PORT_STATED}" ]]; then
    _disabled_public_host="$(t3_https_public_host "${CONSTRUCT_EXTERNAL_HOST}" "$(hostname 2>/dev/null || echo vm)")"
    cfg T3CODE_PUBLIC_BASE_URL "http://$(t3_https_url_host "${_disabled_public_host}"):${T3CODE_PUBLIC_PORT_STATED}"
    note "advertising the forwarded plain-HTTP origin http://$(t3_https_url_host "${_disabled_public_host}"):${T3CODE_PUBLIC_PORT_STATED}"
  else
    cfg_clear T3CODE_PUBLIC_BASE_URL
  fi
  rm -f "${STATUS_FILE}"
  if [[ -e "${SITE_LINK}" || -L "${SITE_LINK}" ]]; then
    rm -f "${SITE_LINK}"
    note "removed ${SITE_LINK}"
    if command -v nginx >/dev/null 2>&1; then
      nginx_apply || warn "nginx could not be reloaded after removing the T3 site"
    fi
  fi
  rm -f "${SITE_FILE}"
  ok "T3 Code HTTPS is off; plain HTTP on :${T3CODE_PORT} is unchanged"
  exit 0
fi

step "Setting up HTTPS for the T3 Code web GUI (:${T3CODE_HTTPS_PORT} -> 127.0.0.1:${T3CODE_PORT})"

# ── 1. Packages ──────────────────────────────────────────────────────────────
# An offline apt must degrade to a warning, never fail provisioning -- but it
# also must not leave config.env advertising an https origin nothing serves.
missing_pkgs=()
command -v nginx   >/dev/null 2>&1 || missing_pkgs+=(nginx)
command -v openssl >/dev/null 2>&1 || missing_pkgs+=(openssl)
if (( ${#missing_pkgs[@]} > 0 )); then
  step "Installing ${missing_pkgs[*]}"
  export DEBIAN_FRONTEND=noninteractive
  apt-get update || true
  apt-get install -y "${missing_pkgs[@]}" || true
fi
for _need in nginx openssl; do
  if ! command -v "${_need}" >/dev/null 2>&1; then
    warn "WARNING: ${_need} is not installed and could not be fetched; T3 Code keeps serving plain HTTP on :${T3CODE_PORT}"
    # Preference kept (retry next provision), effective origin cleared.
    cfg T3CODE_HTTPS true
    cfg_clear T3CODE_PUBLIC_BASE_URL
    rm -f "${STATUS_FILE}"
    exit 0
  fi
done

# ── 2. Certificates ──────────────────────────────────────────────────────────
install -d -m 0700 "${TLS_DIR}"

vm_hostname="$(hostname 2>/dev/null || echo vm)"
primary_ip="$(ip -o -4 route get 1.1.1.1 2>/dev/null | awk '{for (i=1; i<=NF; i++) if ($i == "src") {print $(i+1); exit}}' || true)"
[[ -n "${primary_ip}" ]] || primary_ip="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
public_host="$(t3_https_public_host "${CONSTRUCT_EXTERNAL_HOST}" "${vm_hostname}")"
want_sans="$(t3_https_san_list "${vm_hostname}" "${CONSTRUCT_EXTERNAL_HOST}" "${primary_ip}")"

if [[ ! -s "${CA_KEY}" || ! -s "${CA_CRT}" ]]; then
  step "Creating the Construct local CA (${TLS_DIR}/ca.crt)"
  rm -f "${CA_KEY}" "${CA_CRT}"
  ( umask 077
    openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
      -keyout "${CA_KEY}" -out "${CA_CRT}" \
      -subj "/CN=Construct Local CA (${vm_hostname})" \
      -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
      -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1 )
  # A half-written CA must never be left behind for the next run to "reuse".
  if [[ ! -s "${CA_KEY}" || ! -s "${CA_CRT}" ]]; then
    rm -f "${CA_KEY}" "${CA_CRT}"
    err "could not create the local CA (openssl failed)"
    exit 1
  fi
  # The leaf can no longer chain to the previous CA.
  rm -f "${LEAF_CRT}" "${LEAF_SANS}"
else
  note "reusing the existing Construct local CA"
fi
chmod 0600 "${CA_KEY}"
chmod 0644 "${CA_CRT}"

have_sans=""
[[ -f "${LEAF_SANS}" ]] && have_sans="$(cat "${LEAF_SANS}")"
leaf_not_after=""
if [[ -s "${LEAF_CRT}" ]]; then
  _end="$(openssl x509 -in "${LEAF_CRT}" -noout -enddate 2>/dev/null | sed -n 's/^notAfter=//p')"
  [[ -n "${_end}" ]] && leaf_not_after="$(date -d "${_end}" +%s 2>/dev/null || true)"
fi
regen_reason="$(t3_https_leaf_regen_reason "${have_sans}" "${want_sans}" "${leaf_not_after}" "$(date +%s)")"
if [[ -z "${regen_reason}" && ! -s "${LEAF_KEY}" ]]; then
  regen_reason="missing"
fi
# A restored (or re-created) CA leaves a leaf that no longer verifies; reissue it
# rather than serving a chain the imported root doesn't cover.
if [[ -z "${regen_reason}" ]] && ! openssl verify -CAfile "${CA_CRT}" "${LEAF_CRT}" >/dev/null 2>&1; then
  regen_reason="ca-mismatch"
fi

if [[ -n "${regen_reason}" ]]; then
  step "Issuing the T3 server certificate (${regen_reason})"
  note "  SANs: ${want_sans}"
  ext_file="$(mktemp)"
  csr_file="$(mktemp)"
  {
    printf 'basicConstraints=critical,CA:FALSE\n'
    printf 'keyUsage=critical,digitalSignature,keyEncipherment\n'
    printf 'extendedKeyUsage=serverAuth\n'
    printf 'subjectAltName=%s\n' "${want_sans}"
  } >"${ext_file}"
  leaf_ok=true
  ( umask 077
    openssl req -newkey rsa:2048 -sha256 -nodes -keyout "${LEAF_KEY}" -out "${csr_file}" \
      -subj "/CN=${public_host}" >/dev/null 2>&1 ) || leaf_ok=false
  if [[ "${leaf_ok}" == true ]]; then
    openssl x509 -req -in "${csr_file}" -CA "${CA_CRT}" -CAkey "${CA_KEY}" -CAcreateserial \
      -days 825 -sha256 -extfile "${ext_file}" -out "${LEAF_CRT}" >/dev/null 2>&1 || leaf_ok=false
  fi
  rm -f "${ext_file}" "${csr_file}"
  if [[ "${leaf_ok}" != true || ! -s "${LEAF_CRT}" ]]; then
    err "could not issue the T3 server certificate (openssl failed); leaving HTTPS unconfigured"
    rm -f "${LEAF_CRT}" "${LEAF_SANS}"
    # Preference kept (retry next provision), effective origin cleared.
    cfg T3CODE_HTTPS true
    cfg_clear T3CODE_PUBLIC_BASE_URL
    rm -f "${STATUS_FILE}"
    exit 1
  fi
  printf '%s' "${want_sans}" >"${LEAF_SANS}"
else
  note "existing T3 server certificate still covers ${want_sans}"
fi
chmod 0600 "${LEAF_KEY}"
chmod 0644 "${LEAF_CRT}" "${LEAF_SANS}"

# ── 3. nginx site ────────────────────────────────────────────────────────────
include_ipv6=true
[[ -e /proc/net/if_inet6 ]] || include_ipv6=false
install -d -m 0755 "${NGINX_AVAILABLE_DIR}" "${NGINX_ENABLED_DIR}"
site_tmp="$(mktemp)"
t3_https_nginx_site "${T3CODE_HTTPS_PORT}" "${T3CODE_PORT}" "${LEAF_CRT}" "${LEAF_KEY}" "${include_ipv6}" >"${site_tmp}"
install -m 0644 "${site_tmp}" "${SITE_FILE}"
rm -f "${site_tmp}"
ln -sfn "${SITE_FILE}" "${SITE_LINK}"
if ! nginx_apply; then
  err "the TLS proxy is not serving; T3 Code keeps working over plain HTTP on :${T3CODE_PORT}"
  # Preference kept (retry next provision), effective origin cleared -- so the
  # panel, the pairing links and the banner all stay on http.
  cfg T3CODE_HTTPS true
  cfg_clear T3CODE_PUBLIC_BASE_URL
  rm -f "${STATUS_FILE}"
  exit 1
fi

# ── 4. Persist the contract + the host handoff ───────────────────────────────
# The ADVERTISED origin, on the port a CLIENT reaches -- the host forward's public
# port on a service-managed VM, the listener's port everywhere else.
public_base_url="$(t3_https_public_base_url "${public_host}" "${T3CODE_PUBLIC_PORT}")"
cfg T3CODE_HTTPS true
cfg T3CODE_HTTPS_PORT "${T3CODE_HTTPS_PORT}"
cfg T3CODE_PUBLIC_BASE_URL "${public_base_url}"

ca_sha256="$(openssl x509 -in "${CA_CRT}" -noout -fingerprint -sha256 2>/dev/null | sed 's/^.*=//; s/://g' | tr 'A-F' 'a-f')"
ca_thumbprint="$(openssl x509 -in "${CA_CRT}" -noout -fingerprint -sha1 2>/dev/null | sed 's/^.*=//; s/://g' | tr 'a-f' 'A-F')"
# The key material's directory is 0700, which a provisioning SSH login as the
# SEED user (the bootstrap-key path) cannot traverse -- so publish the CA
# CERTIFICATE, which is public by definition, at a readable path for the host to
# scp. Same file, same fingerprint; only the private keys stay behind the 0700.
install -d -m 0755 "$(dirname "${CA_HANDOFF}")" "$(dirname "${STATUS_FILE}")"
install -m 0644 "${CA_CRT}" "${CA_HANDOFF}"
{
  printf 'T3CODE_HTTPS_READY=yes\n'
  printf 'T3CODE_HTTPS_PORT=%s\n'      "${T3CODE_HTTPS_PORT}"
  printf 'T3CODE_PUBLIC_BASE_URL=%s\n' "${public_base_url}"
  printf 'T3CODE_CA_PATH=%s\n'         "${CA_CRT}"
  printf 'T3CODE_CA_HANDOFF=%s\n'      "${CA_HANDOFF}"
  printf 'T3CODE_CA_SHA256=%s\n'       "${ca_sha256}"
  printf 'T3CODE_CA_THUMBPRINT=%s\n'   "${ca_thumbprint}"
} >"${STATUS_FILE}"
# Public certificate metadata only -- no key material, so it stays world-readable
# like the other status files the host reads back.
chmod 0644 "${STATUS_FILE}"

# Open the firewall for the TLS port if ufw is active (a no-op on the default
# open VM). Capture the status first -- piping `ufw status` into grep would
# SIGPIPE ufw and, under `set -o pipefail`, misreport the result.
if command -v ufw >/dev/null 2>&1; then
  _ufw_status="$(ufw status 2>/dev/null || true)"
  if [[ "${_ufw_status}" == *"Status: active"* ]]; then
    if ufw allow "${T3CODE_HTTPS_PORT}/tcp" >/dev/null 2>&1; then
      note "opened the firewall for T3 HTTPS (ufw, ${T3CODE_HTTPS_PORT}/tcp)"
    else
      warn "could not open ${T3CODE_HTTPS_PORT}/tcp in ufw; the https URL may be unreachable from your PC"
    fi
  fi
fi

ok "T3 Code is served over ${public_base_url} (CA: ${CA_CRT})"
