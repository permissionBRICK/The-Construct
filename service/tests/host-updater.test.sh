#!/usr/bin/env bash
# Temporary fixtures only. No real systemd unit, host service, database, or network is used.
set -euo pipefail
cd "$(dirname "$0")/../.."
source service/host/update-construct-host.sh --library-only
task_dir=$(mktemp -d)
trap 'rm -r "$task_dir"' EXIT
mkdir "$task_dir/bin"
cat >"$task_dir/bin/systemctl" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ -n "${UPDATER_TEST_CASE:-}" && -d "$UPDATER_TEST_CASE" ]] || exit 99
printf '%s\n' "$*" >>"$UPDATER_TEST_CASE/control.log"
case "$1" in
  stop) echo inactive >"$UPDATER_TEST_CASE/state" ;;
  start) echo active >"$UPDATER_TEST_CASE/state" ;;
  is-active)
    state=$(cat "$UPDATER_TEST_CASE/state")
    [[ "$2" == --quiet ]] || echo "$state"
    [[ "$state" == active ]] ;;
  *) exit 99 ;;
esac
SH
cat >"$task_dir/bin/chown" <<'SH'
#!/usr/bin/env bash
# Ownership is field-tested as root; fixtures can run under an ordinary Linux user.
[[ "$*" == *"$UPDATER_TEST_CASE"* ]] || exit 99
SH
cat >"$task_dir/bin/systemd-run" <<'SH'
#!/usr/bin/env bash
exit 99 # The shell updater must never launch itself.
SH
chmod +x "$task_dir/bin/"*
export PATH="$task_dir/bin:$PATH"

