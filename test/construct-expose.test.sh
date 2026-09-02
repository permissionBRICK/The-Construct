#!/usr/bin/env bash
# Regression tests for `construct expose` — the guest side of port forwards
# (plan §4.6, contract in docs/expose.md). Run: bash test/construct-expose.test.sh
#
# Everything runs against a sandboxed config.env, a sandboxed spool root and a
# stub `curl` on PATH that records its argv, its header file and its body — so
# these also assert the promise that the VM token NEVER appears on a command
# line (a command line is world-readable in `ps`).

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

# ── sandbox ──────────────────────────────────────────────────────────────────

spool="${tmp}/forwards"
empty_cfg="${tmp}/config_empty.env"
: >"${empty_cfg}"

# A stub curl that records what it was given and replays a canned answer.
# ${stub_dir}/code[-N] is the HTTP status, ${stub_dir}/response[-N] the body;
# the -N variants answer the Nth call, so a poll can change its mind.
stub_dir="${tmp}/curlstub"
stubs="${tmp}/stubs"
mkdir -p "${stubs}"
cat >"${stubs}/curl" <<'STUB'
#!/usr/bin/env bash
d="${STUB_DIR}"
n=$(( $(cat "${d}/calls" 2>/dev/null || echo 0) + 1 ))
printf '%s' "${n}" >"${d}/calls"
printf '%s\n' "$*" >>"${d}/argv"
out=""; hdrfile=""; bodyfile=""; method="GET"; url=""
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[i]}" in
    -o) out="${args[i+1]}" ;;
    -X) method="${args[i+1]}" ;;
    -H) case "${args[i+1]}" in @*) hdrfile="${args[i+1]#@}" ;; esac ;;
    --data-binary) bodyfile="${args[i+1]#@}" ;;
    --cacert) printf '%s\n' "${args[i+1]}" >>"${d}/cacert" ;;
    http://*|https://*) url="${args[i]}" ;;
  esac
done
[[ -n "${hdrfile}" ]] && cat "${hdrfile}" >>"${d}/headers"
[[ -n "${bodyfile}" ]] && { cat "${bodyfile}" >>"${d}/bodies"; printf '\n' >>"${d}/bodies"; }
printf '%s %s\n' "${method}" "${url}" >>"${d}/requests"
# Per-call answer first (response-2 = the 2nd call), then a per-method default
# (response-GET), then the plain default -- so a poll loop of unknown length
# keeps getting a realistically shaped answer.
resp="${d}/response-${n}"
[[ -f "${resp}" ]] || resp="${d}/response-${method}"
[[ -f "${resp}" ]] || resp="${d}/response"
code="${d}/code-${n}"
[[ -f "${code}" ]] || code="${d}/code-${method}"
[[ -f "${code}" ]] || code="${d}/code"
[[ -n "${out}" && -f "${resp}" ]] && cat "${resp}" >"${out}"
printf '%s' "$(cat "${code}" 2>/dev/null || echo 200)"
exit "$(cat "${d}/exit" 2>/dev/null || echo 0)"
STUB
chmod +x "${stubs}/curl"

reset_stub() { rm -rf "${stub_dir}"; mkdir -p "${stub_dir}"; }
reset_spool() { rm -rf "${spool}"; }

expose() { CONSTRUCT_FORWARDS_DIR="${spool}" CONFIG_FILE="${empty_cfg}" bash "${CLI}" expose "$@"; }
requests() { find "${spool}/requests" -maxdepth 1 -name '*.json' 2>/dev/null | sort; }
first_request() { requests | head -1; }
request_id() { basename "$(first_request)" .json; }

# ── local mode: a request that nobody acknowledges ───────────────────────────

reset_spool
out="${tmp}/queued.out"
expose 5173 --label "vite dev" --wait 0 >"${out}" 2>"${out}.err"
rc=$?
ok "no ack: exits 6 (queued), distinct from a plain error" test "${rc}" = 6
ok "no ack: says no client is attached" grep -q 'no Construct client is attached' "${out}.err"
ok "no ack: names the request id so --list/--close can find it" \
  sh -c "grep -q \"$(request_id)\" '${out}.err'"
