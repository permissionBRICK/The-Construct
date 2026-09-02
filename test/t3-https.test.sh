#!/usr/bin/env bash
# Tests for the T3 Code HTTPS front end (bin/setup-t3-https.sh) and its wiring.
#
# Two layers:
#   1. PURE functions, sourced with CONSTRUCT_T3_HTTPS_FUNCS_ONLY=true: the SAN
#      builder, the leaf-regeneration policy, the URL/host rules, and the nginx
#      site renderer (every directive T3 needs through a proxy is asserted, and
#      the rendered site is handed to `nginx -t` when nginx is installed).
#   2. The WHOLE script against a sandbox: temp config.env / TLS dir / nginx dirs
#      and stubbed nginx+systemctl, so certificate issuance, the config.env key
#      writes, the status file, idempotence and the disable/teardown paths run for
#      real without touching this machine.
#
# Run: bash test/t3-https.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/setup-t3-https.sh"

# Pull in the PURE helpers. This must happen BEFORE the test helpers are defined:
# the script ships its own `ok`/`step`/`warn` loggers (and `set -euo pipefail`),
# so we shadow the logger and undo the shell options rather than fight them.
# shellcheck disable=SC1090
CONSTRUCT_T3_HTTPS_FUNCS_ONLY=true . "${SCRIPT}"
set +e +o pipefail
set -u

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
# The inverse: the command must FAIL.
nok() {
  local name="$1"
  shift
  if "$@"; then
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "${name}"
  else
    pass=$((pass + 1))
    printf '  PASS  %s\n' "${name}"
  fi
}

tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

# ── Layer 1: pure functions ──────────────────────────────────────────────────

ok "funcs-only: sourcing installs nothing (no nginx site, no certificates)" \
  test ! -e /etc/nginx/sites-available/construct-t3

# IP vs DNS SAN classification.
ok "is-ip: IPv4 literal" t3_https_is_ip_literal "192.168.1.5"
ok "is-ip: IPv6 literal" t3_https_is_ip_literal "2001:db8::1"
nok "is-ip: a DNS name is not an IP" t3_https_is_ip_literal "vm.example.com"
nok "is-ip: empty is not an IP" t3_https_is_ip_literal ""
nok "is-ip: a name with digits is not an IP" t3_https_is_ip_literal "vm1.local"

# SAN builder: the fixed four, then the external host and the VM's own IPv4.
sans_plain="$(t3_https_san_list testvm "" "")"
ok "sans: always covers the mshome name, short name, localhost and 127.0.0.1" \
  test "${sans_plain}" = "DNS:testvm.mshome.net,DNS:testvm,DNS:localhost,IP:127.0.0.1"
sans_dns="$(t3_https_san_list testvm myhost.example.com "")"
ok "sans: a DNS external host becomes a DNS SAN" \
  test "${sans_dns}" = "DNS:testvm.mshome.net,DNS:testvm,DNS:localhost,IP:127.0.0.1,DNS:myhost.example.com"
sans_ip="$(t3_https_san_list testvm 203.0.113.7 "")"
ok "sans: an IP external host becomes an IP SAN (a DNS SAN would be rejected)" \
  test "${sans_ip}" = "DNS:testvm.mshome.net,DNS:testvm,DNS:localhost,IP:127.0.0.1,IP:203.0.113.7"
sans_v6="$(t3_https_san_list testvm 2001:db8::1 "")"
ok "sans: an IPv6 external host becomes an IP SAN" \
  test "${sans_v6}" = "DNS:testvm.mshome.net,DNS:testvm,DNS:localhost,IP:127.0.0.1,IP:2001:db8::1"
sans_lan="$(t3_https_san_list testvm "" 10.0.0.4)"
ok "sans: the VM's primary IPv4 is included" \
  test "${sans_lan}" = "DNS:testvm.mshome.net,DNS:testvm,DNS:localhost,IP:127.0.0.1,IP:10.0.0.4"
sans_dup="$(t3_https_san_list testvm testvm.mshome.net 127.0.0.1)"
ok "sans: duplicates are dropped (the list is compared verbatim later)" \
  test "${sans_dup}" = "DNS:testvm.mshome.net,DNS:testvm,DNS:localhost,IP:127.0.0.1"
sans_scoped="$(t3_https_san_list testvm "fe80::1%12" "")"
ok "sans: a scope-qualified IPv6 literal is skipped (openssl rejects it)" \
  test "${sans_scoped}" = "DNS:testvm.mshome.net,DNS:testvm,DNS:localhost,IP:127.0.0.1"

# Public host / URL rules (same precedence as the pairing script).
ok "public-host: CONSTRUCT_EXTERNAL_HOST wins" \
  test "$(t3_https_public_host myhost.example.com testvm)" = "myhost.example.com"
