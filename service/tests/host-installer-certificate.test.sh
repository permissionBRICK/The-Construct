#!/usr/bin/env bash
# The installer's TLS step on a host rename: the old certificate stays in service as the previous
# one, a new one names the new host, a second rename is refused while a previous one is kept, and
# --drop-previous-certificate retires it. Runs only the certificate functions, never the installer.
set -euo pipefail
cd "$(dirname "$0")/../.."
work="$(mktemp -d)"
trap 'rm -r "$work"' EXIT
eval "$(sed -n '/^cert_names_host() {/,/^}$/p' service/host/install-construct-host.sh)"
eval "$(sed -n '/^ensure_tls_certificate() {$/,/^}$/p' service/host/install-construct-host.sh)"
note() { :; }
die() { echo "$*" >&2; exit 1; }
ETC_DIR="$work/etc" TMP_ROOT="$work/tmp" NODE=node1 DROP_PREVIOUS_CERT=0
mkdir -p "$ETC_DIR" "$TMP_ROOT"
fp() { openssl x509 -in "$1" -noout -fingerprint -sha256 | sed 's/^.*=//'; }
fail() { echo "FAIL: $*" >&2; exit 1; }

# A fresh install on an address.
PUBLIC_HOST=192.0.2.10
ensure_tls_certificate
first="$FINGERPRINT"
[[ -z "$PREVIOUS_FINGERPRINT" ]] || fail "fresh install has a previous certificate"
cert_names_host "$ETC_DIR/tls.crt" 192.0.2.10 || fail "certificate does not name the address"
cert_names_host "$ETC_DIR/tls.crt" NODE1 || fail "host names are not compared case-insensitively"
cert_names_host "$ETC_DIR/tls.crt" 192.0.2.1 && fail "a prefix of the address matched"
[[ ! -e "$TMP_ROOT/tls.key" ]] || fail "the private key was left in the temp directory"

# Re-running with the same public host changes nothing.
ensure_tls_certificate
[[ "$FINGERPRINT" == "$first" && -z "$PREVIOUS_FINGERPRINT" ]] || fail "re-run replaced the certificate"

# The rename: the old certificate becomes the previous one, a new one names the new host.
PUBLIC_HOST=construct.example.test
ensure_tls_certificate
[[ "$PREVIOUS_FINGERPRINT" == "$first" ]] || fail "rename did not keep the old certificate as the previous one"
[[ "$FINGERPRINT" != "$first" ]] || fail "rename kept a certificate that does not name the new host"
[[ "$(fp "$ETC_DIR/tls-previous.crt")" == "$first" ]] || fail "tls-previous.crt is not the old certificate"
for f in tls-previous.pfx tls-previous.pass; do [[ -f "$ETC_DIR/$f" ]] || fail "$f missing"; done
openssl pkcs12 -in "$ETC_DIR/tls-previous.pfx" -passin "file:$ETC_DIR/tls-previous.pass" -noout 2>/dev/null \
  || fail "the previous PFX no longer opens with its password"
cert_names_host "$ETC_DIR/tls.crt" construct.example.test || fail "new certificate does not name the new host"
cert_names_host "$ETC_DIR/tls.crt" example.test && fail "a suffix of the name matched"
second="$FINGERPRINT"

# Re-running after the rename keeps both.
ensure_tls_certificate
[[ "$FINGERPRINT" == "$second" && "$PREVIOUS_FINGERPRINT" == "$first" ]] || fail "re-run after the rename changed a certificate"

# A second rename while the previous certificate is still in service is refused, and changes nothing.
PUBLIC_HOST=other.example.test
if (ensure_tls_certificate) 2>/dev/null; then fail "second rename was accepted while a previous certificate is kept"; fi
[[ "$(fp "$ETC_DIR/tls.crt")" == "$second" && "$(fp "$ETC_DIR/tls-previous.crt")" == "$first" ]] || fail "refused rename changed the certificates"

# Retiring the previous certificate (no rename) keeps the current one.
PUBLIC_HOST=construct.example.test DROP_PREVIOUS_CERT=1
ensure_tls_certificate
[[ "$FINGERPRINT" == "$second" && -z "$PREVIOUS_FINGERPRINT" ]] || fail "drop changed the current certificate or kept the previous one"
[[ ! -e "$ETC_DIR/tls-previous.pfx" && ! -e "$ETC_DIR/tls-previous.pass" && ! -e "$ETC_DIR/tls-previous.crt" ]] || fail "drop left files behind"

echo 'Host installer certificate tests passed'