fixture() {
  export UPDATER_TEST_CASE="$task_dir/$1"
  python3 - "$UPDATER_TEST_CASE" <<'PY'
import datetime, hashlib, json, pathlib, sys, uuid, zipfile
base=pathlib.Path(sys.argv[1]); publish=base/'publish'; scripts=base/'scripts'; data=base/'data'
uid=uuid.uuid4().hex; stage=data/'updates'/uid; extracted=stage/'extracted'
for p in (publish,scripts/'keys',extracted): p.mkdir(parents=True)
def write(path,text): path.parent.mkdir(parents=True,exist_ok=True);path.write_text(text)
def dump(path,value): write(path,json.dumps(value))
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
write(base/'state','active')
write(publish/'Constructd.Api','old');(publish/'Constructd.Api').chmod(0o755)
write(publish/'removed.dll','old-owned');write(publish/'unowned.txt','unowned')
write(scripts/'keys/bootstrap_ed25519','preserved-key');write(data/'constructd.db','preserved-db')
write(data/'constructd.db-wal','preserved-wal')
settings={'Constructd':{'DatabasePath':str(data/'constructd.db'),'Iso':{'CacheDir':str(data/'iso')},'HostAdmin':{'Media':{'RootDir':str(data/'media')},'Source':{'RootDir':str(scripts/'custom-cache')}}}}
dump(publish/'appsettings.Production.json',settings);write(scripts/'custom-cache/cached.zip','preserved-cache')
old=[{'path':'service/'+name,'sha256':sha(publish/name)} for name in ('Constructd.Api','removed.dll')]
stamp=datetime.datetime.now(datetime.timezone.utc).isoformat()
dump(publish/'install.json',dict(source='installer',commit='b'*40,packageVersion='old',installedAt=stamp,previousCommit=None,updateId=None,files=old))
files=[]
for name,text in [('service/Constructd.Api','new'),('service/new.dll','added'),('scripts/bin/provision.sh','#!/bin/bash\necho provision\n'),('updater/update-construct-host.sh','verified-updater')]:
    p=extracted/name;write(p,text);p.chmod(0o755 if name.endswith('.sh') else 0o644)
    files.append(dict(path=name,sha256=sha(p)))
write(extracted/'SHA256SUMS',''.join(f['sha256']+'  '+f['path']+'\n' for f in files))
with zipfile.ZipFile(stage/'package.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in extracted.rglob('*'):
        if p.is_file(): z.write(p,p.relative_to(extracted).as_posix())
dump(stage/'manifest.json',dict(schemaVersion=1,commit='a'*40,ref='refs/heads/main',releaseTag='host-'+'a'*40,packageVersion='test',
    linuxAsset='construct-host-aaaaaaa-linux-x64.zip',linuxSha256=sha(stage/'package.zip'),linuxSizeBytes=(stage/'package.zip').stat().st_size,
    linuxSumsSha256=sha(extracted/'SHA256SUMS'),linuxUncompressedSizeBytes=sum(p.stat().st_size for p in extracted.rglob('*') if p.is_file()),
    linuxUpdaterPath='updater/update-construct-host.sh',linuxUpdaterSha256=files[-1]['sha256'],database=dict(schemaVersion=600,minReadableBy=0,breakingMigrations=[])))
dump(stage/'verified.json',dict(updateId=uid,commit='a'*40,verifiedAt=stamp,source='linux',files=files))
dump(data/'updates/handoff.json',dict(updateId=uid,commit='a'*40,previousCommit='b'*40,stagedPath=str(stage),publishDir=str(publish),scriptsDir=str(scripts),dataDir=str(data),
    serviceName='constructd',previousSchemaVersion=100,healthTimeoutSeconds=0,healthUrl='https://127.0.0.1:1/api/v1/health',certificateThumbprint='d'*40,
    adminCliPath=str(publish/'Constructd.Api'),healthToken='secret-sentinel',writtenAt=stamp))
PY
  handoff="$UPDATER_TEST_CASE/data/updates/handoff.json"
  fail_new=false; fail_all=false; migrate=false
}

# Test support edits only temporary JSON fixtures and validates exact contract key sets.
fixture_action() {
  python3 - "$UPDATER_TEST_CASE" "$@" <<'PY'
import datetime,json,pathlib,sys
b=pathlib.Path(sys.argv[1]);action,*args=sys.argv[2:];root=b/'data/updates'
h=json.loads((root/'handoff.json').read_text());stage=pathlib.Path(h['stagedPath']);rp=root/'last-update.json'
def read(p):return json.loads(p.read_text())
def write(p,v):p.write_text(json.dumps(v))
if action=='fence':write(root/'fence.json',dict(updateId=h['updateId'],disposition=args[0],actor='test',at=datetime.datetime.now(datetime.timezone.utc).isoformat()))
elif action=='backup-failure':
    p=b/'publish/install.json';v=read(p);v['files'].append(dict(path='service/missing.dll',sha256='0'*64));write(p,v)
elif action=='bad-manifest':(stage/'package.zip').write_text('tampered')
elif action=='database-mode':
    p=stage/'manifest.json';v=read(p);v['database'].update(minReadableBy=600,breakingMigrations=[600]);write(p,v)
elif action=='migrate':
    (b/'data/constructd.db').write_text('migrated');(b/'data/constructd.db-wal').unlink(missing_ok=True);(b/'data/constructd.db-shm').write_text('new-shm')
elif action=='attempt':
    r=read(rp);r['healthAttempts']+=1;write(rp,r)
elif action=='clear-terminal':
    r=read(rp);r['outcome']=None;write(rp,r)
elif action=='incomplete':
    write(rp,dict(updateId=h['updateId'],commit=h['commit'],previousCommit=h['previousCommit'],phase='replace',phaseAt=h['writtenAt'],outcome=None,error=None,
        backupPath=str(root/('backup-'+h['updateId'])),backupComplete=False,replaceStarted=True,stagedPath=str(stage),healthAttempts=0,manualSteps=[]))
elif action=='assert':
    r=read(rp);assert r['outcome']==args[0],r
    assert set(r)==set('updateId commit previousCommit phase phaseAt outcome error backupPath backupComplete replaceStarted stagedPath healthAttempts manualSteps'.split()),r
    assert isinstance(r['healthAttempts'],int) and isinstance(r['manualSteps'],list)
    datetime.datetime.fromisoformat(r['phaseAt'])
    if (root/'fence.json').exists():
        f=read(root/'fence.json');assert set(f)=={'updateId','disposition','actor','at'};assert f['disposition'] in ('closed','commitOnly','rollbackAuthorized')
    ledger=read(b/'publish/install.json')
    assert set(ledger)==set('source commit packageVersion installedAt previousCommit updateId files'.split()),ledger
    for f in ledger['files']:assert set(f)=={'path','sha256'}
    assert 'secret-sentinel' not in rp.read_text()
    if (root/'updater.log').exists():assert 'secret-sentinel' not in (root/'updater.log').read_text()
    assert (b/'publish/unowned.txt').read_text()=='unowned'
    assert (b/'scripts/keys/bootstrap_ed25519').read_text()=='preserved-key'
    assert (b/'scripts/custom-cache/cached.zip').read_text()=='preserved-cache'
    assert read(b/'publish/appsettings.Production.json')['Constructd']['DatabasePath']==str(b/'data/constructd.db')
elif action=='check-success':
    assert (b/'publish/Constructd.Api').read_text()=='new'
    assert (b/'publish/Constructd.Api').stat().st_mode & 0o777 == 0o755
    assert (b/'scripts/bin/provision.sh').stat().st_mode & 0o777 == 0o755
    assert not (b/'publish/removed.dll').exists()
    assert (b/'data/constructd.db').read_text()=='preserved-db'
elif action=='check-rollback':
    assert (b/'publish/Constructd.Api').read_text()=='old'
    assert (b/'publish/removed.dll').read_text()=='old-owned'
    assert not (b/'publish/new.dll').exists()
elif action=='check-database':
    assert (b/'data/constructd.db').read_text()=='preserved-db'
    assert (b/'data/constructd.db-wal').read_text()=='preserved-wal'
    assert not (b/'data/constructd.db-shm').exists()
elif action=='check-binary-db':assert (b/'data/constructd.db').read_text()=='migrated'
elif action=='check-manual':assert len(read(rp)['manualSteps'])==3
elif action=='tamper-backup':
    (pathlib.Path(read(rp)['backupPath'])/'service/Constructd.Api').write_text('bad')
elif action=='mixed':(b/'publish/Constructd.Api').write_text('mixed')
elif action=='missing-ledger':(b/'publish/install.json').unlink()
elif action=='stale-fence':write(root/'fence.json',dict(updateId='f'*32,disposition='closed',actor='test',at=h['writtenAt']))
else:raise AssertionError(action)
PY
}
test_update_health() {
  fixture_action attempt
  if [[ "$1" == new && "$migrate" == true ]]; then fixture_action migrate; fi
  [[ "$fail_all" != true && ( "$fail_new" != true || "$1" == old ) ]]
}
passed=0
run_update() {
  local expected="$1" actual=0
  invoke_construct_host_update "$handoff" "${2:-false}" "${3:-false}" || actual=$?
  [[ "$actual" == "$expected" ]] || { echo "Expected exit $expected, got $actual: $UPDATER_TEST_CASE" >&2; cat "$UPDATER_TEST_CASE/data/updates/last-update.json" >&2; exit 1; }
  passed=$((passed+1))
}
stops() { if [[ -f "$UPDATER_TEST_CASE/control.log" ]]; then awk '$1=="stop"{n++}END{print n+0}' "$UPDATER_TEST_CASE/control.log"; else echo 0; fi; }

fixture success
run_update 0
fixture_action assert succeeded
fixture_action check-success
before=$(stops); run_update 0 true true; [[ $(stops) == "$before" ]]
fixture_action clear-terminal
fixture_action fence commitOnly
run_update 0 true
[[ $(stops) == "$before" ]]; fixture_action assert succeeded

fixture closed
fixture_action fence closed
run_update 4 true
[[ $(stops) == 0 ]]

fixture backup-failure
fixture_action backup-failure
run_update 1
fixture_action assert applyFailed
[[ $(cat "$UPDATER_TEST_CASE/state") == active ]]
[[ $(cat "$UPDATER_TEST_CASE/publish/Constructd.Api") == old ]]

fixture rollback
fail_new=true; migrate=true
run_update 0
fixture_action assert rolledBack
fixture_action check-rollback
fixture_action check-binary-db

fixture database-rollback
fixture_action database-mode
fail_new=true; migrate=true
run_update 0
fixture_action assert rolledBackWithDatabase
fixture_action check-rollback
fixture_action check-database

fixture recovery-failure
fail_all=true
run_update 1
fixture_action assert recoveryFailed
fixture_action check-manual
fixture_action fence rollbackAuthorized
fail_all=false
run_update 0 true true
fixture_action assert rolledBack

fixture commit-only-mixed
run_update 0
fixture_action clear-terminal
fixture_action fence commitOnly
fixture_action mixed
before=$(stops); run_update 1 true
[[ $(stops) == "$before" ]]; fixture_action assert recoveryFailed

fixture incomplete
fixture_action incomplete
run_update 1 true
fixture_action assert recoveryFailed
fixture_action check-manual

fixture bad-manifest
fixture_action bad-manifest
run_update 1
[[ $(stops) == 0 ]]; fixture_action assert applyFailed

fixture tampered-backup
fail_all=true; run_update 1
fixture_action tamper-backup
fixture_action fence rollbackAuthorized
fail_all=false; run_update 1 true true
fixture_action assert recoveryFailed

fixture symlink
ln -s "$UPDATER_TEST_CASE/data" "$UPDATER_TEST_CASE/link"
handoff="$UPDATER_TEST_CASE/link/updates/handoff.json"
run_update 1
[[ $(stops) == 0 ]]

fixture scoped-fence
fixture_action stale-fence
run_update 0
fixture_action assert succeeded

fixture no-ledger
fixture_action missing-ledger
run_update 1
[[ $(cat "$UPDATER_TEST_CASE/state") == active ]]
[[ $(cat "$UPDATER_TEST_CASE/publish/Constructd.Api") == old ]]

# A revocation written between phases is observed before backup or replacement.
fixture revoked-after-stop
stop_update_service() { systemctl stop "$1"; fixture_action fence closed; }
run_update 4
[[ $(cat "$UPDATER_TEST_CASE/publish/Constructd.Api") == old ]]

printf 'host-updater-linux: %s scenarios passed (service control and health faked)\n' "$passed"
python3 service/tests/host-updater-health.test.py
python3 service/tests/host-installer-ledger.test.py