ok "public-host: falls back to <hostname>.mshome.net" \
  test "$(t3_https_public_host "" testvm)" = "testvm.mshome.net"
ok "public-url: https origin, no path" \
  test "$(t3_https_public_base_url testvm.mshome.net 5178)" = "https://testvm.mshome.net:5178"
ok "public-url: IPv6 hosts are bracketed" \
  test "$(t3_https_public_base_url 2001:db8::1 5178)" = "https://[2001:db8::1]:5178"

# Leaf regeneration policy.
now=1000000000
in_year=$((now + 365 * 86400))
in_month=$((now + 30 * 86400))
ok "regen: no certificate -> missing" \
  test "$(t3_https_leaf_regen_reason "" "DNS:a" "" "${now}")" = "missing"
ok "regen: unparseable expiry -> missing" \
  test "$(t3_https_leaf_regen_reason "DNS:a" "DNS:a" "not-a-date" "${now}")" = "missing"
ok "regen: SAN set changed -> sans-changed" \
  test "$(t3_https_leaf_regen_reason "DNS:a" "DNS:a,DNS:b" "${in_year}" "${now}")" = "sans-changed"
ok "regen: inside the 60-day window -> expiring" \
  test "$(t3_https_leaf_regen_reason "DNS:a" "DNS:a" "${in_month}" "${now}")" = "expiring"
ok "regen: unchanged and long-lived -> keep (empty reason)" \
  test -z "$(t3_https_leaf_regen_reason "DNS:a" "DNS:a" "${in_year}" "${now}")"
ok "regen: an already expired certificate is regenerated" \
  test "$(t3_https_leaf_regen_reason "DNS:a" "DNS:a" "$((now - 10))" "${now}")" = "expiring"

# nginx site: every directive T3 needs through a reverse proxy.
site="${tmp}/site.conf"
t3_https_nginx_site 5178 5177 /etc/construct/tls/t3.crt /etc/construct/tls/t3.key true >"${site}"
has() { grep -qF "$1" "${site}"; }
ok "site: listens on the HTTPS port over IPv4 and IPv6 with ssl" \
  sh -c "grep -qF 'listen 0.0.0.0:5178 ssl;' '${site}' && grep -qF 'listen [::]:5178 ssl;' '${site}'"
ok "site: proxies to the local t3 serve port" has 'proxy_pass http://127.0.0.1:5177;'
ok "site: HTTP/1.1 upstream (required for WebSocket upgrades)" has 'proxy_http_version 1.1;'
ok "site: forwards the WebSocket upgrade" \
  sh -c "grep -qF 'proxy_set_header Upgrade \$http_upgrade;' '${site}' && grep -qF 'proxy_set_header Connection \$connection_upgrade;' '${site}'"
ok "site: defines the standard \$connection_upgrade map" \
  sh -c "grep -qF 'map \$http_upgrade \$connection_upgrade {' '${site}' && grep -qF 'default upgrade;' '${site}'"
# The two DPoP-critical headers: T3 rebuilds the request URL from them, and a
# mismatch fails every authenticated request with url_mismatch.
ok "site: forwards Host verbatim (DPoP URL reconstruction)" has 'proxy_set_header Host $http_host;'
ok "site: forwards X-Forwarded-Proto https (DPoP URL reconstruction)" has 'proxy_set_header X-Forwarded-Proto https;'
ok "site: forwards X-Forwarded-For" has 'proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;'
ok "site: long read/send timeouts for live WebSockets" \
  sh -c "grep -qF 'proxy_read_timeout 1h;' '${site}' && grep -qF 'proxy_send_timeout 1h;' '${site}'"
ok "site: uncapped body size for attachment uploads" has 'client_max_body_size 0;'
ok "site: response buffering off" has 'proxy_buffering off;'
ok "site: points at the certificate and key" \
  sh -c "grep -qF 'ssl_certificate     /etc/construct/tls/t3.crt;' '${site}' && grep -qF 'ssl_certificate_key /etc/construct/tls/t3.key;' '${site}'"
ok "site: no HTTP/2 directive (unknown to nginx before 1.25.1)" \
  sh -c "! grep -qE '^\s*http2 ' '${site}'"
site_v4="${tmp}/site-v4.conf"
t3_https_nginx_site 5178 5177 /c.crt /k.key false >"${site_v4}"
ok "site: the IPv6 listener is omitted when the kernel has no IPv6" \
  sh -c "! grep -qF '[::]' '${site_v4}' && grep -qF 'listen 0.0.0.0:5178 ssl;' '${site_v4}'"
site_ports="${tmp}/site-ports.conf"
t3_https_nginx_site 6443 7000 /c.crt /k.key true >"${site_ports}"
ok "site: honours non-default ports on both ends" \
  sh -c "grep -qF 'listen 0.0.0.0:6443 ssl;' '${site_ports}' && grep -qF 'proxy_pass http://127.0.0.1:7000;' '${site_ports}'"

