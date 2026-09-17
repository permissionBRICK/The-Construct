#!/usr/bin/env bash
# Independent systemd updater. Source with --library-only to replace service/health functions in tests.
set -euo pipefail

# JSON, filesystem validation, and pinned TLS live in Python's standard library. No credential is
# passed in argv or emitted in diagnostics. Each operation reloads the durable recovery state.
update_python() {
  python3 - "$@" <<'PY'
import datetime, hashlib, http.client, json, os, pathlib, re, shutil, ssl, stat, subprocess, sys, tempfile, time, urllib.parse, zipfile

class UpdateError(Exception):
    pass

def fail(code='updater-step-failed'):
    raise UpdateError(code)

def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()

def assert_update_no_links(path):
    p = pathlib.Path(os.path.abspath(path))
    for ancestor in (p, *p.parents):
        if ancestor.is_symlink(): fail()

def read_json(path):
    assert_update_no_links(path)
    return json.loads(pathlib.Path(path).read_text()) if pathlib.Path(path).exists() else None

def write_json(path, value):
    path = pathlib.Path(path)
    assert_update_no_links(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp = tempfile.mkstemp(prefix=path.name+'.', suffix='.tmp', dir=path.parent)
    try:
        with os.fdopen(fd, 'w') as stream:
            json.dump(value, stream, separators=(',', ':'))
            stream.write('\n'); stream.flush(); os.fsync(stream.fileno())
        os.replace(temp, path)
    finally:
        if os.path.exists(temp): os.unlink(temp)

def sha(path):
    assert_update_no_links(path)
    with open(path, 'rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def test_update_path(path):
    return (isinstance(path, str) and 0 < len(path) <= 240 and
            not re.search(r'[\\:*?"<>|\x00-\x1f]', path) and
            all(p and p not in ('.', '..') and not p.endswith(('.', ' ')) and
                not re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)', p, re.I) for p in path.split('/')))

def preserved(path):
    return any(p.lower() in ('appsettings.production.json', 'install.json', 'settings.json', 'projects', 'keys', '.git', '.construct-tools', 'data', 'media', 'iso') or '.db' in p.lower() for p in path.split('/'))

def payload(path):
    return test_update_path(path) and (path.startswith(('service/', 'scripts/')) or path == 'updater/update-construct-host.sh')

def under(path, root):
    return bool(root) and pathlib.Path(os.path.abspath(path)).is_relative_to(os.path.abspath(root))

def setting(*parts):
    value = settings
    for part in parts:
        value = next((v for k, v in value.items() if k.lower() == part.lower()), None) if isinstance(value, dict) else None
    return value

def get_update_target(path):
    if not test_update_path(path) or preserved(path) or not path.startswith(('service/', 'scripts/')): fail()
    base = h['publishDir'] if path.startswith('service/') else h['scriptsDir']
    dest = pathlib.Path(base, path[8:])
    protected = [h['dataDir'], setting('Constructd', 'Iso', 'CacheDir'), setting('Constructd', 'HostAdmin', 'Media', 'RootDir'),
                 setting('Constructd', 'HostAdmin', 'Source', 'RootDir'), *(str(pathlib.Path(h['scriptsDir'], p)) for p in ('keys', 'projects', '.construct-tools'))]
    if not under(dest, base) or any(under(dest, p) for p in protected): fail()
    if path.startswith('scripts/') and under(dest, h['publishDir']): fail()
    assert_update_no_links(dest)
    return dest

def get_update_authority():
    record = read_json(record_path)
    if record and record.get('updateId') == h['updateId'] and record.get('outcome') in ('succeeded', 'rolledBack', 'rolledBackWithDatabase'):
        return 'already-terminal'
    fence = read_json(fence_path)
    if fence and fence.get('updateId') == h['updateId']:
        choices = {'closed': 'superseded', 'commitOnly': 'commitOnly', 'rollbackAuthorized': 'rollback'}
        if fence.get('disposition') not in choices: fail()
        return choices[fence['disposition']]
    return 'apply'

def set_phase(phase):
    authority = get_update_authority()
    if authority in ('superseded', 'already-terminal'): fail(authority)
    if authority == 'commitOnly' and phase != 'commit': fail()
    if authority == 'rollback' and phase not in ('stop', 'backup', 'rollback'): fail('admin-rollback')
    r['phase'] = phase; r['phaseAt'] = now()
    write_json(record_path, r)
    log = root/'updater.log'; assert_update_no_links(log)
    with log.open('a') as stream: stream.write(r['phaseAt']+' phase='+phase+'\n')

def test_update_manifest():
    if (not m or not v or m.get('schemaVersion') != 1 or m.get('commit') != h['commit'] or v.get('commit') != h['commit'] or
        v.get('updateId') != h['updateId'] or v.get('source') != 'linux' or m.get('ref') != 'refs/heads/main' or
        m.get('releaseTag') != 'host-'+h['commit'] or m.get('linuxAsset') != 'construct-host-'+h['commit'][:7]+'-linux-x64.zip' or
        m.get('linuxUpdaterPath') != 'updater/update-construct-host.sh'): fail()
    for key in ('linuxSha256', 'linuxSumsSha256', 'linuxUpdaterSha256'):
        if not re.fullmatch('[0-9a-f]{64}', m.get(key, '')): fail()
    for key in ('linuxSizeBytes', 'linuxUncompressedSizeBytes'):
        if type(m.get(key)) is not int or not 0 < m[key] <= 1073741824: fail()
    package = stage/'package.zip'
    if package.stat().st_size != m['linuxSizeBytes'] or sha(package) != m['linuxSha256']: fail()
    extracted = stage/'extracted'; assert_update_no_links(extracted)
    hashes = {}; names = set(); total = 0
    with zipfile.ZipFile(package) as archive:
        if len(archive.infolist()) > 20000: fail()
        for entry in archive.infolist():
            name = entry.filename
            if (not test_update_path(name) or (name != 'SHA256SUMS' and not payload(name)) or name.lower() in names or
                entry.external_attr & 0x400 or stat.S_IFMT(entry.external_attr >> 16) not in (0, stat.S_IFREG) or
                entry.file_size > 268435456): fail()
            names.add(name.lower()); total += entry.file_size
            if total > m['linuxUncompressedSizeBytes']: fail()
            file = extracted/name; assert_update_no_links(file)
            if not file.is_file() or file.stat().st_size != entry.file_size: fail()
            digest = hashlib.sha256(); length = 0
            with archive.open(entry) as stream:
                while chunk := stream.read(81920):
                    length += len(chunk)
                    if length > entry.file_size: fail()
                    digest.update(chunk)
            if length != entry.file_size or digest.hexdigest() != sha(file): fail()
            hashes[name] = digest.hexdigest()
    if total != m['linuxUncompressedSizeBytes'] or hashes.get('SHA256SUMS') != m['linuxSumsSha256']: fail()
    listed = {}; folded = set()
    for line in (extracted/'SHA256SUMS').read_text().splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  (.+)', line)
        if not match: fail()
        digest, name = match.groups()
        if not payload(name) or name.lower() in folded or hashes.get(name) != digest: fail()
        listed[name] = digest; folded.add(name.lower())
    if set(hashes) != set(listed) | {'SHA256SUMS'}: fail()
    if len(v['files']) != len(listed) or {f['path']: f['sha256'] for f in v['files']} != listed: fail()
    actual = set()
    for file in extracted.rglob('*'):
        assert_update_no_links(file)
        if file.is_file(): actual.add(file.relative_to(extracted).as_posix())
    if actual != set(hashes) or listed.get(m['linuxUpdaterPath']) != m['linuxUpdaterSha256']: fail()
    if not settings: fail()
    for file in new_files: get_update_target(file['path'])

def copy_file(source, dest):
    assert_update_no_links(source); assert_update_no_links(dest)
    dest.parent.mkdir(parents=True, exist_ok=True)
    # GNU install preserves the selected source permission bits, never setuid/setgid bits.
    subprocess.run(['install', '-m', format(stat.S_IMODE(source.stat().st_mode) & 0o777, 'o'), '--', str(source), str(dest)],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

def assert_update_backup():
    marker = read_json(backup/'backup-complete.json')
    if not marker or marker.get('updateId') != h['updateId']: fail('backup-incomplete')
    files = read_json(backup/'files.json')
    if not isinstance(files, list) or not files: fail('backup-incomplete')
    names = set()
    for file in files:
        name = file['path']
        if not test_update_path(name) or name.lower() in names: fail('backup-hash-mismatch')
        names.add(name.lower())
        if not (name.startswith(('service/', 'scripts/')) or name in ('previous-install.json', 'constructd.db', 'constructd.db-wal', 'constructd.db-shm')): fail('backup-hash-mismatch')
        if sha(backup/name) != file['sha256']: fail('backup-hash-mismatch')
    return files

def make_backup():
    if (backup/'backup-complete.json').exists():
        assert_update_backup()
    else:
        if r['replaceStarted']: fail('backup-incomplete')
        assert_update_no_links(backup)
        if backup.exists(): shutil.rmtree(backup)
        backup.mkdir(mode=0o700)
        previous = read_json(ledger)
        # Linux installations write a ledger from day one. Refuse to claim unowned files.
        if not previous or not previous.get('files'): fail('backup-incomplete')
        for file in previous['files']:
            copy_file(get_update_target(file['path']), backup/file['path'])
        copy_file(ledger, backup/'previous-install.json')
        db = setting('Constructd', 'DatabasePath')
        if not db: fail()
        for suffix in ('', '-wal', '-shm'):
            source = pathlib.Path(db+suffix); assert_update_no_links(source)
            if source.exists(): copy_file(source, backup/('constructd.db'+suffix))
        files = [{'path': f.relative_to(backup).as_posix(), 'sha256': sha(f)} for f in sorted(backup.rglob('*')) if f.is_file()]
        write_json(backup/'files.json', files)
        write_json(backup/'backup-complete.json', {'updateId': h['updateId'], 'previousSchema': h['previousSchemaVersion']})
    r['backupComplete'] = True; write_json(record_path, r)

def apply_modes():
    # Refuse links before recursive ownership repair, including preserved subtrees.
    for base in (pathlib.Path(h['publishDir']), pathlib.Path(h['scriptsDir'])):
        for path in base.rglob('*'): assert_update_no_links(path)
        subprocess.run(['chown', '-R', 'root:root', '--', str(base)], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    pathlib.Path(h['publishDir'], 'Constructd.Api').chmod(0o755)
    key = pathlib.Path(h['scriptsDir'], 'keys/bootstrap_ed25519'); assert_update_no_links(key)
    if key.exists(): key.chmod(0o600)

def replace_files():
    files = assert_update_backup()
    set_phase('replace')
    r['replaceStarted'] = True; write_json(record_path, r)
    for file in new_files: copy_file(stage/'extracted'/file['path'], get_update_target(file['path']))
    remove_stale(files, new_files)
    apply_modes()

def remove_stale(old, new):
    keep = {f['path'].lower() for f in new}
    for file in old:
        if file['path'].startswith(('service/', 'scripts/')) and file['path'].lower() not in keep:
            get_update_target(file['path']).unlink(missing_ok=True)

def get_update_rollback_mode():
    db = m['database']
    return 'database' if db['minReadableBy'] > h['previousSchemaVersion'] or db['breakingMigrations'] else 'binary'

def rollback_files():
    files = assert_update_backup()
    for file in files:
        if file['path'].startswith(('service/', 'scripts/')): copy_file(backup/file['path'], get_update_target(file['path']))
    remove_stale(new_files, files)
    if get_update_rollback_mode() == 'database':
        if not (backup/'constructd.db').is_file(): fail('backup-incomplete')
        for suffix in ('', '-wal', '-shm'):
            dest = pathlib.Path(setting('Constructd', 'DatabasePath')+suffix); assert_update_no_links(dest)
            dest.unlink(missing_ok=True)
            source = backup/('constructd.db'+suffix)
            if source.exists(): copy_file(source, dest)
    for file in files:
        if file['path'].startswith(('service/', 'scripts/')) and sha(get_update_target(file['path'])) != file['sha256']: fail('backup-hash-mismatch')
    # The original ledger bytes are restored atomically, including unknown additive fields.
    temp = ledger.with_name('install.json.'+str(os.getpid())+'.tmp'); assert_update_no_links(temp)
    try:
        copy_file(backup/'previous-install.json', temp); os.replace(temp, ledger)
    finally: temp.unlink(missing_ok=True)
    apply_modes()

def test_update_health(commit, schema):
    uri = urllib.parse.urlsplit(h['healthUrl'])
    if (uri.scheme != 'https' or uri.hostname != '127.0.0.1' or uri.username or uri.password or
        uri.path != '/api/v1/health' or uri.query or uri.fragment or not re.fullmatch('[0-9A-Fa-f]{40}', h['certificateThumbprint'])):
        fail('invalid-health-endpoint')
    deadline = time.monotonic()+h['healthTimeoutSeconds']
    while True:
        authority = get_update_authority()
        if authority in ('superseded', 'already-terminal'): fail(authority)
        r['healthAttempts'] += 1; write_json(record_path, r)
        connection = None
        try:
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT); context.check_hostname = False; context.verify_mode = ssl.CERT_NONE
            connection = http.client.HTTPSConnection('127.0.0.1', uri.port or 443, timeout=5, context=context)
            connection.connect()
            if hashlib.sha1(connection.sock.getpeercert(binary_form=True)).hexdigest().lower() != h['certificateThumbprint'].lower(): raise ValueError()
            connection.request('GET', uri.path, headers={'Authorization': 'UpdateHandoff '+h['healthToken']})
            response = connection.getresponse()
            body = json.loads(response.read(1048576)) if response.status == 200 else {}
            if body.get('status') == 'maintenance' and body.get('commit') == commit and body.get('schemaVersion', -1) >= schema:
                result = subprocess.run([h['adminCliPath'], 'admin', 'db', 'check', '--json'], cwd=h['publishDir'],
                    env={**os.environ, 'DOTNET_ENVIRONMENT': 'Production'}, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=15)
                check = json.loads(result.stdout)
                if result.returncode == 0 and check.get('status') == 'ok' and check.get('schemaVersion') == body['schemaVersion']: return
        except Exception: pass  # No response or exception can expose the health credential.
        finally:
            if connection: connection.close()
        if time.monotonic() >= deadline: fail('health-failed')
        time.sleep(2)

def complete(outcome, error=None):
    authority = get_update_authority()
    if authority in ('superseded', 'already-terminal'): fail(authority)
    r['outcome'] = outcome; r['error'] = error; write_json(record_path, r)
    if outcome in ('applyFailed', 'rolledBack', 'rolledBackWithDatabase'):
        write_json(fence_path, {'updateId': h['updateId'], 'disposition': 'closed', 'actor': 'updater', 'at': now()})

def commit():
    set_phase('commit')
    for file in new_files:
        if sha(get_update_target(file['path'])) != file['sha256']: fail('installation-mixed')
    if (backup/'backup-complete.json').exists():
        for file in assert_update_backup():
            if file['path'].startswith(('service/', 'scripts/')) and file['path'].lower() not in {f['path'].lower() for f in new_files} and get_update_target(file['path']).exists(): fail('installation-mixed')
    write_json(ledger, {'source': 'release', 'commit': h['commit'], 'packageVersion': m['packageVersion'], 'installedAt': now(),
                        'previousCommit': h['previousCommit'], 'updateId': h['updateId'], 'files': new_files})
    complete('succeeded')
    # Cleanup cannot change the terminal result.
    backups = [p for p in root.glob('backup-*') if (p/'backup-complete.json').is_file()]
    for path in sorted(backups, key=lambda p:p.stat().st_mtime, reverse=True)[2:]:
        assert_update_no_links(path); shutil.rmtree(path)
    for path in root.iterdir():
        if re.fullmatch('[a-f0-9]{32}', path.name) and path.name != h['updateId']:
            assert_update_no_links(path)
            if path.is_dir(): shutil.rmtree(path)

try:
    op, handoff, *args = sys.argv[1:]
    assert_update_no_links(handoff)
    h = read_json(handoff)
    if not h or not re.fullmatch('[a-f0-9]{32}', h['updateId']) or not re.fullmatch('[a-f0-9]{40}', h['commit']): fail()
    for key in ('publishDir', 'scriptsDir', 'dataDir', 'stagedPath', 'adminCliPath'):
        path = h[key]
        if not os.path.isabs(path) or any(ord(c) < 32 or c in "\"'" for c in path): fail()
        assert_update_no_links(path)
    root = pathlib.Path(h['dataDir'], 'updates'); assert_update_no_links(root)
    stage = pathlib.Path(h['stagedPath']); backup = root/('backup-'+h['updateId'])
    if stage != root/h['updateId'] or pathlib.Path(os.path.abspath(handoff)) != root/'handoff.json' or h['serviceName'] != 'constructd': fail()
    record_path = root/'last-update.json'; fence_path = root/'fence.json'
    for path in (record_path, fence_path, backup, root/'updater.lock', pathlib.Path(h['dataDir'], 'admin.lock')): assert_update_no_links(path)
    r = read_json(record_path)
    if not r or r.get('updateId') != h['updateId']:
        r = dict(updateId=h['updateId'], commit=h['commit'], previousCommit=h['previousCommit'], phase='stop', phaseAt=now(), outcome=None,
                 error=None, backupPath=str(backup), backupComplete=False, replaceStarted=False, stagedPath=str(stage), healthAttempts=0, manualSteps=[])
    # Terminal and fence checks work even after a stage has been removed or corrupted.
    if op == 'context': print(str(root)+'\n'+h['dataDir']+'\n'+h['serviceName'])
    elif op == 'authority': print(get_update_authority())
    elif op == 'phase': set_phase(args[0])
    elif op == 'record': print(str(r[args[0]]).lower())
    elif op == 'complete': complete(args[0], args[1] if len(args)>1 else None)
    elif op == 'recovery-failed':
        r['manualSteps'] = ['Keep the service in maintenance. Inspect this record and backup/files.json.',
            'Repair from the complete backup, or repair the staged installation. Do not restore a database after commitOnly or a terminal outcome.',
            'Use the admin update resolve API to commit, abort or close after verifying the installed files.']
        complete('recoveryFailed', args[0])
    else:
        m = read_json(stage/'manifest.json'); v = read_json(stage/'verified.json')
        settings = read_json(pathlib.Path(h['publishDir'], 'appsettings.Production.json'))
        ledger = pathlib.Path(h['publishDir'], 'install.json')
        new_files = [f for f in v['files'] if f['path'].startswith(('service/', 'scripts/'))] if v else []
        if op == 'verify': test_update_manifest()
        elif op == 'backup': make_backup()
        elif op == 'replace': replace_files()
        elif op == 'assert-backup': assert_update_backup()
        elif op == 'rollback': rollback_files()
        elif op == 'rollback-mode': print(get_update_rollback_mode())
        elif op == 'commit': commit()
        elif op == 'health': test_update_health(h['previousCommit'] if args[0]=='old' else h['commit'], h['previousSchemaVersion'] if args[0]=='old' else m['database']['schemaVersion'])
        else: fail()
except UpdateError as error:
    print(str(error)); sys.exit(1)
except Exception:
    print('updater-step-failed'); sys.exit(1)
PY
}

get_update_failure_code() {
  case "$1" in
    update-health-failed|rollback-health-failed|old-service-health-failed|installation-mixed|backup-incomplete|backup-hash-mismatch|invalid-health-endpoint|admin-rollback) printf '%s\n' "$1" ;;
    *) printf '%s\n' updater-step-failed ;;
  esac
}
get_update_authority() { update_python authority "$UPDATE_HANDOFF"; }
test_update_manifest() { update_step verify; }
set_phase() { update_step phase "$1"; }
assert_update_backup() { update_step assert-backup; }
get_update_rollback_mode() { update_python rollback-mode "$UPDATE_HANDOFF"; }
test_update_health() { update_step health "$1"; }
update_step() {
  local result
  if result=$(update_python "$1" "$UPDATE_HANDOFF" "${@:2}"); then return 0; fi
  UPDATE_FAILURE="$result"
  return 1
}
stop_update_service() {
  systemctl stop "$1" >/dev/null 2>&1 || return 1
  local deadline=$((SECONDS+120)) state
  while (( SECONDS < deadline )); do
    state=$(systemctl is-active "$1" 2>/dev/null) || :
    [[ "$state" == inactive || "$state" == failed ]] && return 0
    sleep 1
  done
  return 1
}
start_update_service() {
  systemctl start "$1" >/dev/null 2>&1 || return 1
  local deadline=$((SECONDS+120))
  while (( SECONDS < deadline )); do
    systemctl is-active --quiet "$1" && return 0
    sleep 1
  done
  return 1
}

invoke_construct_host_update() (
  # A subshell releases both flock descriptors on every exit, including a failed shell command.
  local UPDATE_HANDOFF="$1" want_rollback="${3:-false}" UPDATE_FAILURE=updater-step-failed
  local context authority update_root update_data service stop_attempted=false verified=false
  context=$(update_python context "$UPDATE_HANDOFF") || return 1
  mapfile -t fields <<<"$context"
  update_root="${fields[0]}"; update_data="${fields[1]}"; service="${fields[2]}"
  umask 077
  exec {updater_fd}>"$update_root/updater.lock"
  flock -n "$updater_fd" || return 1
  authority=$(get_update_authority) || return 1
  case "$authority" in already-terminal) return 0 ;; superseded) return 4 ;; esac
  [[ "$want_rollback" != true || "$authority" == rollback ]] || return 1
  exec {admin_fd}>"$update_data/admin.lock"
  flock -n "$admin_fd" || return 1

  apply_update() {
    test_update_manifest || return 1
    verified=true
    if [[ "$authority" != commitOnly ]]; then
      set_phase stop || return 1
      stop_attempted=true
      stop_update_service "$service" || return 1
      set_phase backup && update_step backup || return 1
      authority=$(get_update_authority) || return 1
      if [[ "$authority" == rollback ]]; then UPDATE_FAILURE=admin-rollback; return 1; fi
      update_step replace || return 1
      set_phase start && start_update_service "$service" || return 1
      set_phase health || return 1
      if ! test_update_health new; then
        [[ "$UPDATE_FAILURE" == invalid-health-endpoint ]] || UPDATE_FAILURE=update-health-failed
        return 1
      fi
    fi
    update_step commit
  }
  if apply_update; then return 0; fi
  local failure
  failure=$(get_update_failure_code "$UPDATE_FAILURE")
  authority=$(get_update_authority) || return 1
  case "$authority" in already-terminal) return 0 ;; superseded) return 4 ;; esac
  recover_update() {
    if [[ $(update_python record "$UPDATE_HANDOFF" replaceStarted) == false && "$authority" != commitOnly ]]; then
      if [[ "$stop_attempted" == true ]]; then
        start_update_service "$service" || return 1
        if ! test_update_health old; then UPDATE_FAILURE=old-service-health-failed; return 1; fi
      fi
      update_step complete applyFailed "$failure" || return 1
      return 2
    fi
    [[ "$authority" != commitOnly && "$verified" == true && $(update_python record "$UPDATE_HANDOFF" backupComplete) == true ]] || return 1
    assert_update_backup && set_phase rollback && stop_update_service "$service" || return 1
    update_step rollback && start_update_service "$service" || return 1
    if ! test_update_health old; then UPDATE_FAILURE=rollback-health-failed; return 1; fi
    local outcome=rolledBack
    [[ $(get_update_rollback_mode) != database ]] || outcome=rolledBackWithDatabase
    update_step complete "$outcome" "$failure"
  }
  local recovered=0
  recover_update || recovered=$?
  case "$recovered" in 0) return 0 ;; 2) return 1 ;; esac
  authority=$(get_update_authority) || return 1
  case "$authority" in already-terminal) return 0 ;; superseded) return 4 ;; esac
  update_step recovery-failed "$(get_update_failure_code "$UPDATE_FAILURE")" || :
  return 1
)

if [[ "${1:-}" != --library-only ]]; then
  handoff=''; resume=false; rollback=false
  while (( $# )); do
    case "$1" in
      --handoff) [[ $# -ge 2 ]] || exit 1; handoff="$2"; shift 2 ;;
      --resume) resume=true; shift ;;
      --rollback) rollback=true; shift ;;
      *) echo 'Usage: update-construct-host.sh --handoff <path> [--resume] [--rollback]' >&2; exit 1 ;;
    esac
  done
  [[ -n "$handoff" ]] || { echo 'A handoff path is required.' >&2; exit 1; }
  result=0
  invoke_construct_host_update "$handoff" "$resume" "$rollback" || result=$?
  if [[ "$result" == 1 ]]; then echo 'Host update could not run; inspect the local recovery record.' >&2; fi
  exit "$result"
fi
