#!/usr/bin/env bash
# Contract tests for the guest-side `construct vm` client. The HTTP service is a
# route-aware curl stub: no real service, VM or Hyper-V host is touched.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="${ROOT}/bin/construct"
tmp="$(mktemp -d)"
trap 'if [[ "${KEEP_TEST_TMP:-0}" == 1 ]]; then printf "kept test fixture: %s\\n" "${tmp}"; else rm -r "${tmp}"; fi' EXIT

pass=0
fail=0
ok() {
  local name="$1"; shift
  if "$@"; then pass=$((pass + 1)); printf '  PASS  %s\n' "${name}"
  else fail=$((fail + 1)); printf '  FAIL  %s\n' "${name}"
  fi
}

stub="${tmp}/stub"
mkdir -p "${stub}/bin" "${stub}/state"
token_file="${tmp}/vm-token"
printf 'sentinel-vm-token\n' >"${token_file}"
chmod 0600 "${token_file}"
ca_file="${tmp}/service-ca.pem"
printf 'test ca\n' >"${ca_file}"
config="${tmp}/config.env"
cat >"${config}" <<EOF
CONSTRUCT_SERVICE_URL=https://construct.example:7462
CONSTRUCT_INSTANCE_NAME=parent-vm
CONSTRUCT_SERVICE_CA_FILE=${ca_file}
CONSTRUCT_VM_TOKEN_FILE=${token_file}
EOF

cat >"${stub}/bin/curl" <<'STUB'
#!/usr/bin/env bash
set -u
d="${VM_STUB_DIR}"
method=GET
url=""
out=""
data=""
dump_headers=""
write_status=false
headers=()
args=("$@")
for ((i=0; i<${#args[@]}; i++)); do
  case "${args[i]}" in
    -X) method="${args[i+1]}" ;;
    -o) out="${args[i+1]}" ;;
    -D) dump_headers="${args[i+1]}" ;;
    -w) write_status=true ;;
    --data-binary) data="${args[i+1]#@}" ;;
    -H) headers+=("${args[i+1]}") ;;
    http://*|https://*) url="${args[i]}" ;;
  esac
done
path="/${url#*://*/}"
printf '%s\t%s\n' "${method}" "${path}" >>"${d}/requests"
printf '%s\0' "$@" >>"${d}/argv"
for h in "${headers[@]}"; do
  case "${h}" in
    @*) cat "${h#@}" >>"${d}/headers" ;;
  esac
done
if [[ -n "${data}" ]]; then
  printf '%s\t%s\t' "${method}" "${path}" >>"${d}/bodies"
  cat "${data}" >>"${d}/bodies"
  printf '\n' >>"${d}/bodies"
fi