# Real nginx parse check when nginx is available (skipped otherwise).
if command -v nginx >/dev/null 2>&1; then
  ng="${tmp}/ng"
  mkdir -p "${ng}"
  openssl req -x509 -newkey rsa:2048 -sha256 -days 2 -nodes \
    -keyout "${ng}/t3.key" -out "${ng}/t3.crt" -subj "/CN=nginx-parse-check" >/dev/null 2>&1
  t3_https_nginx_site 5178 5177 "${ng}/t3.crt" "${ng}/t3.key" true >"${ng}/site.conf"
  cat >"${ng}/nginx.conf" <<EOF
worker_processes 1;
error_log ${ng}/error.log;
pid ${ng}/nginx.pid;
events { worker_connections 64; }
http {
  access_log ${ng}/access.log;
  client_body_temp_path ${ng}/body;
  proxy_temp_path ${ng}/proxy;
  fastcgi_temp_path ${ng}/fcgi;
  uwsgi_temp_path ${ng}/uwsgi;
  scgi_temp_path ${ng}/scgi;
  include ${ng}/site.conf;
}
EOF
  ok "site: real nginx accepts the generated configuration" \
    nginx -t -c "${ng}/nginx.conf"
else
  printf '  SKIP  site: real nginx accepts the generated configuration (nginx not installed)\n'
fi

# ── Layer 2: the whole script in a sandbox ───────────────────────────────────
# Stub nginx + systemctl so nothing on this machine is reconfigured, and record
# every call so the test can assert the config was TESTED before a reload.
stubs="${tmp}/stubs"
mkdir -p "${stubs}"
calls="${tmp}/calls"
: >"${calls}"
cat >"${stubs}/nginx" <<EOF
#!/usr/bin/env bash
printf 'nginx %s\n' "\$*" >>"${calls}"
exit 0
EOF
cat >"${stubs}/systemctl" <<EOF
#!/usr/bin/env bash
printf 'systemctl %s\n' "\$*" >>"${calls}"
# "is-active nginx" is asked before a reload; claim it is running.
exit 0
EOF
printf '#!/bin/sh\necho testvm\n' >"${stubs}/hostname"
chmod +x "${stubs}/nginx" "${stubs}/systemctl" "${stubs}/hostname"

sandbox_root="${tmp}/vm"
mkdir -p "${sandbox_root}/etc/construct" "${sandbox_root}/etc/nginx/sites-available" \
         "${sandbox_root}/etc/nginx/sites-enabled"
CFG="${sandbox_root}/etc/construct/config.env"
: >"${CFG}"
TLS="${sandbox_root}/etc/construct/tls"
STATUS="${sandbox_root}/etc/construct/t3code-https-status"
HANDOFF="${sandbox_root}/etc/construct/t3code-ca.crt"
SITE="${sandbox_root}/etc/nginx/sites-available/construct-t3"
LINK="${sandbox_root}/etc/nginx/sites-enabled/construct-t3"

run_setup() {
  PATH="${stubs}:${PATH}" \
  CONFIG_FILE="${CFG}" REPO_DIR="${ROOT}" \
  T3CODE_TLS_DIR="${TLS}" T3CODE_HTTPS_STATUS_FILE="${STATUS}" \
  T3CODE_CA_HANDOFF="${HANDOFF}" \
  T3CODE_NGINX_AVAILABLE_DIR="${sandbox_root}/etc/nginx/sites-available" \
  T3CODE_NGINX_ENABLED_DIR="${sandbox_root}/etc/nginx/sites-enabled" \
  bash "${SCRIPT}" "$@"
}
cfgval() { sed -n "s/^$1=//p" "${CFG}" | head -1; }

if [[ "${EUID}" -ne 0 ]]; then
  printf '  SKIP  end-to-end sandbox run (needs root for the script'"'"'s own EUID check)\n'