ok "no ack: prints no link on stdout" test ! -s "${out}"
ok "no ack: the request stays queued" test "$(requests | wc -l)" = 1

ok "request is one line of JSON" test "$(wc -l <"$(first_request)")" = 1
ok "request carries the protocol version" grep -q '"v":1' "$(first_request)"
ok "request carries its id" sh -c "grep -q '\"id\":\"$(request_id)\"' '$(first_request)'"
ok "request carries the VM port as a number" grep -q '"vmPort":5173' "$(first_request)"
ok "request carries the label" grep -q '"label":"vite dev"' "$(first_request)"
ok "request defaults to the client target" grep -q '"target":"client"' "$(first_request)"
ok "request carries a UTC timestamp" grep -qE '"createdAt":"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]+Z"' "$(first_request)"
ok "publish is atomic — no temp files left behind" test -z "$(find "${spool}/requests" -name '.tmp.*')"
ok "the spool is root-owned 0755, NOT world-writable" \
  sh -c "test \"\$(stat -c %a '${spool}/requests')\" = 755"
ok "the spool has the three contract directories" \
  sh -c "test -d '${spool}/requests' -a -d '${spool}/acks' -a -d '${spool}/close'"

# ── local mode: an ack turns into the printed link ───────────────────────────

reset_spool
out="${tmp}/acked.out"
( expose 5173 --label vite --wait 10 >"${out}" 2>"${out}.err"; printf '%s' "$?" >"${tmp}/acked.rc" ) &
waiter=$!
id=""
for _ in $(seq 1 40); do
  if [[ -n "$(requests)" ]]; then id="$(request_id)"; break; fi
  sleep 0.25
done
printf '{"v":1,"id":"%s","status":"open","localPort":5173}\n' "${id}" >"${spool}/acks/${id}.json"
wait "${waiter}" 2>/dev/null
ok "ack: exits 0" test "$(cat "${tmp}/acked.rc")" = 0
ok "ack: prints exactly the link" test "$(cat "${out}")" = "http://localhost:5173/"

# The extension may open a different local port than the VM port (busy port) --
# which is the whole reason the CLI waits for an ack instead of guessing.
reset_spool
out="${tmp}/acked2.out"
( expose 5173 --wait 10 >"${out}" 2>&1; printf '%s' "$?" >"${tmp}/acked2.rc" ) &
waiter=$!
id=""
for _ in $(seq 1 40); do
  if [[ -n "$(requests)" ]]; then id="$(request_id)"; break; fi
  sleep 0.25
done
printf '{"v":1,"id":"%s","status":"open","localPort":15173,"hostLabel":"alice-pc"}\n' \
  "${id}" >"${spool}/acks/${id}.json"
wait "${waiter}" 2>/dev/null
ok "ack: a hostLabel + remapped port build the link" \
  test "$(cat "${out}")" = "http://alice-pc:15173/"

# ── the ONE host-label rule (docs/expose.md) ─────────────────────────────────
# hostLabel travels BARE and is bracketed exactly once, here, when the link is built.
# Both spellings therefore print the same URL: the CLI used to bracket anything with a
# colon, so an already-bracketed label became http://[[fe80::1]]:5173/ while the service,
# which interpolated the label as it stood, printed http://fe80::1:5173/ for the bare one.
# Neither link opens, and no representation worked in both modes.
ack_link() {   # <ack json fields> -> the link the CLI prints for a client forward
  local fields="$1" link_out link_id
  reset_spool
  link_out="${tmp}/acklink.out"
  ( expose 5173 --wait 10 >"${link_out}" 2>&1; printf '%s' "$?" >"${tmp}/acklink.rc" ) &
  local waiter_pid=$!
  link_id=""
  for _ in $(seq 1 40); do
    if [[ -n "$(requests)" ]]; then link_id="$(request_id)"; break; fi
    sleep 0.25
  done
  printf '{"v":1,"id":"%s","status":"open","localPort":5173,%s}\n' "${link_id}" "${fields}" \
    >"${spool}/acks/${link_id}.json"
  wait "${waiter_pid}" 2>/dev/null
  cat "${link_out}"
}

ok "ack: a BARE IPv6 host label is bracketed exactly once" \
  test "$(ack_link '"hostLabel":"fe80::1"')" = "http://[fe80::1]:5173/"
