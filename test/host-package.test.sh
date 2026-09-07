#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
task_dir=$(mktemp -d)
trap 'rm -r "$task_dir"' EXIT
mkdir "$task_dir/publish"
head -c 131072 /dev/urandom > "$task_dir/publish/Constructd.Api.exe"
printf '{}\n' > "$task_dir/publish/appsettings.json"
openssl genpkey -algorithm ED25519 -out "$task_dir/key.pem" 2>/dev/null
commit=$(git rev-parse HEAD)
pwsh -NoProfile -File service/host/New-ConstructHostPackage.ps1 -PublishDir "$task_dir/publish" -OutputDir "$task_dir/output" -Commit "$commit" -SigningKeyPath "$task_dir/key.pem"
openssl pkey -in "$task_dir/key.pem" -pubout -out "$task_dir/public.pem" 2>/dev/null
openssl pkeyutl -verify -pubin -inkey "$task_dir/public.pem" -rawin -in "$task_dir/output/manifest.json" -sigfile "$task_dir/output/manifest.json.sig"
python3 - "$task_dir/output" "$commit" <<'PY'
import hashlib,json,pathlib,sys,zipfile
root=pathlib.Path(sys.argv[1]);m=json.loads((root/'manifest.json').read_text());checks=0
assert m['commit']==sys.argv[2] and m['releaseTag']=='host-'+sys.argv[2];checks+=1
assert m['ref']=='refs/heads/main' and m['database']['schemaVersion']>=600;checks+=1
zip_path=root/m['payloadAsset']
assert hashlib.sha256(zip_path.read_bytes()).hexdigest()==m['payloadSha256'];checks+=1
with zipfile.ZipFile(zip_path) as z:
    sums=z.read('SHA256SUMS');assert hashlib.sha256(sums).hexdigest()==m['sumsSha256'];checks+=1
    files=dict((line[66:],line[:64]) for line in sums.decode().splitlines())
    assert set(z.namelist())==set(files)|{'SHA256SUMS'};checks+=1
    assert 'manifest.json' not in z.namelist();checks+=1
    assert sum(i.file_size for i in z.infolist())<=zip_path.stat().st_size*4;checks+=1
    for path,sha in files.items():
        assert hashlib.sha256(z.read(path)).hexdigest()==sha;checks+=1
    assert files[m['updaterPath']]==m['updaterSha256'];checks+=1
    assert z.read('scripts/config/iso-builder.json')==pathlib.Path('config/iso-builder.json').read_bytes();checks+=1
    assert not any('appsettings.Production.json' in p or '/keys/' in p for p in files);checks+=1
print(f'host-package: {checks} assertions passed ({len(files)} payload files); fixture executable, no Windows publish performed')
PY