else
  out="${tmp}/run1.out"
  run_setup >"${out}" 2>&1
  ok "enable: exits 0" test "$?" = "0"
  ok "enable: creates the CA and the leaf" \
    sh -c "test -s '${TLS}/ca.crt' && test -s '${TLS}/ca.key' && test -s '${TLS}/t3.crt' && test -s '${TLS}/t3.key'"
  ok "enable: the TLS directory is 0700" \
    sh -c "test \"\$(stat -c %a '${TLS}')\" = 700"
  ok "enable: private keys are 0600" \
    sh -c "test \"\$(stat -c %a '${TLS}/ca.key')\" = 600 && test \"\$(stat -c %a '${TLS}/t3.key')\" = 600"
  ok "enable: certificates are world-readable (public material)" \
    sh -c "test \"\$(stat -c %a '${TLS}/ca.crt')\" = 644 && test \"\$(stat -c %a '${TLS}/t3.crt')\" = 644"
  ok "enable: the leaf is signed by the CA" \
    openssl verify -CAfile "${TLS}/ca.crt" "${TLS}/t3.crt"
  ok "enable: the leaf carries the expected SANs" \
    sh -c "openssl x509 -in '${TLS}/t3.crt' -noout -text | grep -q 'DNS:testvm.mshome.net' && \
           openssl x509 -in '${TLS}/t3.crt' -noout -text | grep -q 'DNS:localhost' && \
           openssl x509 -in '${TLS}/t3.crt' -noout -text | grep -q 'IP Address:127.0.0.1'"
  ok "enable: the leaf is a serverAuth certificate, not a CA" \
    sh -c "openssl x509 -in '${TLS}/t3.crt' -noout -text | grep -q 'TLS Web Server Authentication' && \
           openssl x509 -in '${TLS}/t3.crt' -noout -text | grep -q 'CA:FALSE'"
  ok "enable: the CA is a CA certificate" \
    sh -c "openssl x509 -in '${TLS}/ca.crt' -noout -text | grep -q 'CA:TRUE'"
  ok "enable: the CA subject names the VM" \
    sh -c "openssl x509 -in '${TLS}/ca.crt' -noout -subject | grep -q 'Construct Local CA (testvm)'"
  ok "enable: records the SAN list next to the certificate" \
    sh -c "grep -q 'DNS:testvm.mshome.net' '${TLS}/t3.sans'"
  ok "enable: writes the nginx site and enables it via a symlink" \
    sh -c "test -f '${SITE}' && test -L '${LINK}'"
  ok "enable: tests the configuration before reloading nginx" \
    sh -c "grep -q '^nginx -t' '${calls}' && grep -q 'systemctl reload nginx' '${calls}' && \
           [ \"\$(grep -n '^nginx -t' '${calls}' | head -1 | cut -d: -f1)\" -lt \"\$(grep -n 'systemctl reload nginx' '${calls}' | head -1 | cut -d: -f1)\" ]"
  ok "enable: enables nginx at boot" sh -c "grep -q 'systemctl enable nginx' '${calls}'"
  ok "enable: persists T3CODE_HTTPS=true" test "$(cfgval T3CODE_HTTPS)" = "true"
  ok "enable: persists the HTTPS port" test "$(cfgval T3CODE_HTTPS_PORT)" = "5178"
  ok "enable: persists the public base URL the T3 server advertises" \
    test "$(cfgval T3CODE_PUBLIC_BASE_URL)" = "https://testvm.mshome.net:5178"
  ok "enable: writes the host handoff status file" \
    sh -c "grep -q '^T3CODE_HTTPS_READY=yes$' '${STATUS}' && grep -q '^T3CODE_HTTPS_PORT=5178$' '${STATUS}'"
  ok "enable: the status file carries the CA path and both fingerprints" \
    sh -c "grep -q '^T3CODE_CA_PATH=' '${STATUS}' && grep -qE '^T3CODE_CA_SHA256=[0-9a-f]{64}$' '${STATUS}' && \
           grep -qE '^T3CODE_CA_THUMBPRINT=[0-9A-F]{40}$' '${STATUS}'"
  ok "enable: publishes a readable CA copy for the host to fetch" \
    sh -c "test -s '${HANDOFF}' && test \"\$(stat -c %a '${HANDOFF}')\" = 644 && \
           cmp -s '${HANDOFF}' '${TLS}/ca.crt'"
  ok "enable: the status file is not secret (no key material, mode 0644)" \
    sh -c "test \"\$(stat -c %a '${STATUS}')\" = 644 && ! grep -q 'PRIVATE KEY' '${STATUS}'"
  ok "enable: no private key material is printed" \
    sh -c "! grep -q 'PRIVATE KEY' '${out}'"

  # Idempotence: a second run must reuse both certificates untouched.
  ca_before="$(sha256sum "${TLS}/ca.crt" | cut -d' ' -f1)"
  leaf_before="$(sha256sum "${TLS}/t3.crt" | cut -d' ' -f1)"
  run_setup >"${tmp}/run2.out" 2>&1
  ok "re-run: reuses the CA" test "$(sha256sum "${TLS}/ca.crt" | cut -d' ' -f1)" = "${ca_before}"
  ok "re-run: reuses the leaf (no needless churn)" \
    test "$(sha256sum "${TLS}/t3.crt" | cut -d' ' -f1)" = "${leaf_before}"
  ok "re-run: says so instead of reissuing" grep -q "still covers" "${tmp}/run2.out"

  # A changed external host must reissue the leaf (new SAN) but keep the CA.
  CONSTRUCT_EXTERNAL_HOST="vm.example.com" run_setup >"${tmp}/run3.out" 2>&1
  ok "san drift: the leaf is reissued" \
    sh -c "test \"\$(sha256sum '${TLS}/t3.crt' | cut -d' ' -f1)\" != '${leaf_before}'"
  ok "san drift: the CA is NOT touched (host trust survives)" \
    test "$(sha256sum "${TLS}/ca.crt" | cut -d' ' -f1)" = "${ca_before}"
  ok "san drift: the new name is in the certificate" \
    sh -c "openssl x509 -in '${TLS}/t3.crt' -noout -text | grep -q 'DNS:vm.example.com'"
  ok "san drift: the public base URL follows the external host" \
    test "$(cfgval T3CODE_PUBLIC_BASE_URL)" = "https://vm.example.com:5178"

  # A restored/replaced CA leaves a leaf that no longer verifies -> reissue.
  rm -f "${TLS}/ca.crt" "${TLS}/ca.key"
  run_setup >"${tmp}/run4.out" 2>&1
  ok "new CA: a leaf that no longer chains is reissued" \
    openssl verify -CAfile "${TLS}/ca.crt" "${TLS}/t3.crt"

  # Disable path.
  : >"${calls}"
  T3CODE_HTTPS=false run_setup >"${tmp}/run5.out" 2>&1
  ok "disable: removes the site and its symlink" \
    sh -c "test ! -e '${LINK}' && test ! -e '${SITE}'"
  ok "disable: reloads nginx after removing the site" \
    sh -c "grep -q 'systemctl reload nginx' '${calls}'"
  ok "disable: records T3CODE_HTTPS=false" test "$(cfgval T3CODE_HTTPS)" = "false"
  ok "disable: clears the public base URL (server falls back to http)" \
    test -z "$(cfgval T3CODE_PUBLIC_BASE_URL)"
  ok "disable: removes the host handoff status file" test ! -e "${STATUS}"
  ok "disable: KEEPS the CA (re-enabling needs no new Windows trust import)" \
    sh -c "test -s '${TLS}/ca.crt' && test -s '${TLS}/ca.key'"

  # A bare re-run after a disable must NOT resurrect the proxy: with no value in
  # the environment the saved T3CODE_HTTPS=false is the answer.
  run_setup >"${tmp}/run5b.out" 2>&1
  ok "disable: a later run with no environment value keeps it off (saved wins)" \
    sh -c "test ! -e '${LINK}' && test \"\$(sed -n 's/^T3CODE_HTTPS=//p' '${CFG}' | head -1)\" = false"

  # Re-enable (what provision.sh does: it always passes the resolved value):
  # same CA, working proxy again.
  ca_kept="$(sha256sum "${TLS}/ca.crt" | cut -d' ' -f1)"
  T3CODE_HTTPS=true run_setup >"${tmp}/run6.out" 2>&1
  ok "re-enable: restores the site" sh -c "test -f '${SITE}' && test -L '${LINK}'"
  ok "re-enable: reuses the same CA" \
    test "$(sha256sum "${TLS}/ca.crt" | cut -d' ' -f1)" = "${ca_kept}"
  ok "re-enable: T3CODE_HTTPS is true again" test "$(cfgval T3CODE_HTTPS)" = "true"

  # --teardown: proxy off, but the user's PREFERENCE and the CA stay.
  run_setup >/dev/null 2>&1
  bash "${ROOT}/bin/config-set.sh" "${CFG}" T3CODE_HTTPS true
  run_setup --teardown >"${tmp}/run7.out" 2>&1
  ok "teardown: removes the site" sh -c "test ! -e '${LINK}'"
  ok "teardown: leaves T3CODE_HTTPS=true (the preference is the user's)" \
    test "$(cfgval T3CODE_HTTPS)" = "true"
  ok "teardown: clears the advertised public base URL" \
    test -z "$(cfgval T3CODE_PUBLIC_BASE_URL)"
  ok "teardown: removes the status file" test ! -e "${STATUS}"

  # A VM that never had HTTPS must not gain an empty key from a teardown.
  fresh_cfg="${tmp}/fresh.env"
  : >"${fresh_cfg}"
  PATH="${stubs}:${PATH}" CONFIG_FILE="${fresh_cfg}" REPO_DIR="${ROOT}" \
    T3CODE_TLS_DIR="${tmp}/fresh-tls" T3CODE_HTTPS_STATUS_FILE="${tmp}/fresh-status" \
    T3CODE_CA_HANDOFF="${tmp}/fresh-ca.crt" \
    T3CODE_NGINX_AVAILABLE_DIR="${sandbox_root}/etc/nginx/sites-available" \
    T3CODE_NGINX_ENABLED_DIR="${sandbox_root}/etc/nginx/sites-enabled" \
    bash "${SCRIPT}" --teardown >/dev/null 2>&1
  ok "teardown: an untouched config.env gains no keys at all" \
    test ! -s "${fresh_cfg}"

  # ── The proxy failed to come up ────────────────────────────────────────────
  # nginx accepts the config but will not (re)start. The preference must survive
  # for a retry while the ADVERTISED origin is cleared, so nothing points a
  # browser at a dead listener. Stub systemctl to fail every nginx activation.
  stubs_fail="${tmp}/stubs-fail"
  mkdir -p "${stubs_fail}"
  cp "${stubs}/nginx" "${stubs}/hostname" "${stubs_fail}/"
  cat >"${stubs_fail}/systemctl" <<EOF