code=200
body='{}'
case "${path}" in
  /api/v1/vms/parent-vm/identity)
    if [[ "${VM_SCENARIO:-}" == legacy ]]; then
      body='{"vmName":"parent-vm","kind":"primary","owner":"alice","tokenKind":"legacy","delegation":null}'
    elif [[ "${VM_SCENARIO:-}" == creation-disabled ]]; then
      body='{"vmName":"parent-vm","kind":"primary","owner":"alice","tokenKind":"primary","delegation":{"allowChildCreation":false}}'
    else
      body='{"vmName":"parent-vm","kind":"primary","owner":"alice","tokenKind":"primary","delegation":{"allowChildCreation":true,"maxRetainedChildren":2}}'
    fi
    ;;
  /api/v1/vms\?parent=parent-vm)
    body='[{"name":"kid-one","state":"off","sharing":"private","hardware":{"cpus":2,"ramMb":1024,"diskGb":20},"lease":{"state":"inactive"}}]'
    if [[ "${VM_SCENARIO:-}" == inventory ]]; then
      body='[{"name":"kid-one","state":"running","cpu":2,"ramGb":1,"diskGb":20,"hardware":null,"lease":{"state":"active","expiresAt":"2026-09-07T10:00:00Z","overdue":true},"currentOperation":{"jobId":"job-active","kind":"vm-shutdown"}}]'
    fi
    ;;
  /api/v1/vms/shared)
    body='[{"name":"shared-one","state":"running","sharing":"host","hardware":{"cpus":1,"ramMb":512,"diskGb":8},"lease":{"expiresAt":"2026-09-08T00:00:00Z"}}]'
    ;;
  /api/v1/vms/kid-one)
    if [[ "${method}" == DELETE ]]; then code=202; body='{"jobId":"job-life"}'
    else body='{"name":"kid-one","state":"running","kind":"child","parent":"parent-vm","sharing":"private"}'; fi
    if [[ "${method}" == DELETE && "${VM_SCENARIO:-}" == replay ]]; then code=200; body='{"jobId":"job-life","replayed":true}'; fi
    ;;
  /api/v1/vms/kid-one/addresses)
    body='{"addresses":[],"isolation":"none","network":{"clientForward":"supported"}}'
    ;;
  /api/v1/vms/kid-one/capabilities)
    body='{"console":{"screenshot":"supported","keyboard":"supported","mouseAbsolute":"conditional"}}'
    ;;
  /api/v1/vms/kid-one/lifecycle)
    count_file="${d}/lifecycle-count"
    count=$(( $(cat "${count_file}" 2>/dev/null || printf 0) + 1 ))
    printf '%s' "${count}" >"${count_file}"
    if [[ "${VM_SCENARIO:-}" == retry-start && "${count}" -eq 1 ]]; then
      code=000; body=''; printf '%s' "${body}" >"${out}"; printf 000; exit 7
    fi
    action="$(jq -r '.action' "${data}")"
    if [[ "${action}" == shutdown || "${action}" == restart ]]; then code=202; body='{"jobId":"job-life"}'
    else body='{"state":"running","lease":{"state":"active","expiresAt":"2026-09-08T00:00:00Z"}}'; fi
    if [[ "${VM_SCENARIO:-}" == replay ]]; then body='{"state":"running","replayed":true}'; fi
    ;;
  /api/v1/vms/kid-one/lease) body='{"state":"active","requestedText":"2h","expiresAt":"2026-09-08T00:00:00Z"}' ;;
  /api/v1/vms/kid-one/sharing) body='{"scope":"host"}' ;;
  /api/v1/vms/kid-one/hardware) body='{"cpus":4,"ramMb":2048,"diskGb":40,"firmware":{"secureBoot":true,"tpm":false}}' ;;
  /api/v1/vms/kid-one/media)
    body='[{"id":"media-install","role":"install","state":"ready"}]'
    ;;
  /api/v1/vms/kid-one/forwards)
    code=201
    if [[ -n "${data}" && "$(jq -r '.target' "${data}")" == client ]]; then body='{"id":"forward-1","vmPort":8080,"target":"client","url":null,"destination":{"verified":false}}'
    else body='{"id":"forward-1","vmPort":8080,"target":"host","url":"http://construct.example:28080/","destination":{"verified":false}}'; fi
    ;;
  /api/v1/vms/kid-one/forwards\?via=parent-vm)
    if [[ "${VM_SCENARIO:-}" == forward-timeout ]]; then body='[{"id":"forward-1","vmPort":8080,"target":"client","url":null}]'
    elif [[ "${VM_SCENARIO:-}" == forward-ipv6 ]]; then body='[{"id":"forward-1","vmPort":8080,"target":"client","localPort":18080,"hostLabel":"2001:db8::8","status":"open","destination":{"verified":true}}]'
    else body='[{"id":"forward-1","vmPort":8080,"target":"client","localPort":18080,"status":"open","destination":{"verified":false}}]'; fi
    ;;
  /api/v1/vms/kid-one/console/sessions)
    code=201; body='{"sessionId":"session-1","expiresAt":"2026-09-07T12:01:00Z","screen":{"width":1024,"height":768}}'
    ;;
  /api/v1/vms/kid-one/console/sessions/session-1/screenshot*) body='PNG-BYTES' ;;
  /api/v1/vms/kid-one/console/sessions/session-1/keyboard)
    if [[ "${VM_SCENARIO:-}" == keyboard-transport ]]; then printf '' >"${out}"; printf 000; exit 7; fi
    body='{"accepted":true,"returnValue":0}'
    ;;
  /api/v1/vms/kid-one/console/sessions/session-1/mouse)
    if [[ "${VM_SCENARIO:-}" == mouse-unavailable ]]; then body='{"applied":false,"unavailable":{"device":"syntheticMouse","returnValue":32768,"fallback":"moveRelative"}}'
    else body='{"applied":true}'; fi
    ;;
  /api/v1/vms/kid-one/console/sessions/session-1) code=204; body='' ;;
  /api/v1/media)
    if [[ "${method}" == GET ]]; then body='[{"id":"media-install","name":"installer.iso","role":"install","state":"ready","sizeBytes":4096}]'
    fi
    ;;
  /api/v1/media/media-install)
    if [[ "${method}" == DELETE ]]; then code=204; body=''
    else body='{"id":"media-install","name":"installer.iso","role":"install","state":"ready","sizeBytes":4096}'; fi
    if [[ "${VM_SCENARIO:-}" == media-pending ]]; then body='{"id":"media-install","name":"installer.iso","role":"install","state":"transferring"}'; fi
    if [[ "${VM_SCENARIO:-}" == media-in-use ]]; then code=409; body='{"title":"Media in use","detail":"Attached media cannot be deleted","code":"media-in-use","references":[{"vmName":"kid-one","slot":"install"}]}'; fi
    ;;
  /api/v1/media/media-aux)
    body='{"id":"media-aux","name":"answers.iso","role":"auxiliary","state":"ready","sizeBytes":2048}'
    if [[ "${VM_SCENARIO:-}" == aux-pending ]]; then body='{"id":"media-aux","state":"pending"}'; fi
    ;;
  /api/v1/media/acquire) code=202; body='{"jobId":"job-media","mediaId":"media-install"}' ;;
  /api/v1/media/uploads)
    code=201; body='{"uploadId":"upload-1","mediaId":"media-upload","chunkSizeBytes":4,"chunkCount":2,"expiresAt":"2026-09-08T00:00:00Z","received":[]}'
    ;;
  /api/v1/media/uploads/upload-1)
    body='{"state":"open","received":[],"missing":[0,1],"expiresAt":"2026-09-08T00:00:00Z"}'
    if [[ "${VM_SCENARIO:-}" == upload-resume ]]; then body='{"state":"open","received":[0],"missing":[1]}'; fi
    ;;
  /api/v1/media/uploads/upload-1/chunks/*)
    printf '%s\n' "${data}" >>"${d}/chunk-paths"
    code=204; body=''
    ;;
  /api/v1/media/uploads/upload-1/complete)
    code=201; body='{"id":"media-upload","name":"tiny.iso","role":"install","state":"ready","sizeBytes":8}'
    ;;
  /api/v1/vms/parent-vm/children)
    code=202; body='{"jobId":"job-create"}'
    ;;
  /api/v1/jobs)
    body='[{"id":"job-create","kind":"child-create","state":"running","phase":"start","vmName":"kid-one"}]'
    ;;
  /api/v1/jobs/job-create) body='{"id":"job-create","kind":"child-create","state":"succeeded","result":{"name":"kid-one"}}' ;;
  /api/v1/jobs/job-life) body='{"id":"job-life","kind":"vm-shutdown","state":"succeeded","result":{"name":"kid-one","outcome":"completed","finalState":"off"}}' ;;
  /api/v1/jobs/job-media) body='{"id":"job-media","kind":"media-acquire","state":"succeeded","result":{"id":"media-install"}}' ;;
  /api/v1/jobs/job-create/events|/api/v1/jobs/job-life/events|/api/v1/jobs/job-media/events)
    if [[ "${VM_SCENARIO:-}" == stream-forbidden ]]; then
      printf 'HTTP/1.1 403 Forbidden\r\nContent-Type: application/problem+json\r\n\r\n' >"${dump_headers}"
      printf '{"title":"Forbidden","detail":"Job access revoked","code":"not-owner"}'
      exit 22
    fi
    [[ -n "${dump_headers}" ]] && printf 'HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n' >"${dump_headers}"
    case "${path}" in
      *job-create*) kind=child-create; result='{"name":"kid-one"}' ;;
      *job-media*) kind=media-acquire; result='{"id":"media-install"}' ;;
      *) kind=vm-shutdown; result='{"name":"kid-one","outcome":"completed","finalState":"off"}' ;;
    esac
    if [[ "${VM_SCENARIO:-}" == shutdown-timeout ]]; then result='{"name":"kid-one","outcome":"timeout","finalState":"running"}'; fi
    if [[ "${VM_SCENARIO:-}" == shutdown-unavailable ]]; then result='{"name":"kid-one","outcome":"unavailable","finalState":"running"}'; fi
    if [[ "${VM_SCENARIO:-}" == nonterminal-state ]]; then printf 'event: state\ndata: {"id":"job-life","state":"running"}\n\n'; fi
    printf 'event: phase\ndata: {"at":"2026-09-07T12:00:00Z","phase":"start"}\n\n'
    printf 'event: progress\ndata: {"at":"2026-09-07T12:00:01Z","text":"working"}\n\n'
    terminal_state=succeeded
    [[ "${VM_SCENARIO:-}" == job-failed ]] && terminal_state=failed
    printf 'event: state\ndata: {"id":"%s","kind":"%s","state":"%s","result":%s,"error":%s}\n\n' "${path%/events}" "${kind}" "${terminal_state}" "${result}" "$([[ "${terminal_state}" == failed ]] && printf '"fixture failure"' || printf null)"
    exit 0
    ;;
  /api/v1/jobs/job-create/cancel) body='{"cancelled":true}' ;;
  *)
    if [[ "${VM_SCENARIO:-}" == invalid-json ]]; then code=200; body='<html>not json</html>'
    elif [[ -n "${VM_ERROR_STATUS:-}" ]]; then code="${VM_ERROR_STATUS}"; body="{\"title\":\"Rejected\",\"detail\":\"fixture refusal\",\"code\":\"${VM_ERROR_CODE:-validation}\"}"
    else code=404; body='{"title":"Not found","detail":"fixture route missing","code":"not-found"}'; fi
    ;;
esac

[[ -n "${dump_headers}" ]] && printf 'HTTP/1.1 %s Test\r\nContent-Type: application/json\r\n\r\n' "${code}" >"${dump_headers}"
if [[ -n "${out}" ]]; then printf '%s' "${body}" >"${out}"; else printf '%s' "${body}"; fi
[[ "${write_status}" == true ]] && printf '%s' "${code}"
[[ "${code}" =~ ^2 ]] && exit 0
exit 22
STUB
chmod 0755 "${stub}/bin/curl"

reset_stub() { rm -r "${stub}/state"; mkdir -p "${stub}/state"; }
vm() {
  VM_STUB_DIR="${stub}/state" VM_SCENARIO="${VM_SCENARIO:-}" VM_ERROR_STATUS="${VM_ERROR_STATUS:-}" VM_ERROR_CODE="${VM_ERROR_CODE:-}" \
    PATH="${stub}/bin:${PATH}" CONFIG_FILE="${config}" CONSTRUCT_VM_POLL_INTERVAL_SEC=0 bash "${CLI}" vm "$@"
}

# Configuration, identity gate and credential hygiene.
CONFIG_FILE="${tmp}/empty.env" CONSTRUCT_VM_TOKEN_FILE="${token_file}" bash "${CLI}" vm list >"${tmp}/unmanaged.out" 2>"${tmp}/unmanaged.err"
ok 'an unmanaged guest exits 9' test "$?" = 9
ok 'an unmanaged guest says why' grep -q 'not service-managed' "${tmp}/unmanaged.err"

reset_stub
VM_SCENARIO=legacy vm list >"${tmp}/legacy.out" 2>"${tmp}/legacy.err"
ok 'a legacy credential exits 9' test "$?" = 9
ok 'legacy refusal prints the documented upgrade hint' grep -q 'Provision-AgentVM.ps1 -InstanceName <primary> -RotateVmToken' "${tmp}/legacy.err"
VM_SCENARIO=legacy vm identity --json >"${tmp}/identity.json" 2>/dev/null
ok 'identity is the one command allowed for a legacy token' test "$?" = 0
ok 'identity preserves the token kind' jq -e '.tokenKind=="legacy"' "${tmp}/identity.json"

reset_stub
vm list --json >"${tmp}/list.json" 2>"${tmp}/list.err"
ok 'list returns the API array as JSON' jq -e 'length==1 and .[0].name=="kid-one"' "${tmp}/list.json"
ok 'every command discovers identity first' test "$(head -n 1 "${stub}/state/requests")" = $'GET\t/api/v1/vms/parent-vm/identity'
ok 'list uses the parent filter' grep -q $'GET\t/api/v1/vms?parent=parent-vm' "${stub}/state/requests"
ok 'the token is sent through a header file' grep -q '^Authorization: VmToken sentinel-vm-token$' "${stub}/state/headers"
ok 'the token never appears in curl argv' sh -c "! tr '\0' '\n' <'${stub}/state/argv' | grep -q sentinel-vm-token"
ok 'the pinned CA is always passed to curl' sh -c "tr '\0' '\n' <'${stub}/state/argv' | grep -A1 -- --cacert | grep -q '${ca_file}'"

reset_stub
vm list --all-shared --json >"${tmp}/shared.json" 2>/dev/null
ok '--all-shared combines owned and shared children' jq -e 'length==2 and ((map(.name)|index("shared-one"))!=null)' "${tmp}/shared.json"

reset_stub
vm inspect kid-one --json >"${tmp}/inspect.json" 2>/dev/null
ok 'inspect composes VM, address and capability responses' jq -e '.vm.name=="kid-one" and (.addresses.addresses|length)==0 and .capabilities.console.screenshot=="supported"' "${tmp}/inspect.json"

# Lifecycle, retries, SSE and operation keys.
reset_stub
vm start kid-one >"${tmp}/missing-life.out" 2>"${tmp}/missing-life.err"
ok 'start without a lifetime is a usage error' test "$?" = 1
ok 'start without a lifetime never reaches lifecycle' sh -c "! grep -q lifecycle '${stub}/state/requests'"

reset_stub
VM_SCENARIO=retry-start vm start kid-one --lifetime 2h --operation-id retry-key-123 --json >"${tmp}/start.json" 2>"${tmp}/start.err"
ok 'a transport failure is retried with the same operation id' test "$(grep -c $'POST\t/api/v1/vms/kid-one/lifecycle' "${stub}/state/requests")" = 2
ok 'start JSON includes the operation id' jq -e '.operationKey=="retry-key-123" and .state=="running"' "${tmp}/start.json"
ok 'both retry attempts carried the operation key header' test "$(grep -c '^X-Construct-Operation-Key: retry-key-123$' "${stub}/state/headers")" = 2
ok 'start request carries action and mandatory lifetime' grep -q '"action":"start","lifetime":"2h"' "${stub}/state/bodies"

reset_stub
vm start kid-one --lifetime 2h --json >"${tmp}/generated-key.json" 2>"${tmp}/generated-key.err"
generated_key="$(jq -r '.operationKey' "${tmp}/generated-key.json")"
ok 'a generated operation id is a UUID and is included in JSON' \
  sh -c "printf '%s' '${generated_key}' | grep -Eq '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'"
ok 'the generated operation id is printed to stderr' grep -q "^operation id: ${generated_key}$" "${tmp}/generated-key.err"
ok 'the generated operation id is the key sent to the service' grep -q "^X-Construct-Operation-Key: ${generated_key}$" "${stub}/state/headers"

reset_stub
vm shutdown kid-one --operation-id shutdown-123 --json >"${tmp}/shutdown.json" 2>"${tmp}/shutdown.err"
ok 'shutdown follows the SSE endpoint to terminal success' jq -e '.state=="succeeded" and .result.outcome=="completed"' "${tmp}/shutdown.json"
ok 'human progress goes to stderr with phase context' grep -q '\[12:00:01\] start: working' "${tmp}/shutdown.err"
ok 'normal JSON stdout contains no progress lines' test "$(wc -l <"${tmp}/shutdown.json")" = 1

reset_stub
VM_SCENARIO=job-failed vm shutdown kid-one --operation-id failed-job-1 --json >"${tmp}/failed-job.json" 2>"${tmp}/failed-job.err"
ok 'a terminal failed job maps to exit 6' test "$?" = 6
ok 'a failed job reports the safe job error' grep -q 'fixture failure' "${tmp}/failed-job.err"

reset_stub
vm wait job-create --json-progress >"${tmp}/progress.ndjson" 2>"${tmp}/progress.err"
ok 'NDJSON progress emits phase, progress and final state events' sh -c "test \"\$(wc -l <'${tmp}/progress.ndjson')\" = 3 && jq -e 'select(.event==\"state\")|.job.state==\"succeeded\"' '${tmp}/progress.ndjson' >/dev/null"
ok 'NDJSON mode keeps stderr clean' test ! -s "${tmp}/progress.err"

reset_stub
vm shutdown kid-one --no-wait --json >"${tmp}/nowait.json" 2>/dev/null
ok '--no-wait returns the accepted job without opening SSE' jq -e '.jobId=="job-life"' "${tmp}/nowait.json"
ok '--no-wait made no events call' sh -c "! grep -q /events '${stub}/state/requests'"

reset_stub
vm save kid-one --operation-id save-key-1 --json >"${tmp}/save.json" 2>/dev/null
ok 'save uses the lifecycle route with no lifetime' grep -q $'POST\t/api/v1/vms/kid-one/lifecycle' "${stub}/state/requests"
ok 'save request is distinct from shutdown' grep -q '"action":"save"' "${stub}/state/bodies"

reset_stub
vm restart kid-one --no-wait --operation-id restart-key-1 --json >"${tmp}/restart.json" 2>/dev/null
ok 'restart is a job and preserves its own action' sh -c "jq -e '.jobId==\"job-life\"' '${tmp}/restart.json' >/dev/null && grep -q '\"action\":\"restart\"' '${stub}/state/bodies'"

reset_stub
vm renew kid-one --lifetime never --operation-id renew-key-1 --json >"${tmp}/renew.json" 2>/dev/null
ok 'renew posts an explicit lifetime to the lease route' sh -c "grep -q '^POST[[:space:]]/api/v1/vms/kid-one/lease' '${stub}/state/requests' && grep -q '\"lifetime\":\"never\"' '${stub}/state/bodies'"

# Sharing, hardware, media attachment and destructive confirmation.
reset_stub
vm share kid-one --scope host --operation-id share-key-1 --json >"${tmp}/share.json" 2>/dev/null
ok 'share sends host scope' grep -q '"scope":"host"' "${stub}/state/bodies"
ok 'share returns machine-readable scope' jq -e '.scope=="host"' "${tmp}/share.json"

reset_stub
vm hardware kid-one --cpus 4 --ram-mb 2048 --disk-gb 40 --secure-boot on --tpm off --boot-order installMedia,disk --json >"${tmp}/hardware.json" 2>/dev/null
ok 'hardware sends numeric sizes and structured firmware' grep -q '"cpus":4,"ramMb":2048,"diskGb":40,"firmware":{"secureBoot":true,"tpm":false,"bootOrder":\["installMedia","disk"\]}' "${stub}/state/bodies"

reset_stub
vm media attach kid-one --install media-install --boot-order installMedia,disk --json >"${tmp}/attach.json" 2>/dev/null
ok 'media attach sends the install id and boot order' grep -q '"installMediaId":"media-install".*"bootOrder":\["installMedia","disk"\]' "${stub}/state/bodies"
reset_stub
vm media detach kid-one --aux --json >"${tmp}/detach.json" 2>/dev/null
ok 'media detach uses an explicit null slot' grep -q '"auxiliaryMediaId":null' "${stub}/state/bodies"

reset_stub
vm media list --json >"${tmp}/media-list.json" 2>/dev/null
ok 'media list passes through the owned media array' jq -e '.[0].id=="media-install"' "${tmp}/media-list.json"

reset_stub
signed_url='https://example.test/standalone.iso?sig=sentinel-signed-query'
vm media acquire "${signed_url}" --role auxiliary --name answers.iso --operation-id acquire-key-1 --no-wait --json >"${tmp}/acquire.json" 2>/dev/null
ok 'standalone acquire sends URL, role and display name' grep -q '"url":"https://example.test/standalone.iso?sig=sentinel-signed-query","role":"auxiliary","name":"answers.iso"' "${stub}/state/bodies"
ok 'standalone acquire returns job and media ids' jq -e '.jobId=="job-media" and .mediaId=="media-install"' "${tmp}/acquire.json"
ok 'signed URL query is absent from curl argv' sh -c "! tr '\0' '\n' <'${stub}/state/argv' | grep -q sentinel-signed-query"

reset_stub
vm media delete media-install --yes --operation-id media-delete-1 --json >"${tmp}/media-delete.json" 2>/dev/null
ok 'media delete requires and then honors explicit confirmation' sh -c "grep -q '^DELETE[[:space:]]/api/v1/media/media-install' '${stub}/state/requests' && jq -e '.deleted==true' '${tmp}/media-delete.json' >/dev/null"

reset_stub
vm delete kid-one >"${tmp}/delete-no.out" 2>"${tmp}/delete-no.err"
ok 'noninteractive delete requires --yes' test "$?" = 1
ok 'delete refusal spells out all destructive artifacts' grep -q 'disk, its saved state and its dedicated media' "${tmp}/delete-no.err"
ok 'delete without confirmation sends no DELETE' sh -c "! grep -q $'DELETE\t' '${stub}/state/requests'"

reset_stub
vm delete parent-vm --yes >"${tmp}/self-delete.out" 2>"${tmp}/self-delete.err"
ok 'the primary credential cannot delete its own parent VM' test "$?" = 4

reset_stub
vm delete kid-one --yes --no-wait --operation-id delete-key-1 --json >"${tmp}/delete.json" 2>/dev/null
ok 'confirmed deletion reaches the child route' grep -q $'DELETE\t/api/v1/vms/kid-one' "${stub}/state/requests"
ok 'confirmed deletion carries its operation key' grep -q '^X-Construct-Operation-Key: delete-key-1$' "${stub}/state/headers"

# Create with existing, URL and uploaded media.
reset_stub
vm create --media media-install --aux-media media-aux --cpus 2 --ram-gb 2 --disk-gb 30 --lifetime 4h --name kid-one --preset linux --secure-boot off --tpm on --no-start --operation-id create-key-1 --json >"${tmp}/create.json" 2>"${tmp}/create.err"
ok 'create returns the documented composite JSON result' jq -e '.operationKey=="create-key-1" and (.media|length)==2 and .job.state=="succeeded" and .vm.name=="kid-one"' "${tmp}/create.json"
ok 'create converts GiB to MiB and sends all required resources' grep -q '"cpus":2,"ramMb":2048,"diskGb":30,"lifetime":"4h"' "${stub}/state/bodies"
ok 'create sends structured media, preset, firmware and no-start' grep -q '"media":{"installMediaId":"media-install","auxiliaryMediaId":"media-aux"}.*"start":false.*"preset":"linux".*"secureBoot":false.*"tpm":true' "${stub}/state/bodies"
ok 'create derives the child sub-key' grep -q '^X-Construct-Operation-Key: create-key-1:create$' "${stub}/state/headers"

reset_stub
vm create --iso-url https://example.test/os.iso --cpus 1 --ram-mb 512 --disk-gb 8 --lifetime 30m --name kid-one --operation-id url-create-1 --no-wait --json >"${tmp}/url-create.json" 2>/dev/null
ok 'URL create acquires media before submitting the child' sh -c "test \"\$(grep -n '^POST[[:space:]]/api/v1/media/acquire' '${stub}/state/requests' | cut -d: -f1)\" -lt \"\$(grep -n '^POST[[:space:]]/api/v1/vms/parent-vm/children' '${stub}/state/requests' | cut -d: -f1)\""
ok 'URL acquisition uses the install sub-key' grep -q '^X-Construct-Operation-Key: url-create-1:install$' "${stub}/state/headers"

tiny_iso="${tmp}/tiny.iso"
printf '12345678' >"${tiny_iso}"
reset_stub
vm media upload "${tiny_iso}" --name tiny.iso --operation-id upload-key-1 --json >"${tmp}/upload.json" 2>"${tmp}/upload.err"
ok 'upload sends every missing chunk' test "$(grep -c $'PUT\t/api/v1/media/uploads/upload-1/chunks/' "${stub}/state/requests")" = 2
ok 'upload completes only after the chunks' grep -q $'POST\t/api/v1/media/uploads/upload-1/complete' "${stub}/state/requests"
ok 'upload returns upload status plus ready media' jq -e '.upload.state=="open" and .media.id=="media-upload"' "${tmp}/upload.json"
ok 'upload reports byte progress on stderr' grep -q 'uploaded 8 bytes of 8 bytes' "${tmp}/upload.err"

reset_stub
vm create --iso "${tiny_iso}" --cpus 1 --ram-mb 512 --disk-gb 8 --lifetime 30m --name kid-one --operation-id local-create-1 --no-wait --json >"${tmp}/local-create.json" 2>/dev/null
ok 'create from a local ISO dedicates it to the child' grep -q '"dedicatedTo":"kid-one"' "${stub}/state/bodies"
ok 'local create uses install and create sub-keys' sh -c "grep -q '^X-Construct-Operation-Key: local-create-1:install$' '${stub}/state/headers' && grep -q '^X-Construct-Operation-Key: local-create-1:create$' '${stub}/state/headers'"

reset_stub
VM_SCENARIO=creation-disabled vm create --media media-install --cpus 1 --ram-mb 512 --disk-gb 8 --lifetime 30m --name kid-one >"${tmp}/disabled-create.out" 2>"${tmp}/disabled-create.err"
ok 'creation-disabled policy exits 4 before touching media' test "$?" = 4
ok 'creation-disabled policy explains the effective allowance' grep -q 'Child VM creation is disabled' "${tmp}/disabled-create.err"
ok 'creation-disabled policy makes no media or child call' sh -c "test \"\$(wc -l <'${stub}/state/requests')\" = 1"

# Console: screenshot bytes, stdin/file-only text and truthful mouse failure.
reset_stub
screenshot="${tmp}/screen.png"
vm console kid-one --screenshot "${screenshot}" --width 640 --height 480 >"${tmp}/shot.out" 2>/dev/null
ok 'screenshot preserves binary response bytes in the requested file' test "$(cat "${screenshot}")" = PNG-BYTES
ok 'screenshot passes bounded dimensions as query parameters' grep -q $'GET\t/api/v1/vms/kid-one/console/sessions/session-1/screenshot?width=640&height=480' "${stub}/state/requests"
ok 'console closes its short-lived session' grep -q $'DELETE\t/api/v1/vms/kid-one/console/sessions/session-1' "${stub}/state/requests"

typed="${tmp}/typed.txt"
printf 'sentinel typed secret' >"${typed}"
reset_stub
vm console kid-one --type-file "${typed}" >"${tmp}/typed.out" 2>/dev/null
ok 'console text is delivered in the JSON request body' grep -q 'sentinel typed secret' "${stub}/state/bodies"
ok 'console text never appears in curl argv' sh -c "! tr '\0' '\n' <'${stub}/state/argv' | grep -q 'sentinel typed secret'"

reset_stub
VM_SCENARIO=mouse-unavailable vm console kid-one --move 10,20 >"${tmp}/mouse.out" 2>"${tmp}/mouse.err"
ok 'an unavailable mouse is a conflict, not false success' test "$?" = 5
ok 'mouse failure prints the relative fallback hint' grep -q 'moveRelative' "${tmp}/mouse.err"

reset_stub
vm console kid-one --key 13 --press >"${tmp}/key.out" 2>/dev/null
ok 'console key press sends keyCode and press=true' grep -q '"kind":"key","keyCode":13,"press":true' "${stub}/state/bodies"

reset_stub
vm console kid-one --scancodes 0f,8f >"${tmp}/scancodes.out" 2>/dev/null
ok 'console scancodes become numeric bytes' grep -q '"kind":"scancodes","scancodes":\[15,143\]' "${stub}/state/bodies"

reset_stub
vm console kid-one --ctrl-alt-del >"${tmp}/cad.out" 2>/dev/null
ok 'console Ctrl+Alt+Del uses its distinct keyboard kind' grep -q '"kind":"ctrlAltDel"' "${stub}/state/bodies"

reset_stub
vm console kid-one --click 1 >"${tmp}/click.out" 2>/dev/null
ok 'console click uses one-based button input' grep -q '"kind":"click","button":1' "${stub}/state/bodies"

# Child forward and representative HTTP exit mappings.
reset_stub
vm forward kid-one 8080 --to host --connect-port 80 --operation-id forward-key-1 >"${tmp}/forward.out" 2>"${tmp}/forward.err"
ok 'host forward prints the service link' test "$(cat "${tmp}/forward.out")" = 'http://construct.example:28080/'
ok 'child forward identifies the requester primary and connect port' grep -q '"via":"parent-vm","connectPort":80' "${stub}/state/bodies"
ok 'an unverified child destination is disclosed' grep -q 'unverified' "${tmp}/forward.err"

reset_stub
vm forward kid-one 8080 --to client --wait 2 --operation-id client-forward-1 --json >"${tmp}/client-forward.json" 2>"${tmp}/client-forward.err"
ok 'client forward polls via the requester primary' grep -q $'GET\t/api/v1/vms/kid-one/forwards?via=parent-vm' "${stub}/state/requests"
ok 'client forward returns the acknowledged local port' jq -e '.localPort==18080' "${tmp}/client-forward.json"

reset_stub
VM_SCENARIO=forward-ipv6 vm forward kid-one 8080 --to client --wait 2 --operation-id ipv6-forward-1 >"${tmp}/ipv6-forward.out" 2>/dev/null
ok 'client forward brackets a bare IPv6 host label exactly once' test "$(cat "${tmp}/ipv6-forward.out")" = 'http://[2001:db8::8]:18080/'

reset_stub
VM_SCENARIO=forward-timeout vm forward kid-one 8080 --to client --wait 0 --operation-id timeout-forward-1 >"${tmp}/forward-timeout.out" 2>"${tmp}/forward-timeout.err"
ok 'a client forward that is not opened in time exits 7' test "$?" = 7

reset_stub
vm addresses kid-one --json >"${tmp}/addresses.json" 2>/dev/null
ok 'addresses tolerates the normal no-IP-yet response' jq -e '(.addresses|length)==0' "${tmp}/addresses.json"

reset_stub
vm jobs --json >"${tmp}/jobs.json" 2>/dev/null
ok 'jobs lists visible operations' jq -e '.[0].id=="job-create" and .[0].phase=="start"' "${tmp}/jobs.json"

reset_stub
vm cancel job-create --operation-id cancel-key-1 --json >"${tmp}/cancel.json" 2>/dev/null
ok 'cancel posts to the job cancel route' sh -c "grep -q '^POST[[:space:]]/api/v1/jobs/job-create/cancel' '${stub}/state/requests' && jq -e '.cancelled==true' '${tmp}/cancel.json' >/dev/null"

error_case() {
  local status="$1" code="$2" expected="$3" label="$4"
  reset_stub
  VM_ERROR_STATUS="${status}" VM_ERROR_CODE="${code}" vm addresses missing-vm --json >"${tmp}/error.out" 2>"${tmp}/error.err"
  ok "${label}" test "$?" = "${expected}"
  ok "${label} prints the problem code" grep -q "(${code})" "${tmp}/error.err"
}
error_case 400 validation 2 '400 maps to invalid request (2)'
error_case 404 not-found 3 '404 maps to not found (3)'
error_case 403 not-owner 4 '403 maps to refused (4)'
error_case 409 unsupported-capability 5 '409 maps to conflict (5)'
error_case 429 rate-limited 11 '429 maps to rate limited (11)'
error_case 503 maintenance 10 '503 maps to maintenance (10)'
error_case 500 server-error 8 '500 maps to service error (8)'

reset_stub
VM_SCENARIO=invalid-json vm addresses missing-vm --json >"${tmp}/invalid-json.out" 2>"${tmp}/invalid-json.err"
ok 'a non-JSON 2xx response maps to service error (8)' test "$?" = 8
ok 'a non-JSON 2xx response is never treated as an empty result' grep -q 'unusable JSON shape' "${tmp}/invalid-json.err"

# Human stdout and successful status must be tested together: bash syntax and
# Static shell checking cannot detect errors inside an embedded jq program.
human_case() {
  local label="$1" expected="$2"; shift 2
  reset_stub
  vm "$@" >"${tmp}/human.out" 2>"${tmp}/human.err"
  local rc=$?
  ok "${label}: exit 0" test "${rc}" = 0
  ok "${label}: human stdout" grep -q -- "${expected}" "${tmp}/human.out"
}
human_case 'identity uses vmName' '^VM: parent-vm$' identity
human_case 'plain lifecycle object' '^state: running$' start kid-one --lifetime 2h
human_case 'completed shutdown job' '^outcome: completed$' shutdown kid-one
human_case 'media item' '^name: installer.iso$' media acquire https://example.test/os.iso
human_case 'composite create' '^name: kid-one$' create --media media-install --cpus 1 --ram-mb 512 --disk-gb 8 --lifetime 30m --name kid-one
human_case 'inspect record' '^name: kid-one$' inspect kid-one
human_case 'console keyboard result' '^accepted: true$' console kid-one --key 13

for outcome in timeout unavailable; do
  VM_SCENARIO="shutdown-${outcome}" human_case "shutdown ${outcome}" "^outcome: ${outcome}$" shutdown kid-one
  ok "shutdown ${outcome}: final state stays visible" grep -q '^final state: running$' "${tmp}/human.out"
done
VM_SCENARIO=replay human_case 'start replay' '^state: running$' start kid-one --lifetime 2h --operation-id replay-start-1
ok 'synchronous replay is disclosed on stderr' grep -qx replayed "${tmp}/human.err"
VM_SCENARIO=replay human_case 'delete replay follows its job' '^state: succeeded$' delete kid-one --yes --operation-id replay-delete-1
ok 'job replay is disclosed on stderr' grep -qx replayed "${tmp}/human.err"

reset_stub
VM_SCENARIO=inventory vm list >"${tmp}/inventory.out" 2>/dev/null
ok 'inventory human output succeeds' test "$?" = 0
ok 'inventory shows an overdue lease and currentOperation.jobId' grep -q 'overdue.*job-active' "${tmp}/inventory.out"
ok 'inventory uses cpu and ramGb fallback without printing null' grep -Eq 'kid-one.*[[:space:]]2[[:space:]]+1024[[:space:]]+20' "${tmp}/inventory.out"

for action in click press release; do
  reset_stub
  VM_SCENARIO=mouse-unavailable vm console kid-one "--${action}" 1 >"${tmp}/button.out" 2>"${tmp}/button.err"
  ok "mouse ${action} unavailable exits 5" test "$?" = 5
  ok "mouse ${action} explains the fallback" grep -q 'try moveRelative' "${tmp}/button.err"
done

for scenario in media-pending aux-pending; do
  reset_stub
  VM_SCENARIO="${scenario}" vm create --media media-install --aux-media media-aux --cpus 1 --ram-mb 512 --disk-gb 8 --lifetime 30m --name kid-one >"${tmp}/pending.out" 2>"${tmp}/pending.err"
  ok "${scenario} refuses locally with exit 5" test "$?" = 5
  ok "${scenario} never submits the create" sh -c "! grep -q /children '${stub}/state/requests'"
  ok "${scenario} names the not-ready state" grep -q 'not ready' "${tmp}/pending.err"
done

reset_stub
long_key="$(printf '%0121d' 0)"
vm create --media media-install --cpus 1 --ram-mb 512 --disk-gb 8 --lifetime 30m --operation-id "${long_key}" >"${tmp}/long-key.out" 2>"${tmp}/long-key.err"
ok 'create reserves room for sub-keys before touching media' test "$?" = 1
ok 'an oversized composite key makes only the identity request' test "$(wc -l <"${stub}/state/requests")" = 1

reset_stub
VM_SCENARIO=media-in-use vm media delete media-install --yes >"${tmp}/in-use.out" 2>"${tmp}/in-use.err"
ok 'media-in-use exits 5' test "$?" = 5
ok 'media-in-use lists its VM reference' grep -q 'reference:.*kid-one' "${tmp}/in-use.err"

reset_stub
VM_SCENARIO=nonterminal-state vm wait job-life --json-progress >"${tmp}/nonterminal.out" 2>/dev/null
ok 'a nonterminal SSE state does not end the wait' test "$?" = 0
ok 'only the terminal SSE state becomes an NDJSON state event' test "$(jq -s '[.[]|select(.event=="state")]|length' "${tmp}/nonterminal.out")" = 1
ok 'SSE disables curl output buffering' sh -c "tr '\0' '\n' <'${stub}/state/argv' | grep -qx -- --no-buffer"

reset_stub
VM_SCENARIO=stream-forbidden vm wait job-life >"${tmp}/stream.out" 2>"${tmp}/stream.err"
ok 'SSE authorization refusal maps to exit 4' test "$?" = 4
ok 'SSE refusal preserves problem detail and code' grep -q 'Job access revoked (not-owner)' "${tmp}/stream.err"
reset_stub
vm wait job-life --timeout 0 >"${tmp}/timeout.out" 2>"${tmp}/timeout.err"
ok 'zero wait timeout returns exit 7 immediately' test "$?" = 7

reset_stub
VM_SCENARIO=keyboard-transport vm console kid-one --key 13 >"${tmp}/keyboard-transport.out" 2>"${tmp}/keyboard-transport.err"
ok 'uncertain console input reports service failure' test "$?" = 8
ok 'non-idempotent keyboard input is not automatically duplicated' test "$(grep -c '/keyboard' "${stub}/state/requests")" = 1

reset_stub
vm console kid-one --screenshot "${tmp}/accept.png" >/dev/null 2>/dev/null
ok 'screenshot asks for image/png' sh -c "tr '\0' '\n' <'${stub}/state/argv' | grep -qx 'Accept: image/png'"

reset_stub
vm media upload "${tiny_iso}" --operation-id chunk-path-1 >/dev/null 2>/dev/null
ok 'upload staging uses only one reusable chunk path' test "$(sort -u "${stub}/state/chunk-paths" | wc -l)" = 1
ok 'upload staging is removed after completion' test ! -e "$(head -n 1 "${stub}/state/chunk-paths")"
reset_stub
VM_SCENARIO=upload-resume vm media upload "${tiny_iso}" --operation-id resume-upload-1 >/dev/null 2>/dev/null
ok 'resumed upload sends only missing chunk 1' test "$(grep -c /chunks/ "${stub}/state/requests")" = 1
ok 'resumed upload skips the already received chunk' grep -q '/chunks/1' "${stub}/state/requests"

cat >"${stub}/bin/dd" <<'DDSTUB'
#!/usr/bin/env bash
if [[ "${VM_SCENARIO:-}" == dd-failed ]]; then exit 1; fi
if [[ "${VM_SCENARIO:-}" == dd-short ]]; then
  for arg in "$@"; do case "${arg}" in of=*) printf x >"${arg#of=}";; esac; done
  exit 0
fi
exec /usr/bin/dd "$@"
DDSTUB
chmod 0755 "${stub}/bin/dd"
for scenario in dd-failed dd-short; do
  reset_stub
  VM_SCENARIO="${scenario}" vm media upload "${tiny_iso}" --operation-id staging-fail-1 >"${tmp}/dd.out" 2>"${tmp}/dd.err"
  ok "${scenario} exits as a local error" test "$?" = 1
  ok "${scenario} sends no truncated chunk" sh -c "! grep -q /chunks/ '${stub}/state/requests'"
done

# Dispatcher/help and static hygiene.
ok 'construct vm --help is available without service configuration' sh -c "CONFIG_FILE='${tmp}/empty.env' bash '${CLI}' vm --help | grep -q 'media upload'"
ok 'per-command help works without service configuration' sh -c "CONFIG_FILE='${tmp}/empty.env' bash '${CLI}' vm create --help | grep -q 'construct vm <command>'"
nojq="${tmp}/nojq"
mkdir -p "${nojq}"
for needed in bash dirname hostname tr; do ln -s "$(command -v "${needed}")" "${nojq}/${needed}"; done
PATH="${nojq}" CONSTRUCT_SERVICE_URL=https://construct.example CONSTRUCT_INSTANCE_NAME=parent-vm \
  CONSTRUCT_SERVICE_CA_FILE="${ca_file}" CONSTRUCT_VM_TOKEN_FILE="${token_file}" \
  /bin/bash "${CLI}" vm list >"${tmp}/nojq.out" 2>"${tmp}/nojq.err"
ok 'missing jq is a local error (1)' test "$?" = 1
ok 'missing jq prints the documented install command' grep -q 'apt-get install -y jq' "${tmp}/nojq.err"
ok 'guest helper parses as bash' bash -n "${ROOT}/bin/construct-vm.sh"
if command -v shellcheck >/dev/null 2>&1; then ok 'guest helper is shellcheck-clean' shellcheck -x "${ROOT}/bin/construct-vm.sh"; fi

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