ok "ack: ...and a BRACKETED one prints the identical link, never doubled" \
  test "$(ack_link '"hostLabel":"[fe80::1]"')" = "http://[fe80::1]:5173/"
ok "ack: a full literal too" \
  test "$(ack_link '"hostLabel":"[2001:db8::8a2e:370:7334]"')" = "http://[2001:db8::8a2e:370:7334]:5173/"
ok "ack: a label that is not an address at all falls back to a link that opens" \
  test "$(ack_link '"hostLabel":"1::2::3"')" = "http://localhost:5173/"
ok "ack: a zone id is not a wire host label (a URL would need %25)" \
  test "$(ack_link '"hostLabel":"fe80::1%eth0"')" = "http://localhost:5173/"
ok "ack: an IPv4 literal is passed through unbracketed" \
  test "$(ack_link '"hostLabel":"10.0.0.7"')" = "http://10.0.0.7:5173/"

# THE SHARED FIXTURE MATRIX. One rule, three implementations — `is_ipv6_literal` here,
# `net.isIP` in extension/src/forwarder.js and `IPAddress.TryParse` in the service's
# ForwardHost — and they are only one rule while they agree address for address. The SAME
# list runs in extension/test/forwarder.test.js and in Constructd.Tests' ForwardHostTests.
#
# Driven against the REAL functions, lifted out of the CLI by name (it has no library-only
# mode; extracting definitions is the trick test/idle-report.test.sh uses for provision.sh),
# so this is the shipped source and not a copy of it — an end-to-end `expose` per address
# would be a minute of sleeping for the same answer. The six link tests above already prove
# the wiring from ack document to printed link.
host_rule_src="$(sed -n '/^is_ipv4_literal()/,/^}$/p;/^ipv6_groups()/,/^}$/p;/^is_ipv6_literal()/,/^}$/p;/^wire_host_label()/,/^}$/p;/^url_host()/,/^}$/p' "${ROOT}/bin/construct-expose.sh")"
host_rule_count="$(printf '%s\n' "${host_rule_src}" | grep -c '^[a-z0-9_]*() {$' || true)"
ok "host rule: all five functions were found in the CLI (a rename must not skip the matrix)" \
  test "${host_rule_count}" = 5

# The link host for one hostLabel, exactly as link_from_ack composes the two helpers.
link_host() {
  HOST_RULE_SRC="${host_rule_src}" LBL="$1" bash -c '
    set -u
    eval "${HOST_RULE_SRC}"
    v="$(wire_host_label "${LBL}")"
    url_host "${v:-localhost}"
  '
}

ipv6_valid=(
  "::" "::1" "fe80::1" "2001:db8::8a2e:370:7334" "1:2:3:4:5:6:7:8"
  "0:0:0:0:0:0:0:0" "1::" "::2" "0::0" "::ffff:10.0.0.1"
  "1:2:3:4:5:6:1.2.3.4" "::1.2.3.4" "1::1.2.3.4" "1:2:3:4:5:6:7::"
  "fe80::0204:61ff:fe9d:f156" "ABCD::1"
)
# The four marked (*) are the pre-fix controls: a "plausible IPv6" shape filter accepts
# every one of them, and each then reached a URL authority as http://[1:::]:5173/ and
# friends. A real parse is what refuses them.
ipv6_invalid=(
  "::::" "1::2::3" "1:2:3:4:5:6:7:8:9" "1.2.3:4" "....:" ":::"
  ":1" "1:" "12345::1"        # (*)
  "::ffff:999.1.1.1" "::ffff:1.2.3.004"
  "1:2" "1:2:3:4:5:6:7" "1:::"  # (*) (*) (*)
  "1::2:3:4:5:6:7:8" "::1.2.3.4.5" "1:2:3:4:5:6:7:1.2.3.4"
  # An embedded IPv4 address is the literal's FINAL 32 bits, so it can never appear before
  # the "::" -- the grammar boundary a per-run "quad must be last" check misses.
  "192.0.2.1::" "192.0.2.1::1" "1:192.0.2.1::" "1.2.3.4::1:2"
)
for v6 in "${ipv6_valid[@]}"; do
  ok "host rule[matrix]: ${v6} is an address, bracketed exactly once" \
    test "$(link_host "${v6}")" = "[${v6}]"
  ok "host rule[matrix]: [${v6}] is the same address, still one pair" \
    test "$(link_host "[${v6}]")" = "[${v6}]"