#!/usr/bin/env bash
printf 'systemctl %s\n' "\$*" >>"${calls}"
case "\$*" in
  *"reload nginx"*|*"start nginx"*|*"restart nginx"*) exit 1 ;;
esac
exit 0
EOF
  printf '#!/usr/bin/env bash\nexit 0\n' >"${stubs_fail}/journalctl"
  chmod +x "${stubs_fail}/systemctl" "${stubs_fail}/journalctl"

  fail_cfg="${tmp}/failed.env"
  : >"${fail_cfg}"
  bash "${ROOT}/bin/config-set.sh" "${fail_cfg}" T3CODE true
  fail_tls="${tmp}/failed-tls"
  fail_status="${tmp}/failed-status"
  fail_enabled="${tmp}/failed-nginx-enabled"
  mkdir -p "${fail_enabled}" "${tmp}/failed-nginx-available"
  # A pre-existing distro default site, so the "was it the cause?" logic runs.
  printf 'server { listen 80; }\n' >"${tmp}/failed-nginx-available/default"
  ln -sfn "${tmp}/failed-nginx-available/default" "${fail_enabled}/default"
  run_failed_setup() {
    PATH="${stubs_fail}:${PATH}" \
    CONFIG_FILE="${fail_cfg}" REPO_DIR="${ROOT}" \
    T3CODE_TLS_DIR="${fail_tls}" T3CODE_HTTPS_STATUS_FILE="${fail_status}" \
    T3CODE_CA_HANDOFF="${tmp}/failed-ca.crt" \
    T3CODE_NGINX_AVAILABLE_DIR="${tmp}/failed-nginx-available" \
    T3CODE_NGINX_ENABLED_DIR="${fail_enabled}" \
    bash "${SCRIPT}"
  }
  run_failed_setup >"${tmp}/failrun.out" 2>&1
  ok "failed setup: exits non-zero" test "$?" != "0"
  ok "failed setup: keeps T3CODE_HTTPS=true so the next provision retries" \
    test "$(sed -n 's/^T3CODE_HTTPS=//p' "${fail_cfg}" | head -1)" = "true"
  ok "failed setup: does NOT advertise a public https origin" \
    test -z "$(sed -n 's/^T3CODE_PUBLIC_BASE_URL=//p' "${fail_cfg}" | head -1)"
  ok "failed setup: writes no host handoff status file" test ! -e "${fail_status}"
  ok "failed setup: says the GUI stays on plain HTTP" \
    grep -q "keeps working over plain HTTP" "${tmp}/failrun.out"
  # ...and the distro default site is still there: it was not the cause.
  ok "failed setup: an unrelated failure leaves the distro default site intact" \
    sh -c "test -L '${fail_enabled}/default' && test \"\$(readlink '${fail_enabled}/default')\" = '${tmp}/failed-nginx-available/default'"
  # The banner must follow suit: http URL, no CA/plain block.
  banner_out="${tmp}/banner-failed.out"
  PATH="${stubs}:${PATH}" CONFIG_FILE="${fail_cfg}" \
    bash "${ROOT}/bin/print-connection-info.sh" >"${banner_out}" 2>/dev/null
  ok "failed setup: the connection banner advertises the http URL" \
    grep -q "URL:      http://testvm.mshome.net:5177" "${banner_out}"
  ok "failed setup: the banner shows no CA/HTTPS block" \
    sh -c "! grep -q 'CA cert:' '${banner_out}'"
  ok "failed setup: the banner's pairing hint uses the http origin" \
    grep -q -- "--base-url http://testvm.mshome.net:5177" "${banner_out}"
  # A recorded origin, by contrast, IS advertised -- with the CA hint beside it.
  succ_cfg="${tmp}/succeeded.env"
  : >"${succ_cfg}"
  bash "${ROOT}/bin/config-set.sh" "${succ_cfg}" T3CODE true
  bash "${ROOT}/bin/config-set.sh" "${succ_cfg}" T3CODE_HTTPS true
  bash "${ROOT}/bin/config-set.sh" "${succ_cfg}" T3CODE_PUBLIC_BASE_URL "https://testvm.mshome.net:5178"
  succ_banner="${tmp}/banner-ok.out"
  PATH="${stubs}:${PATH}" CONFIG_FILE="${succ_cfg}" \
    bash "${ROOT}/bin/print-connection-info.sh" >"${succ_banner}" 2>/dev/null
  ok "success: the banner advertises the https URL, the CA and the plain fallback" \
    sh -c "grep -q 'URL:      https://testvm.mshome.net:5178' '${succ_banner}' && \
           grep -q 'CA cert:' '${succ_banner}' && grep -q 'Plain:    http://' '${succ_banner}'"
  ok "success: the banner's pairing hint uses the https origin" \
    grep -q -- "--base-url https://testvm.mshome.net:5178" "${succ_banner}"

  # ── The distro default site really IS the culprit ──────────────────────────
  # Then, and only then, it stays removed -- and the run succeeds.
  stubs_culprit="${tmp}/stubs-culprit"
  mkdir -p "${stubs_culprit}"
  cp "${stubs}/nginx" "${stubs}/hostname" "${stubs_fail}/journalctl" "${stubs_culprit}/"
  cat >"${stubs_culprit}/systemctl" <<EOF
