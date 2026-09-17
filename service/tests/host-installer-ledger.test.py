#!/usr/bin/env python3
"""Run only the installer's ledger writer and ZIP-layout function in disposable directories."""
import hashlib
import json
import pathlib
import subprocess
import tempfile
import zipfile

installer = pathlib.Path(__file__).parents[1]/'host/install-construct-host.sh'
text = installer.read_text()
ledger = text.split("<<'LEDGER_PY'\n", 1)[1].split('\nLEDGER_PY\n', 1)[0]
stage_function = 'stage_from_zip() {' + text.split('stage_from_zip() {', 1)[1].split('\nbuild_service()', 1)[0]

with tempfile.TemporaryDirectory(prefix='installer-ledger-') as temp:
    root = pathlib.Path(temp)
    host, scripts, stage, payload = [root/name for name in ('host','scripts','stage','payload')]
    for path in (host, scripts, stage, payload): path.mkdir()
    for name, body in [('Constructd.Api','binary'),('lib.dll','library'),('appsettings.Production.json','settings'),('constructd.db-wal','wal')]:
        (stage/name).write_text(body); (host/name).write_text(body)
    (host/'unowned.txt').write_text('unowned')
    for path in (scripts/'bin', payload/'bin', scripts/'keys', payload/'keys'): path.mkdir()
    (payload/'bin/provision.sh').write_text('provision')
    (scripts/'bin/provision.sh').write_text('provision')
    (payload/'keys/private').write_text('never-owned')
    (scripts/'keys/private').write_text('never-owned')

    def write_ledger(publish, package_scripts, commit='', version=''):
        return subprocess.run(['python3','-',str(host),str(scripts),str(publish),str(package_scripts),commit,version],
                              input=ledger,text=True,check=True,capture_output=True)

    write_ledger(stage,payload,'a'*40,'test-version')
    result=json.loads((host/'install.json').read_text())
    assert result['source']=='installer' and result['commit']=='a'*40 and result['packageVersion']=='test-version'
    assert {f['path'] for f in result['files']} == {'service/Constructd.Api','service/lib.dll','scripts/bin/provision.sh'}
    assert all(f['sha256']==hashlib.sha256((host/f['path'][8:] if f['path'].startswith('service/') else scripts/f['path'][8:]).read_bytes()).hexdigest() for f in result['files'])
    assert (host/'install.json').stat().st_mode & 0o777 == 0o600
    assert (host/'constructd.db-wal').read_text()=='wal'
    write_ledger('','')  # Repair keeps binary identity, and does not claim checkout scripts.
    result=json.loads((host/'install.json').read_text())
    assert result['commit']=='a'*40 and result['packageVersion']=='test-version'
    assert {f['path'] for f in result['files']} == {'service/Constructd.Api','service/lib.dll'}
    write_ledger(stage,'')  # Raw publish has no trustworthy release identity.
    result=json.loads((host/'install.json').read_text())
    assert result['commit']=='unknown' and result['packageVersion']=='unknown'

    for layout in ('old','release'):
        package=root/(layout+'.zip')
        with zipfile.ZipFile(package,'w') as z:
            z.writestr('Constructd.Api' if layout=='old' else 'service/Constructd.Api','binary')
            if layout=='release':z.writestr('scripts/.construct-revision','a'*40+'\n')
        for explicit in (0,1):
            work=root/(layout+str(explicit));work.mkdir()
            command=stage_function+'\nTMP_ROOT="$1"; SOURCE_EXPLICIT="$2"; PACKAGE_SCRIPTS=""; INSTALL_COMMIT=""; die() { exit 99; }; stage_from_zip "$3"; printf "%s\\n%s\\n" "$STAGE" "$PACKAGE_SCRIPTS"'
            output=subprocess.check_output(['bash','-c',command,'fixture',str(work),str(explicit),str(package)],text=True).splitlines()
            assert pathlib.Path(output[0],'Constructd.Api').is_file()
            assert bool(output[1])==(layout=='release' and explicit==0)
print('host-installer-ledger: owned hashes, preserved files, identity and both ZIP layouts passed')
