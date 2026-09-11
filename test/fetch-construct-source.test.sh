#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export ROOT
python3 - <<'PYTEST'
import contextlib,hashlib,http.server,json,os,pathlib,shutil,subprocess,tempfile,threading,zipfile,io
root=pathlib.Path(os.environ['ROOT'])
script=root/'bin/fetch-construct-source.sh'
with tempfile.TemporaryDirectory(prefix='source-fetch-test-') as temp:
    t=pathlib.Path(temp); archive=t/'source.zip'
    subprocess.run(['git','archive','--format=zip','--prefix=The-Construct-main/','-o',str(archive),'HEAD'],cwd=root,check=True)
    good=archive.read_bytes(); commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()
    token='source-token-sentinel'; tokenfile=t/'token';tokenfile.write_text(token)
    requests=[]; state={'bytes':good,'statuses':[]}
    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append((self.path,self.headers.get('Authorization')))
            status=state['statuses'].pop(0) if state['statuses'] else 200
            body=state['bytes'] if status==200 else b'{"code":"source-not-cached"}'
            self.send_response(status);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
        def log_message(self,*args): pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);thread=threading.Thread(target=server.serve_forever);thread.start()
    try:
        base=f'http://127.0.0.1:{server.server_port}'
        parent=t/'opt';parent.mkdir();repo=parent/'repo';seed=subprocess.check_output(['id','-un'],text=True).strip()
        stubs=t/'stubs';stubs.mkdir();counter=t/'counter'; argvfile=t/'argv';headersfile=t/'headers';passed=0
        def reset(existing=True):
            for child in parent.iterdir():
                if child.is_dir(): shutil.rmtree(child)
                else: child.unlink()
            for child in stubs.iterdir(): child.unlink()
            counter.write_text('0');requests.clear();state.update(bytes=good,statuses=[])
            if existing: repo.mkdir();(repo/'old').write_text('keep old tree')
        def stub(name,body):
            p=stubs/name;p.write_text('#!/usr/bin/env bash\n'+body);p.chmod(0o755)
        def snapshot():
            return {str(p.relative_to(repo)):p.read_bytes() for p in repo.rglob('*') if p.is_file()} if repo.exists() else None
        def run(expected=0,extras=None):
            env=os.environ.copy()
            for key in list(env):
                if key.startswith('CONSTRUCT_SOURCE_') or key in ('CONSTRUCT_SERVICE_CA_FILE','CONSTRUCT_SERVICE_CA_B64','CONSTRUCT_CURL'):env.pop(key)
            env.update(CONSTRUCT_SERVICE_URL=base,CONSTRUCT_INSTANCE_NAME='vm',CONSTRUCT_SOURCE_COMMIT=commit,
                CONSTRUCT_SOURCE_SHA256=hashlib.sha256(state['bytes']).hexdigest(),CONSTRUCT_SOURCE_SIZE=str(len(state['bytes'])),
                CONSTRUCT_SEED_USER=seed,CONSTRUCT_VM_TOKEN_FILE=str(tokenfile),REPO_DIR=str(repo),PATH=str(stubs)+':'+os.environ['PATH'])
            if extras:env.update(extras)
            before=snapshot();r=subprocess.run(['bash',str(script)],env=env,capture_output=True,text=True,timeout=30)
            assert r.returncode==expected,(expected,r.returncode,r.stdout,r.stderr)
            assert token not in r.stdout+r.stderr
            if expected in (2,3,4,5,6):assert snapshot()==before,(expected,'previous repo changed')
            assert not list(parent.glob('.repo-fetch.*'))
            return r
        def ok(name):
            global passed
            passed+=1; print('PASS '+name)
        for existing in (False,True):
            reset(existing);r=run();assert (repo/'.construct-revision').read_text().strip()==commit
            assert (repo/'bin/provision.sh').stat().st_mode&0o777==0o755
            assert (repo/'README.md').stat().st_mode&0o777==0o644
            assert all(p.stat().st_uid==os.getuid() for p in repo.rglob('*'))
            assert not (repo/'old').exists();assert not list(parent.glob('.repo-old.*'))
            assert 'CONSTRUCT_SOURCE_INSTALLED='+commit in r.stdout
            assert requests==[(f'/api/v1/vms/vm/source/{commit}','VmToken '+token)]
            ok('replacement' if existing else 'fresh install')
        reset();run(4,{'CONSTRUCT_SOURCE_SIZE':str(len(good)+1)});ok('wrong size preserves tree')
        reset();run(4,{'CONSTRUCT_SOURCE_SHA256':'0'*64});ok('wrong hash preserves tree')
        reset();state['statuses']=[409,200];run();assert len(requests)==2;ok('409 retried once')
        reset();state['statuses']=[404];run(3);assert len(requests)==1;ok('404 preserves tree')
        reset();run(2,{'CONSTRUCT_SOURCE_COMMIT':''});ok('missing input')
        def badzip(name,mode=0,second=None):
            out=io.BytesIO()
            with zipfile.ZipFile(out,'w',compression=zipfile.ZIP_STORED) as z:
                e=zipfile.ZipInfo(name);e.external_attr=mode<<16;z.writestr(e,b'data')
                if second:z.writestr(second,b'data')
            return out.getvalue()
        for label,data in [('traversal',badzip('repo-main/../escape')),('symlink',badzip('repo-main/link',0xa1ff)),
            ('two roots',badzip('repo-main/a',second='other-main/b')),('absolute',badzip('/repo-main/a')),
            ('directory type mismatch',badzip('repo-main/file',0x41ed)),('file collision',badzip('repo-main/a',second='repo-main/a/b'))]:
            reset();state['bytes']=data;run(5);ok(label+' refused')
        for n in (1,2):
            reset();stub('chown',f'''n=$(cat '{counter}'); n=$((n+1)); printf '%s' "$n" > '{counter}'
if [[ "$n" == {n} ]]; then exit 1; fi
exec /usr/bin/chown "$@"
''');run(6);ok('chown failure '+str(n))
        for failures,expected in [('1',6),('2',6),('2 3',7)]:
            reset();stub('mv',f'''n=$(cat '{counter}'); n=$((n+1)); printf '%s' "$n" > '{counter}'
for fail in {failures}; do [[ "$n" == "$fail" ]] && exit 1; done
exec /usr/bin/mv "$@"
''');run(expected)
            if expected==7:assert not repo.exists();assert list(parent.glob('.repo-old.*'))
            ok('rename failures '+failures)
        reset();stub('rm','''for arg in "$@"; do [[ "$arg" == */.repo-old.* ]] && exit 1; done
exec /usr/bin/rm "$@"
''');r=run();assert 'warning: previous repo left at' in r.stderr;assert (repo/'README.md').exists();ok('old cleanup failure is nonfatal')
        reset();ca=t/'ca';ca.write_text('public certificate')
        stub('curl',f'''printf '%s\\n' "$@" > '{argvfile}'
for arg in "$@"; do if [[ "$arg" == @* ]]; then cat "${{arg#@}}" > '{headersfile}'; fi; done
exec /usr/bin/curl "$@"
''');run(extras={'CONSTRUCT_SERVICE_CA_FILE':str(ca)})
        assert token not in argvfile.read_text();assert headersfile.read_text().strip()=='Authorization: VmToken '+token
        assert '--cacert\n'+str(ca) in argvfile.read_text();ok('token uses header file and CA is passed')
        print(f'{passed} passed')
    finally:
        server.shutdown();thread.join();server.server_close()

PYTEST