#!/usr/bin/env bash
printf 'systemctl %s\n' "\$*" >>"${calls}"
case "\$*" in
  # Comes up only once the default site is out of the way.
  *"reload nginx"*|*"start nginx"*|*"restart nginx"*)
    [ -e "${fail_enabled}/default" ] && exit 1
    exit 0 ;;
esac
exit 0
EOF
  chmod +x "${stubs_culprit}/systemctl"
  bash "${ROOT}/bin/config-set.sh" "${fail_cfg}" T3CODE_PUBLIC_BASE_URL ""
  PATH="${stubs_culprit}:${PATH}" \
    CONFIG_FILE="${fail_cfg}" REPO_DIR="${ROOT}" \
    T3CODE_TLS_DIR="${fail_tls}" T3CODE_HTTPS_STATUS_FILE="${fail_status}" \
    T3CODE_CA_HANDOFF="${tmp}/failed-ca.crt" \
    T3CODE_NGINX_AVAILABLE_DIR="${tmp}/failed-nginx-available" \
    T3CODE_NGINX_ENABLED_DIR="${fail_enabled}" \
    bash "${SCRIPT}" >"${tmp}/culprit.out" 2>&1
  ok "default site: a causal default site is removed and the run succeeds" test "$?" = "0"
  ok "default site: ...it stays removed" test ! -e "${fail_enabled}/default"
  ok "default site: ...and says why" \
    grep -q "prevented nginx from starting" "${tmp}/culprit.out"
  ok "default site: the public origin is advertised again" \
    test -n "$(sed -n 's/^T3CODE_PUBLIC_BASE_URL=//p' "${fail_cfg}" | head -1)"

  # Refusals: a bad port, and the two ports colliding.
  PATH="${stubs}:${PATH}" CONFIG_FILE="${CFG}" REPO_DIR="${ROOT}" \
    T3CODE_HTTPS_PORT=99999 bash "${SCRIPT}" >/dev/null 2>&1
  ok "guard: an out-of-range HTTPS port is refused" test "$?" != "0"
  PATH="${stubs}:${PATH}" CONFIG_FILE="${CFG}" REPO_DIR="${ROOT}" \
    T3CODE_HTTPS_PORT=5177 T3CODE_PORT=5177 bash "${SCRIPT}" >/dev/null 2>&1
  ok "guard: the HTTPS port must differ from the plain-HTTP port" test "$?" != "0"
  PATH="${stubs}:${PATH}" CONFIG_FILE="${CFG}" REPO_DIR="${ROOT}" \
    bash "${SCRIPT}" --bogus >/dev/null 2>&1
  ok "guard: an unknown argument is refused" test "$?" != "0"