done
for v6 in "${ipv6_invalid[@]}"; do
  ok "host rule[matrix]: ${v6} is not an address, so the link falls back to localhost" \
    test "$(link_host "${v6}")" = "localhost"
done
ok "host rule: a zone id is not a wire host label" test "$(link_host 'fe80::1%eth0')" = "localhost"
ok "host rule: a host name passes through" test "$(link_host 'alice-pc')" = "alice-pc"
ok "host rule: an empty label is loopback" test "$(link_host '')" = "localhost"

# An error ack is a final answer, not "keep waiting".
reset_spool
out="${tmp}/ackerr.out"
( expose 5173 --wait 10 >"${out}" 2>"${out}.err"; printf '%s' "$?" >"${tmp}/ackerr.rc" ) &
waiter=$!
id=""
for _ in $(seq 1 40); do
  if [[ -n "$(requests)" ]]; then id="$(request_id)"; break; fi
  sleep 0.25
done
printf '{"v":1,"id":"%s","status":"error","message":"port 5173 is busy on the PC"}\n' \
  "${id}" >"${spool}/acks/${id}.json"
wait "${waiter}" 2>/dev/null
ok "error ack: fails fast with exit 1" test "$(cat "${tmp}/ackerr.rc")" = 1
ok "error ack: shows the client's message" grep -q 'port 5173 is busy on the PC' "${out}.err"

# ── local mode: --list and --close ───────────────────────────────────────────

reset_spool
expose 3000 --label api --wait 0 >/dev/null 2>&1
id_a="$(request_id)"
expose 8080 --wait 0 >/dev/null 2>&1
printf '{"v":1,"id":"%s","status":"open","localPort":3000}\n' "${id_a}" >"${spool}/acks/${id_a}.json"
listing="${tmp}/list.out"
expose --list >"${listing}" 2>&1
ok "--list has a header row" grep -qE '^ID +PORT +TARGET +STATUS +LABEL +URL' "${listing}"
ok "--list shows the acked forward as open, with its link" \
  sh -c "grep -q '${id_a}' '${listing}' && grep -q 'http://localhost:3000/' '${listing}'"
ok "--list shows the unacked forward as queued" grep -qE '8080 +client +queued' "${listing}"
ok "--list carries the label" grep -q 'api' "${listing}"

expose --close 8080 >"${tmp}/close.out" 2>&1
ok "--close by port succeeds" test "$?" = 0
ok "--close removes the request" test "$(requests | wc -l)" = 1
ok "--close leaves a close document for the extension" \
  sh -c "ls '${spool}/close/'*.json >/dev/null 2>&1"
ok "--close by id works too" sh -c "CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose --close '${id_a}' >/dev/null 2>&1"
ok "--close removed the ack as well" test ! -e "${spool}/acks/${id_a}.json"
ok "--close cannot escape the spool with a path" \
  sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose --close '../../etc/passwd' >/dev/null 2>&1"
ok "--close of an unknown reference is an error" \
  sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose --close 9999 >/dev/null 2>&1"

# ── local mode: the host target degrades to the VM's own address ─────────────

reset_spool
host_out="$(CONSTRUCT_FORWARDS_DIR="${spool}" CONFIG_FILE="${empty_cfg}" \
  CONSTRUCT_EXTERNAL_HOST=myvm.example.net bash "${CLI}" expose 3000 --to host 2>&1)"
ok "--to host uses CONSTRUCT_EXTERNAL_HOST" test "${host_out}" = "http://myvm.example.net:3000/"
ok "--to host spools nothing (the address is already reachable)" test -z "$(requests)"

host_v6="$(CONSTRUCT_FORWARDS_DIR="${spool}" CONFIG_FILE="${empty_cfg}" \
  CONSTRUCT_EXTERNAL_HOST=2001:db8::1 bash "${CLI}" expose 3000 --to host 2>&1)"
ok "--to host brackets an IPv6 literal" test "${host_v6}" = "http://[2001:db8::1]:3000/"

