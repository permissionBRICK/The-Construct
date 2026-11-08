#!/usr/bin/env bash
# The installer's TLS step on a host rename: the old certificate stays in service as the previous
# one and a new one names the new host; that needs a service that reads the previous-certificate
# setting; renaming back swaps the old one back; a second rename is refused while a previous one is
# kept; a rename to an address reissues; --drop-previous-certificate retires the previous one.
# Runs only the certificate functions, never the installer.
set -euo pipefail
cd "$(dirname "$0")/../.."
work="$(mktemp -d)"
trap 'rm -r "$work"' EXIT
eval "$(sed -n '/^cert_names_host() {/,/^}$/p' service/host/install-construct-host.sh)"
eval "$(sed -n '/^service_supports_previous_cert() {/,/^}$/p' service/host/install-construct-host.sh)"
eval "$(sed -n '/^ensure_tls_certificate() {$/,/^}$/p' service/host/install-construct-host.sh)"
note() { :; }
die() { echo "$*" >&2; exit 1; }
ETC_DIR="$work/etc" TMP_ROOT="$work/tmp" HOST_DIR="$work/host" NODE=node1 DROP_PREVIOUS_CERT=0
mkdir -p "$ETC_DIR" "$TMP_ROOT" "$HOST_DIR"
supported() { printf 'metadata ... PreviousCertPath ...' >"$HOST_DIR/Constructd.Core.dll"; }
unsupported() { printf 'metadata ... CertPath ...' >"$HOST_DIR/Constructd.Core.dll"; }
fp() { openssl x509 -in "$1" -noout -fingerprint -sha256 | sed 's/^.*=//'; }
fail() { echo "FAIL: $*" >&2; exit 1; }
state() { echo "$(fp "$ETC_DIR/tls.crt") $( [[ -f "$ETC_DIR/tls-previous.crt" ]] && fp "$ETC_DIR/tls-previous.crt" || echo none)"; }

# A fresh install on an address.
supported
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

# A rename under a service that cannot serve a previous certificate is refused, and changes nothing.
unsupported
PUBLIC_HOST=construct.example.test
if (ensure_tls_certificate) 2>/dev/null; then fail "rename was accepted by a service without previous-certificate support"; fi
[[ "$(state)" == "$first none" ]] || fail "refused rename changed the certificates"
supported

# The rename: the old certificate becomes the previous one, a new one names the new host.
ensure_tls_certificate
[[ "$PREVIOUS_FINGERPRINT" == "$first" ]] || fail "rename did not keep the old certificate as the previous one"
[[ "$FINGERPRINT" != "$first" ]] || fail "rename kept a certificate that does not name the new host"
[[ "$(fp "$ETC_DIR/tls-previous.crt")" == "$first" ]] || fail "tls-previous.crt is not the old certificate"
openssl pkcs12 -in "$ETC_DIR/tls-previous.pfx" -passin "file:$ETC_DIR/tls-previous.pass" -noout 2>/dev/null \
  || fail "the previous PFX no longer opens with its password"
cert_names_host "$ETC_DIR/tls.crt" construct.example.test || fail "new certificate does not name the new host"
cert_names_host "$ETC_DIR/tls.crt" example.test && fail "a suffix of the name matched"
second="$FINGERPRINT"

# Re-running after the rename keeps both; with a service that lost support it is refused.
ensure_tls_certificate
[[ "$(state)" == "$second $first" ]] || fail "re-run after the rename changed a certificate"
unsupported
if (ensure_tls_certificate) 2>/dev/null; then fail "a kept previous certificate was accepted by a service that cannot serve it"; fi
supported

# A second rename while the previous certificate is still in service is refused, and changes nothing.
PUBLIC_HOST=other.example.test
if (ensure_tls_certificate) 2>/dev/null; then fail "second rename was accepted while a previous certificate is kept"; fi
[[ "$(state)" == "$second $first" ]] || fail "refused rename changed the certificates"

# Renaming back to the old address swaps the old certificate back in and drops the interim one.
PUBLIC_HOST=192.0.2.10
ensure_tls_certificate
[[ "$FINGERPRINT" == "$first" && -z "$PREVIOUS_FINGERPRINT" ]] || fail "renaming back did not restore the original certificate"
[[ "$(state)" == "$first none" ]] || fail "renaming back left files behind"

# Rename again, then retire the previous certificate (no rename): the current one stays.
PUBLIC_HOST=construct.example.test
ensure_tls_certificate
third="$FINGERPRINT"
[[ "$(state)" == "$third $first" ]] || fail "second rename to the name did not keep the original as previous"
DROP_PREVIOUS_CERT=1
ensure_tls_certificate
[[ "$(state)" == "$third none" && -z "$PREVIOUS_FINGERPRINT" ]] || fail "drop changed the current certificate or kept the previous one"
[[ ! -e "$ETC_DIR/tls-previous.pfx" && ! -e "$ETC_DIR/tls-previous.pass" ]] || fail "drop left files behind"
DROP_PREVIOUS_CERT=0

# A rename to an address cannot keep the old certificate beside the new one (no SNI): it reissues.
PUBLIC_HOST=198.51.100.7
ensure_tls_certificate
[[ "$FINGERPRINT" != "$third" && -z "$PREVIOUS_FINGERPRINT" ]] || fail "rename to an address kept a previous certificate"
cert_names_host "$ETC_DIR/tls.crt" 198.51.100.7 || fail "reissued certificate does not name the address"

echo 'Host installer certificate tests passed'