fi

# ── Wiring: the callers must reconcile HTTPS at the right moments ────────────
installer="${ROOT}/bin/install-ai-tools.sh"
provision="${ROOT}/bin/provision.sh"

ok "installer: runs the HTTPS setup before restarting t3code-serve" \
  sh -c "grep -q 'bin/setup-t3-https.sh' '${installer}' && \
         [ \"\$(grep -n 'setup-t3-https.sh' '${installer}' | head -1 | cut -d: -f1)\" -lt \"\$(grep -n 'systemctl restart t3code-serve' '${installer}' | head -1 | cut -d: -f1)\" ]"
# The unchanged-build early return must NOT skip HTTPS: flipping the toggle on an
# otherwise untouched T3 install has to take effect.
ok "installer: reconciles HTTPS before the unchanged-build early return" \
  sh -c "[ \"\$(grep -n 'setup-t3-https.sh' '${installer}' | head -1 | cut -d: -f1)\" -lt \"\$(grep -n 'T3 Code build is unchanged' '${installer}' | head -1 | cut -d: -f1)\" ]"
ok "installer: a failed HTTPS setup warns instead of failing the T3 install" \
  grep -q 'WARNING: T3 Code HTTPS setup failed' "${installer}"

# `t3 serve` reads T3CODE_PUBLIC_BASE_URL from its EnvironmentFile only at START,
# so the unchanged-build fast path may only skip the restart when the HTTPS
# reconciliation did NOT change that value -- otherwise the running process keeps
# advertising the old origin. The decision is a pure function, exercised here
# through the installer's funcs-only sourcing (a subprocess each time, because
# that file ships its own `ok` logger).
can_skip() {
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true bash -c '
    source "$1" >/dev/null 2>&1
    t3_can_skip_restart "$2" "$3" "$4" "$5"' _ "${installer}" "$1" "$2" "$3" "$4"
}
ok "fast path: same build and unchanged public URL -> skip the restart" \
  can_skip "buildkey" "buildkey" "https://vm:5178" "https://vm:5178"
