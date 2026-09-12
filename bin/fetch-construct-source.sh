#!/usr/bin/env bash
# Streamed by Provision-AgentVM.ps1; never run from the tree being replaced.
set -euo pipefail
umask 077
fail() { printf 'source-fetch: %s\n' "$2" >&2; exit "$1"; }
commit="${CONSTRUCT_SOURCE_COMMIT:-}"
hash="${CONSTRUCT_SOURCE_SHA256:-}"
size="${CONSTRUCT_SOURCE_SIZE:-}"
max="${CONSTRUCT_SOURCE_MAX_BYTES:-268435456}"
limit="${CONSTRUCT_SOURCE_TIMEOUT_SEC:-600}"
seed="${CONSTRUCT_SEED_USER:-}"
base="${CONSTRUCT_SERVICE_URL:-}"
name="${CONSTRUCT_INSTANCE_NAME:-}"
token_file="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}"
repo="${REPO_DIR:-/opt/construct/repo}"
curl_bin="${CONSTRUCT_CURL:-curl}"
overlay="${CONSTRUCT_SOURCE_OVERLAY:-}"
work=''; old=''; displaced=false
cleanup() {
  local code=$?
  trap - EXIT
  if $displaced && [[ ! -e "$repo" && -d "$old" ]]; then
    if ! mv -- "$old" "$repo"; then code=7; printf 'source-fetch: repo-lost\n' >&2; fi
  fi
  if [[ -n "$work" && -d "$work" ]]; then rm -r -- "$work" 2>/dev/null || true; fi
  if [[ -n "$overlay" && "$overlay" == /tmp/* ]]; then
    case "$(realpath -m -- "$overlay")" in
      /tmp/*) if [[ -f "$overlay" || -L "$overlay" ]]; then rm -r -- "$overlay" 2>/dev/null || true; fi ;;
    esac
  fi
  exit "$code"
}
trap cleanup EXIT
[[ "$commit" =~ ^[0-9a-f]{40}$ && "$hash" =~ ^[0-9a-f]{64}$ ]] || fail 2 invalid-identity
[[ "$size" =~ ^[1-9][0-9]{0,9}$ && "$max" =~ ^[1-9][0-9]{0,9}$ && "$limit" =~ ^[1-9][0-9]{0,5}$ ]] || fail 2 invalid-size
((size <= max)) || fail 2 source-too-large
[[ "$seed" =~ ^[a-z_][a-z0-9_-]*$ && "$name" =~ ^[A-Za-z0-9][A-Za-z0-9_-]*$ ]] || fail 2 invalid-name
id "$seed" >/dev/null 2>&1 || fail 2 unknown-seed-user
[[ -r "$token_file" ]] || fail 2 missing-token
for tool in python3 sha256sum "$curl_bin"; do command -v "$tool" >/dev/null 2>&1 || fail 2 missing-tool; done
python3 - "$base" "$repo" <<'PY' || exit 2
import sys, urllib.parse, os
u=urllib.parse.urlsplit(sys.argv[1])
if u.scheme not in ('http','https') or not u.hostname or u.username or u.password or u.query or u.fragment or u.path not in ('','/'):
    print('source-fetch: invalid-service-url',file=sys.stderr);sys.exit(1)
if not os.path.isabs(sys.argv[2]) or os.path.normpath(sys.argv[2])=='/':
    print('source-fetch: invalid-repo-dir',file=sys.stderr);sys.exit(1)
PY
repo="$(realpath -m -- "$repo")"
parent="$(dirname -- "$repo")"
mkdir -p -- "$parent" || fail 6 parent-create-failed
# Remove leftovers only when a current tree exists; never discard a recovery copy.
if [[ -d "$repo" ]]; then
  for stale in "$parent"/.repo-old.*; do
    [[ -d "$stale" && ! -L "$stale" ]] || continue
    rm -r -- "$stale" 2>/dev/null || printf 'warning: previous repo left at %s\n' "$stale" >&2
  done
fi
work="$(mktemp -d "$parent/.repo-fetch.XXXXXX")" || fail 6 staging-failed
token="$(cat -- "$token_file")" || fail 2 missing-token
[[ -n "$token" && "$token" != *$'\n'* && "$token" != *$'\r'* ]] || fail 2 invalid-token
printf 'Authorization: VmToken %s\n' "$token" > "$work/headers"
unset token
ca=()
if [[ -n "${CONSTRUCT_SERVICE_CA_B64:-}" ]]; then
  printf '%s' "$CONSTRUCT_SERVICE_CA_B64" | base64 -d > "$work/ca.pem" || fail 2 invalid-ca
  ca=(--cacert "$work/ca.pem")
elif [[ -n "${CONSTRUCT_SERVICE_CA_FILE:-}" ]]; then
  [[ -r "$CONSTRUCT_SERVICE_CA_FILE" ]] || fail 2 missing-ca
  ca=(--cacert "$CONSTRUCT_SERVICE_CA_FILE")
elif [[ -f /etc/construct/service-ca.pem ]]; then ca=(--cacert /etc/construct/service-ca.pem); fi
status=000; rc=0
for attempt in 1 2; do
  rc=0
  status="$("$curl_bin" --silent --show-error --fail-with-body --max-time "$limit" --max-filesize "$size" \
    -H "@$work/headers" -H 'Accept: application/zip' "${ca[@]}" -o "$work/source.zip" -w '%{http_code}' \
    "${base%/}/api/v1/vms/$name/source/$commit" 2> "$work/curl-error")" || rc=$?
  [[ "$status" == 200 && "$rc" == 0 ]] && break
  if [[ "$attempt" == 1 && ( "$status" == 409 || "$status" == 000 || -z "$status" ) ]]; then sleep 3; continue; fi
  break
done
if [[ "$rc" != 0 || "$status" != 200 ]]; then
  [[ "$rc" == 63 ]] && fail 4 size-mismatch
  code="$(python3 - "$work/source.zip" <<'PY'
import json,re,sys
try:
    value=json.load(open(sys.argv[1])).get('code','')
    if isinstance(value,str) and re.fullmatch(r'[a-z0-9-]{1,80}',value): print(value)
except (OSError,ValueError,AttributeError): pass
PY
)"
  [[ "$status" =~ ^[0-9]{3}$ ]] || status=000
  fail 3 "http=$status code=$code"
fi
[[ "$(stat -c %s "$work/source.zip")" == "$size" ]] || fail 4 size-mismatch
[[ "$(sha256sum "$work/source.zip" | cut -d ' ' -f 1)" == "$hash" ]] || fail 4 hash-mismatch
if [[ -n "$overlay" ]]; then
  overlay_size="${CONSTRUCT_SOURCE_OVERLAY_SIZE:-}"
  overlay_hash="${CONSTRUCT_SOURCE_OVERLAY_SHA256:-}"
  [[ "$overlay_size" =~ ^[1-9][0-9]{0,9}$ && -f "$overlay" && ! -L "$overlay" ]] || fail 4 size-mismatch
  [[ "$(stat -c %s -- "$overlay")" == "$overlay_size" ]] || fail 4 size-mismatch
  [[ "$overlay_hash" =~ ^[0-9a-f]{64}$ && "$(sha256sum -- "$overlay" | cut -d ' ' -f 1)" == "$overlay_hash" ]] || fail 4 hash-mismatch
fi
python3 - "$work/source.zip" "$work/repo" "$commit" "$overlay" "$work/overlay-counts" <<'PY' || exit 5
import os,sys,zipfile,re,stat
archive,dest,commit,overlay,counts=sys.argv[1:]
refusal="extraction-refused"
def validate_name(name):
    parts=name.split('/')
    if not name or len(name)>240 or '\\' in name or any(ord(c)<32 or c in ':*?"<>|' for c in name): raise ValueError()
    if any(not p or p in ('.','..') or p.endswith(('.',' ')) or re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)',p,re.I) for p in parts): raise ValueError()
    return parts
def validate(z,archive,is_overlay=False):
    entries=z.infolist(); names={}; root=None; total=0
    if not 0<len(entries)<=20000: raise ValueError()
    for e in entries:
        directory=e.filename.endswith('/'); name=e.filename[:-1] if directory else e.filename
        parts=validate_name(name); kind=(e.external_attr>>16)&0xf000
        if kind not in (0,stat.S_IFDIR if directory else stat.S_IFREG) or e.external_attr&0x400: raise ValueError()
        if name.lower() in names: raise ValueError()
        names[name.lower()]=directory
        root=root or parts[0]
        if is_overlay:
            if name == 'construct-overlay.deleted':
                if directory: raise ValueError()
            elif parts[0] != 'construct-overlay' or len(parts)==1 and not directory: raise ValueError()
        elif parts[0]!=root or not re.fullmatch(r'[A-Za-z0-9_.-]+-main',root) or len(parts)==1 and not directory: raise ValueError()
        total+=e.file_size
        if total>min(os.path.getsize(archive)*16,1<<30): raise ValueError()
    for name in names:
        p=name
        while '/' in p:
            p=p.rsplit('/',1)[0]
            if p in names and not names[p]: raise ValueError()
    return entries,root
try:
    with zipfile.ZipFile(archive) as z:
        entries,root=validate(z,archive)
        os.mkdir(dest,0o755)
        for e in entries:
            relative=e.filename[len(root)+1:]
            if not relative: continue
            target=os.path.normpath(os.path.join(dest,relative))
            if os.path.commonpath([dest,target])!=dest: raise ValueError()
            if e.is_dir(): os.makedirs(target,mode=0o755,exist_ok=True);os.chmod(target,0o755);continue
            os.makedirs(os.path.dirname(target),mode=0o755,exist_ok=True)
            with z.open(e) as inp,open(target,'xb') as out:
                import shutil
                shutil.copyfileobj(inp,out)
            bits=(e.external_attr>>16)&0o777
            os.chmod(target,bits or 0o644)
        # All parents created under umask 077 must also have the source directory mode.
        for parent,dirs,files in os.walk(dest): os.chmod(parent,0o755)
        if overlay:
            refusal="overlay-refused"
            with zipfile.ZipFile(overlay) as patch:
                overlay_entries,_=validate(patch,overlay,True)
                deletions=[]; written=0; removed=0
                if 'construct-overlay.deleted' in patch.namelist():
                    text=patch.read('construct-overlay.deleted').decode('utf-8')
                    if '\r' in text or text and not text.endswith('\n'): raise ValueError()
                    deletions=text.split('\n')[:-1]
                    for relative in deletions:
                        validate_name('construct-overlay/'+relative)
                for e in overlay_entries:
                    if e.filename == 'construct-overlay.deleted' or e.is_dir(): continue
                    relative=e.filename[len('construct-overlay/'):]
                    target=os.path.normpath(os.path.join(dest,relative))
                    if os.path.commonpath([dest,target])!=dest: raise ValueError()
                    mode=stat.S_IMODE(os.stat(target).st_mode) if os.path.isfile(target) else (0o755 if relative.endswith('.sh') or relative.startswith('bin/') else 0o644)
                    os.makedirs(os.path.dirname(target),mode=0o755,exist_ok=True)
                    with patch.open(e) as inp,open(target,'wb') as out:
                        import shutil
                        shutil.copyfileobj(inp,out)
                    os.chmod(target,mode); written+=1
                for relative in deletions:
                    target=os.path.normpath(os.path.join(dest,relative))
                    if os.path.commonpath([dest,target])!=dest or target==dest: raise ValueError()
                    if not os.path.lexists(target): continue
                    if not stat.S_ISREG(os.lstat(target).st_mode): raise ValueError()
                    os.unlink(target); removed+=1
                    parent=os.path.dirname(target)
                    while parent!=dest:
                        if os.listdir(parent): break
                        os.rmdir(parent); parent=os.path.dirname(parent)
                for parent,dirs,files in os.walk(dest): os.chmod(parent,0o755)
                with open(counts,'w') as out:
                    out.write(f'CONSTRUCT_SOURCE_OVERLAY_FILES={written}\nCONSTRUCT_SOURCE_OVERLAY_DELETED={removed}\n')
            refusal="extraction-refused"
        with open(os.path.join(dest,'.construct-revision'),'w') as marker: marker.write(commit+'\n')
        os.chmod(os.path.join(dest,'.construct-revision'),0o644)
except (OSError,ValueError,zipfile.BadZipFile,RuntimeError):
    print('source-fetch: '+refusal,file=sys.stderr);sys.exit(1)
PY
chown -R "$seed:$seed" "$work/repo" || fail 6 tree-chown-failed
chown "$seed:$seed" "$parent" || fail 6 parent-chown-failed
old="$parent/.repo-old.$$"
if [[ -e "$repo" ]]; then
  mv -- "$repo" "$old" || fail 6 old-rename-failed
  displaced=true
fi
if ! mv -- "$work/repo" "$repo"; then
  if $displaced; then
    if ! mv -- "$old" "$repo"; then displaced=false; fail 7 repo-lost; fi
    displaced=false
  fi
  fail 6 new-rename-failed
fi
displaced=false
if [[ -d "$old" ]]; then rm -r -- "$old" 2>/dev/null || printf 'warning: previous repo left at %s\n' "$old" >&2; fi
printf 'CONSTRUCT_SOURCE_INSTALLED=%s\nCONSTRUCT_SOURCE_BYTES=%s\n' "$commit" "$size"
if [[ -n "$overlay" ]]; then cat -- "$work/overlay-counts"; fi