# The default target comes from config.env when --to is not given.
target_cfg="${tmp}/config_target.env"
printf 'CONSTRUCT_EXPOSE_DEFAULT_TARGET=host\nCONSTRUCT_EXTERNAL_HOST=fromcfg.example.net\n' >"${target_cfg}"
default_target_out="$(CONSTRUCT_FORWARDS_DIR="${spool}" CONFIG_FILE="${target_cfg}" \
  bash "${CLI}" expose 3000 2>&1)"
ok "the default target is read from config.env" \
  test "${default_target_out}" = "http://fromcfg.example.net:3000/"
ok "an explicit --to still wins over config.env" \
  sh -c "CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${target_cfg}' bash '${CLI}' expose 3000 --to client --wait 0 2>&1 | grep -q 'no Construct client'"

# ── remote mode: the host service ────────────────────────────────────────────

remote_cfg="${tmp}/config_remote.env"
printf 'CONSTRUCT_SERVICE_URL=https://buildbox.example.local:7462\n' >"${remote_cfg}"
printf 'CONSTRUCT_INSTANCE_NAME=work-vm\n' >>"${remote_cfg}"
token_file="${tmp}/vm-token"
printf 'sekrit-vm-token-value\n' >"${token_file}"
chmod 0600 "${token_file}"

remote() {
  STUB_DIR="${stub_dir}" PATH="${stubs}:${PATH}" \
    CONFIG_FILE="${remote_cfg}" CONSTRUCT_FORWARDS_DIR="${spool}" \
    CONSTRUCT_VM_TOKEN_FILE="${token_file}" bash "${CLI}" expose "$@"
}

# A host forward: the service answers with the URL it materialized.
reset_stub
printf '201' >"${stub_dir}/code"
printf '{"id":"fwd-1","vmName":"work-vm","vmPort":3000,"publicPort":31234,"target":"host","label":"hook","created":"2026-09-01T09:00:00Z","url":"http://buildbox.example.local:31234/"}' \
  >"${stub_dir}/response"
remote_out="$(remote 3000 --label hook --to host 2>&1)"
ok "remote host target: prints the service's URL" \
  test "${remote_out}" = "http://buildbox.example.local:31234/"
ok "remote: POSTs to the instance's forwards route" \
  grep -qx 'POST https://buildbox.example.local:7462/api/v1/vms/work-vm/forwards' "${stub_dir}/requests"
ok "remote: sends the VM token in an Authorization header" \
  grep -qx 'Authorization: VmToken sekrit-vm-token-value' "${stub_dir}/headers"
ok "remote: the token is NEVER on the command line" \
  sh -c "! grep -q 'sekrit-vm-token-value' '${stub_dir}/argv'"
ok "remote: the header is passed as a file reference" grep -q -- '-H @' "${stub_dir}/argv"
ok "remote: the body carries vmPort, label and target" \
  sh -c "grep -q '\"vmPort\":3000' '${stub_dir}/bodies' && grep -q '\"target\":\"host\"' '${stub_dir}/bodies' && grep -q '\"label\":\"hook\"' '${stub_dir}/bodies'"
ok "remote: fails loudly rather than trusting an unverified certificate" \
  sh -c "! grep -qE -- '--insecure|-k( |$)' '${stub_dir}/argv'"
ok "remote: asks curl to keep the body of a failed response" \
  grep -q -- '--fail-with-body' "${stub_dir}/argv"

# A client forward that the extension opens on the second poll.
reset_stub
printf '201' >"${stub_dir}/code"
printf '{"id":"fwd-2","vmPort":5173,"target":"client","label":"vite","url":null}' >"${stub_dir}/response"
printf '200' >"${stub_dir}/code-2"
printf '[{"id":"fwd-2","vmPort":5173,"target":"client","label":"vite","url":null}]' >"${stub_dir}/response-2"
printf '200' >"${stub_dir}/code-3"
printf '[{"id":"fwd-2","vmPort":5173,"target":"client","label":"vite","localPort":5173,"status":"open"}]' \
  >"${stub_dir}/response-3"
client_out="$(remote 5173 --label vite --wait 6 2>&1)"
ok "remote client target: polls until the extension reports the port" \
  test "${client_out}" = "http://localhost:5173/"