nok "fast path: HTTPS just turned ON (empty -> url) forces a restart" \
  can_skip "buildkey" "buildkey" "" "https://vm:5178"
nok "fast path: HTTPS just turned OFF (url -> empty) forces a restart" \
  can_skip "buildkey" "buildkey" "https://vm:5178" ""
nok "fast path: a changed port/external host forces a restart" \
  can_skip "buildkey" "buildkey" "https://vm:5178" "https://vm:6443"
nok "fast path: a changed build key still forces the reinstall path" \
  can_skip "buildkey" "otherkey" "https://vm:5178" "https://vm:5178"
nok "fast path: no recorded build key never skips" \
  can_skip "" "" "https://vm:5178" "https://vm:5178"
ok "installer: the fast-path guard is the one used at the early return" \
  sh -c "grep -q 'if t3_can_skip_restart \"\${_wanted_t3_build}\" \"\${_active_t3_build}\" \"\${_t3_pub_before}\" \"\${_t3_pub_after}\"' '${installer}'"
ok "installer: samples the public URL before AND after the HTTPS setup" \
  sh -c "grep -q '_t3_pub_before=' '${installer}' && grep -q '_t3_pub_after=' '${installer}' && \
         [ \"\$(grep -n '_t3_pub_before=\"\$(sed' '${installer}' | cut -d: -f1)\" -lt \"\$(grep -n 'bash \"\${REPO_DIR}/bin/setup-t3-https.sh\"' '${installer}' | head -1 | cut -d: -f1)\" ]"
ok "installer: says why it restarts when the public URL changed" \
  grep -q "public base URL changed" "${installer}"
ok "installer: defaults T3CODE_HTTPS to true and honours an env override" \
  sh -c "grep -q '_t3_https_override' '${installer}' && grep -q 'T3CODE_HTTPS:-true' '${installer}'"
ok "provision: T3CODE_HTTPS keeps the saved value when passed empty" \
  sh -c "grep -q '_t3code_https_saved' '${provision}' && \
         grep -q 'T3CODE_HTTPS:-\${_t3code_https_saved:-true}' '${provision}'"
ok "provision: persists T3CODE_HTTPS and logs it with the other T3 keys" \
  sh -c "grep -q 'cfg T3CODE_HTTPS' '${provision}' && grep -q 'T3CODE_HTTPS=\${T3CODE_HTTPS}' '${provision}'"
ok "provision: passes T3CODE_HTTPS into the T3 installer" \
  sh -c "grep -q 'T3CODE_HTTPS=\"\${T3CODE_HTTPS}\"' '${provision}'"
ok "provision: T3CODE=false tears the proxy down without losing the preference" \
  sh -c "grep -q 'setup-t3-https.sh\" --teardown' '${provision}'"
ok "export: carries the TLS directory (auth-gated)" \
  sh -c "grep -q 'etc/construct/tls' '${ROOT}/bin/export-config.sh'"
ok "restore: restores the TLS directory and reconciles HTTPS" \
  sh -c "grep -q 'etc/construct/tls' '${ROOT}/bin/restore-config.sh' && \
         grep -q 'setup-t3-https.sh' '${ROOT}/bin/restore-config.sh'"
ok "banner: keys the https URL off the RECORDED origin" \
  grep -qF '"${T3CODE_PUBLIC_BASE_URL}" == https://' "${ROOT}/bin/print-connection-info.sh"
ok "banner: shows the CA hint" \
  grep -qF 'CA cert:' "${ROOT}/bin/print-connection-info.sh"
nok "banner: does NOT branch on the T3CODE_HTTPS preference" \
  grep -qF '"${T3CODE_HTTPS}" == "true"' "${ROOT}/bin/print-connection-info.sh"

# ── Summary ─────────────────────────────────────────────────────────────────
printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
