import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
const data=Buffer.alloc(8*1024*1024, 0x61);
const api=createServer((req,res)=>{
 if(req.url==='/redirect'){res.writeHead(302,{Location:'/binary'});res.end();return;}
 if(req.url==='/binary'){res.writeHead(200,{'Content-Length':data.length});res.end(data);return;}
 res.writeHead(404);res.end('not found');
});
await new Promise(r=>api.listen(0,'127.0.0.1',r));
try {
 const child=spawn(process.platform==='win32'?'powershell.exe':'pwsh',
 ['-NoProfile','-ExecutionPolicy','Bypass','-File','test/t3-reprovision-host.test.ps1',
 '-DownloadBase',`http://127.0.0.1:${api.address().port}`], {stdio:'inherit'});
 const [code]=await once(child,'exit');
 if(code!==0)throw Error(`Host regression tests exited ${code}; expected SHA ${createHash('sha256').update(data).digest('hex')}`);
} finally { await new Promise(r=>api.close(r)); }