ok "remote client target: defaults the body's target to client" \
  grep -q '"target":"client"' "${stub_dir}/bodies"
ok "remote client target: polls the forwards list with GET" \
  grep -qx 'GET https://buildbox.example.local:7462/api/v1/vms/work-vm/forwards' "${stub_dir}/requests"

# Nobody picks it up: queued, exit 6 -- same contract as local mode.
reset_stub
printf '201' >"${stub_dir}/code"
printf '{"id":"fwd-3","vmPort":5173,"target":"client","url":null}' >"${stub_dir}/response"
printf '200' >"${stub_dir}/code-GET"
printf '[{"id":"fwd-3","vmPort":5173,"target":"client","url":null}]' >"${stub_dir}/response-GET"
remote 5173 --wait 2 >"${tmp}/remote_queued.out" 2>"${tmp}/remote_queued.err"
ok "remote client target: exits 6 when no client picks it up" test "$?" = 6
ok "remote client target: explains that no client is attached" \
  grep -q 'no Construct client is attached' "${tmp}/remote_queued.err"

# A failing poll must NOT masquerade as "queued": the request may well be open,
# we simply cannot tell, and exit 6 would tell the agent to keep waiting.
poll_failure_case() { # <http-code> <expected-exit> <name> [curl-exit]
  reset_stub
  printf '201' >"${stub_dir}/code"
  printf '{"id":"fwd-5","vmPort":5173,"target":"client","url":null}' >"${stub_dir}/response"
  printf '%s' "$1" >"${stub_dir}/code-GET"
  printf '{"title":"nope","detail":"the list blew up"}' >"${stub_dir}/response-GET"
  [[ -n "${4:-}" ]] && printf '%s' "$4" >"${stub_dir}/exit"
  remote 5173 --wait 3 >"${tmp}/poll.out" 2>"${tmp}/poll.err"
  local rc=$?
  ok "$3" test "${rc}" = "$2"
  ok "$3 (does not claim it is merely queued)" \
    sh -c "! grep -q 'no Construct client is attached' '${tmp}/poll.err'"
}
poll_failure_case 500 8 "polling a 5xx exits 8"
poll_failure_case 401 8 "polling a 401 exits 8"
poll_failure_case 403 7 "polling a 403 exits 7"
poll_failure_case 000 8 "polling a dead connection exits 8" 7

# A 2xx whose body is not the JSON shape the API promises is a service problem,
# not an empty list (docs/expose.md: unparsable body -> exit 8).
reset_stub
printf '201' >"${stub_dir}/code"
printf '{"id":"fwd-6","vmPort":5173,"target":"client","url":null}' >"${stub_dir}/response"
printf '200' >"${stub_dir}/code-GET"
printf '<html>captive portal</html>' >"${stub_dir}/response-GET"
remote 5173 --wait 3 >"${tmp}/garbage.out" 2>"${tmp}/garbage.err"
ok "polling an unreadable 2xx body exits 8" test "$?" = 8
ok "polling an unreadable 2xx body does not claim it is queued" \
  sh -c "! grep -q 'no Construct client is attached' '${tmp}/garbage.err'"

reset_stub
printf '201' >"${stub_dir}/code"
printf 'not json at all' >"${stub_dir}/response"
remote 5173 --to host >"${tmp}/garbage2.out" 2>"${tmp}/garbage2.err"
ok "a POST answered with an unreadable 2xx body exits 8" test "$?" = 8
ok "…and says the answer could not be read" \
  grep -q 'cannot read' "${tmp}/garbage2.err"

reset_stub
printf '200' >"${stub_dir}/code"
printf '<html>not a forward list</html>' >"${stub_dir}/response"
remote --list >"${tmp}/garbage3.out" 2>"${tmp}/garbage3.err"
ok "--list against an unreadable 2xx body exits 8 instead of printing nothing" \
  test "$?" = 8

# Host forwards disabled for this user.
reset_stub
printf '403' >"${stub_dir}/code"
printf '{"type":"about:blank","title":"Forbidden","status":403,"detail":"Host-target forwards are disabled for '\''DOMAIN\\\\alice'\''. Use target=client."}' \
  >"${stub_dir}/response"
