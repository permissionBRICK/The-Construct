#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
task_dir=$(mktemp -d)
trap 'rm -r "$task_dir"' EXIT
mkdir "$task_dir/publish"
head -c 131072 /dev/urandom > "$task_dir/publish/Constructd.Api.exe"
printf '{}\n' > "$task_dir/publish/appsettings.json"
mkdir "$task_dir/fdd"
head -c 4096 /dev/urandom > "$task_dir/fdd/Constructd.Api.exe"
printf '%s' '{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}' > "$task_dir/fdd/Constructd.Api.runtimeconfig.json"
commit=$(git rev-parse HEAD)
pwsh -NoProfile -File service/host/New-ConstructHostPackage.ps1 -PublishDir "$task_dir/publish" -FrameworkDependentPublishDir "$task_dir/fdd" -OutputDir "$task_dir/output" -Commit "$commit"
python3 scripts/package-construct-release.py --output "$task_dir/output" --commit "$commit" --repository permissionBRICK/The-Construct
python3 - "$task_dir/output" "$commit" <<'PY'
import hashlib,json,pathlib,sys,zipfile
root=pathlib.Path(sys.argv[1]);m=json.loads((root/'manifest.json').read_text());checks=0
assert not (root/'manifest.json.sig').exists();checks+=1
assert m['commit']==sys.argv[2] and m['releaseTag']=='host-'+sys.argv[2];checks+=1
assert m['ref']=='refs/heads/main' and m['database']['schemaVersion']>=600;checks+=1
source=root/m['sourceAsset']
assert source.stat().st_size==m['sourceSizeBytes'];checks+=1
assert hashlib.sha256(source.read_bytes()).hexdigest()==m['sourceSha256'];checks+=1
with zipfile.ZipFile(source) as z:
    assert z.read('The-Construct-main/.construct-revision').decode().strip()==m['commit'];checks+=1
    assert 'The-Construct-main/Auto-Install.ps1' in z.namelist();checks+=1
assert m['runtimes']==[{'name':'Microsoft.NETCore.App','majorVersion':10},{'name':'Microsoft.AspNetCore.App','majorVersion':10}];checks+=1
for prefix in ('payload','frameworkDependent'):
    zip_path=root/m[prefix+'Asset']
    assert zip_path.stat().st_size==m[prefix+'SizeBytes'];checks+=1
    assert hashlib.sha256(zip_path.read_bytes()).hexdigest()==m[prefix+'Sha256'];checks+=1
    with zipfile.ZipFile(zip_path) as z:
        sums=z.read('SHA256SUMS');assert hashlib.sha256(sums).hexdigest()==m['sumsSha256' if prefix=='payload' else 'frameworkDependentSumsSha256'];checks+=1
        files=dict((line[66:],line[:64]) for line in sums.decode().splitlines())
        assert set(z.namelist())==set(files)|{'SHA256SUMS'};checks+=1
        assert 'manifest.json' not in z.namelist();checks+=1
        assert sum(i.file_size for i in z.infolist())==m[prefix+'UncompressedSizeBytes']<=1073741824;checks+=1
        assert all(i.file_size<=268435456 and i.compress_type==zipfile.ZIP_DEFLATED for i in z.infolist());checks+=1
        for path,sha in files.items():
            assert hashlib.sha256(z.read(path)).hexdigest()==sha;checks+=1
        assert files[m['updaterPath']]==m['updaterSha256'];checks+=1
        assert z.read('scripts/config/iso-builder.json')==pathlib.Path('config/iso-builder.json').read_bytes();checks+=1
        assert not any('appsettings.Production.json' in p or '/keys/' in p for p in files);checks+=1
print(f'host-package: {checks} assertions passed ({len(files)} payload files); fixture executable, no Windows publish performed')
PY