remote 3000 --to host >"${tmp}/forbidden.out" 2>"${tmp}/forbidden.err"
ok "remote: a 403 exits 7" test "$?" = 7
ok "remote: a 403 says the host forwards are disabled" \
  grep -q 'Host-target forwards are disabled' "${tmp}/forbidden.err"

# The service is unreachable.
reset_stub
printf '000' >"${stub_dir}/code"
printf '7' >"${stub_dir}/exit"
: >"${stub_dir}/response"
remote 3000 >"${tmp}/down.out" 2>"${tmp}/down.err"
ok "remote: an unreachable service exits 8" test "$?" = 8
ok "remote: an unreachable service names the URL" \
  grep -q 'buildbox.example.local:7462' "${tmp}/down.err"

# No token at all.
reset_stub
printf '201' >"${stub_dir}/code"
STUB_DIR="${stub_dir}" PATH="${stubs}:${PATH}" CONFIG_FILE="${remote_cfg}" \
  CONSTRUCT_VM_TOKEN_FILE="${tmp}/absent-token" bash "${CLI}" expose 3000 \
  >"${tmp}/notoken.out" 2>"${tmp}/notoken.err"
ok "remote: a missing VM token exits 8 with an explanation" \
  sh -c "test \"$?\" = 8 && grep -q 'no VM token' '${tmp}/notoken.err'"
ok "remote: a missing VM token makes no request at all" test ! -f "${stub_dir}/requests"

# --list and --close speak to the service, not to the spool.
reset_stub
printf '200' >"${stub_dir}/code"
printf '[{"id":"fwd-9","vmPort":8080,"target":"host","label":"hook","url":"http://buildbox.example.local:31235/"}]' \
  >"${stub_dir}/response"
remote --list >"${tmp}/remote_list.out" 2>&1
ok "remote --list renders the service's forwards" \
  sh -c "grep -q 'fwd-9' '${tmp}/remote_list.out' && grep -q 'http://buildbox.example.local:31235/' '${tmp}/remote_list.out'"

reset_stub
printf '200' >"${stub_dir}/code"
printf '[{"id":"fwd-9","vmPort":8080,"target":"host","url":null}]' >"${stub_dir}/response"
printf '204' >"${stub_dir}/code-2"
: >"${stub_dir}/response-2"
remote --close 8080 >"${tmp}/remote_close.out" 2>&1
ok "remote --close resolves the port to an id and DELETEs it" \
  grep -qx 'DELETE https://buildbox.example.local:7462/api/v1/vms/work-vm/forwards/fwd-9' "${stub_dir}/requests"

# A pinned certificate is handed to curl.
reset_stub
printf '201' >"${stub_dir}/code"
printf '{"id":"fwd-4","vmPort":3000,"target":"host","url":"http://x/"}' >"${stub_dir}/response"
ca="${tmp}/service-ca.pem"
: >"${ca}"
STUB_DIR="${stub_dir}" PATH="${stubs}:${PATH}" CONFIG_FILE="${remote_cfg}" \
  CONSTRUCT_VM_TOKEN_FILE="${token_file}" CONSTRUCT_SERVICE_CA_FILE="${ca}" \
  bash "${CLI}" expose 3000 --to host >/dev/null 2>&1
ok "remote: CONSTRUCT_SERVICE_CA_FILE is passed as --cacert" grep -qx "${ca}" "${stub_dir}/cacert"

# ── usage, help and validation ───────────────────────────────────────────────

help_out="${tmp}/help.out"
expose --help >"${help_out}" 2>&1
ok "--help documents the client target" grep -q "USER'S PC" "${help_out}"
ok "--help documents the host target" grep -qE '^  host ' "${help_out}"
ok "--help documents the exit codes" grep -q 'no client attached' "${help_out}"
ok "--help documents the config keys" grep -q 'CONSTRUCT_SERVICE_URL' "${help_out}"
# ── the existing verbs stay byte-identical ───────────────────────────────────
# Adding a verb must not change a single character of what the CLI printed
# before it existed: `construct help` (which `notify --help` also prints) and the
# diagnostics for existing invocations are part of the zero-change contract.
# `construct expose --help` and the system-prompt paragraph are where the new
# verb is documented.

head_cli="${tmp}/head-construct"
if git -C "${ROOT}" show HEAD:bin/construct >"${head_cli}" 2>/dev/null; then
  same_output() { # <name> <args...>
    local name="$1"
    shift
    local before after
    before="$(NOTIFY_SPOOL="${tmp}/nospool" PROJECTS_STORE="${tmp}/noprojects" \
      bash "${head_cli}" "$@" 2>&1; printf 'rc=%s' "$?")"
    after="$(NOTIFY_SPOOL="${tmp}/nospool" PROJECTS_STORE="${tmp}/noprojects" \
      bash "${CLI}" "$@" 2>&1; printf 'rc=%s' "$?")"
    ok "${name}" test "${before}" = "${after}"
  }
  same_output "construct help is byte-identical to HEAD" help
  same_output "construct --help is byte-identical to HEAD" --help
  same_output "construct notify --help is byte-identical to HEAD" notify --help
  same_output "an unknown command still says exactly what it said before" teleport
  same_output "construct with no arguments is unchanged"
  same_output "an unknown notify option is unchanged" notify msg --shout
  same_output "project list on an empty store is unchanged" project list
  same_output "an invalid profile name is unchanged" project get ../secret
else
  printf '  WARN  could not read bin/construct from HEAD; skipping the base diff\n'
fi

reset_spool
ok "a missing port is a usage error" sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose >/dev/null 2>&1"
ok "a non-numeric port is rejected" sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose http >/dev/null 2>&1"
ok "port 0 is rejected" sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose 0 >/dev/null 2>&1"
ok "port 70000 is rejected" sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose 70000 >/dev/null 2>&1"
ok "an unknown target is rejected" sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose 3000 --to lan >/dev/null 2>&1"
ok "an unknown option is rejected" sh -c "! CONSTRUCT_FORWARDS_DIR='${spool}' CONFIG_FILE='${empty_cfg}' bash '${CLI}' expose 3000 --tunnel >/dev/null 2>&1"
ok "a rejected request spools nothing" test -z "$(requests)"

# Hostile label text cannot break the one-line JSON contract.
reset_spool
CONSTRUCT_FORWARDS_DIR="${spool}" CONFIG_FILE="${empty_cfg}" \
  bash "${CLI}" expose 3000 --label "$(printf 'a\nb\ttab "quoted" \\ back')" --wait 0 >/dev/null 2>&1
ok "a hostile label stays one line" test "$(wc -l <"$(first_request)")" = 1
ok "a hostile label is JSON-escaped" grep -q '\\"quoted\\"' "$(first_request)"
if command -v python3 >/dev/null 2>&1; then
  ok "the request is still valid JSON" \
    sh -c "python3 -c 'import json,sys; json.load(open(sys.argv[1]))' '$(first_request)'"
fi

# A value config-set.sh had to quote must come back verbatim.
quoted_cfg="${tmp}/config_quoted.env"
bash "${ROOT}/bin/config-set.sh" "${quoted_cfg}" CONSTRUCT_EXTERNAL_HOST "fe80::1%12"
quoted_out="$(CONSTRUCT_FORWARDS_DIR="${spool}" CONFIG_FILE="${quoted_cfg}" \
  bash "${CLI}" expose 3000 --to host 2>&1)"
ok "a quoted saved value is decoded (no quote characters leak)" \
  test "${quoted_out}" = "http://[fe80::1%12]:3000/"

# config.env must not be able to set internal variables (it is read key by key,
# never sourced).
poison_cfg="${tmp}/config_poison.env"
printf 'CONSTRUCT_EXTERNAL_HOST=poison.example.net\nFORWARDS_DIR=/tmp/evil\nVM_TOKEN_FILE=/tmp/evil-token\n' >"${poison_cfg}"
reset_spool
CONSTRUCT_FORWARDS_DIR="${spool}" CONFIG_FILE="${poison_cfg}" \
  bash "${CLI}" expose 3000 --wait 0 >/dev/null 2>&1
ok "config.env cannot redirect the spool directory" test "$(requests | wc -l)" = 1
ok "config.env cannot redirect the spool directory (no evil path)" test ! -d /tmp/evil

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
