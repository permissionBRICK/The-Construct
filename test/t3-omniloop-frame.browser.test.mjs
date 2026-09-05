// Chromium integration: actual desktop CSP, panel URL expression, proxy asset
// rewrite and Omniloop GUI. Only the daemon's workflow data is a fixture.
// T3_TEST_SOURCE = upstream checkout, T3_TEST_TOOLS = esbuild/playwright checkout,
// OMNILOOP_GUI_SOURCE = Omniloop src/gui. No live daemon is started or modified.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import https from 'node:https';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {execFileSync} from 'node:child_process';
const root=path.resolve(import.meta.dirname,'..');
const source=process.env.T3_TEST_SOURCE;
const gui=process.env.OMNILOOP_GUI_SOURCE;
if(!source || !gui)throw Error('Set T3_TEST_SOURCE and OMNILOOP_GUI_SOURCE');
const require=createRequire(path.resolve(process.env.T3_TEST_TOOLS || root,'package.json'));
const {transform}=require('esbuild');const {chromium}=require('playwright');
const channel=process.env.T3_TEST_CHANNEL || 'release';
const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'t3-frame-'));
const protocolPath='apps/desktop/src/electron/ElectronProtocol.ts';
const original=execFileSync('git',['show',`HEAD:${protocolPath}`],{cwd:source,encoding:'utf8'});
const manifest=JSON.parse(fs.readFileSync(path.join(root,`patches/t3code-${channel}/source-transforms.json`)));
fs.mkdirSync(path.dirname(path.join(tmp,protocolPath)),{recursive:true});
fs.writeFileSync(path.join(tmp,protocolPath),original);
fs.writeFileSync(path.join(tmp,'manifest.json'),JSON.stringify({version:2,overlays:[],transforms:manifest.transforms.filter(t=>t.path===protocolPath)}));
execFileSync(process.execPath,[path.join(root,'bin/apply-t3code-source.mjs'),'apply','--source',tmp,'--manifest',path.join(tmp,'manifest.json')]);
const patched=fs.readFileSync(path.join(tmp,protocolPath),'utf8');
async function functionFrom(text,start,end) {
 const {code}=await transform(text.slice(text.indexOf(start),text.indexOf(end)),{loader:'ts',format:'esm'});
 return import('data:text/javascript;base64,'+Buffer.from(code).toString('base64'));
}
const {makeDesktopContentSecurityPolicy:makePolicy}=await functionFrom(patched,'export function makeDesktopContentSecurityPolicy','function withContentSecurityPolicy');
const {makeDesktopContentSecurityPolicy:oldPolicy}=await functionFrom(original,'export function makeDesktopContentSecurityPolicy','function withContentSecurityPolicy');
const proxy=fs.readFileSync(path.join(root,`patches/t3code-${channel}/overlays/apps/server/src/constructOmniloopProxy.ts`),'utf8');
const {rewriteOmniloopAsset}=await functionFrom(proxy,'export function rewriteOmniloopAsset','export function rewriteOmniloopLocation');
const panel=fs.readFileSync(path.join(root,`patches/t3code-${channel}/overlays/apps/web/src/components/omniloop/OmniloopPanel.tsx`),'utf8');
const expression=panel.match(/  const src = (.*);/)[1];
const frameSrc=new Function('httpBaseUrl','current','workflowId',`return ${expression}`);
const ticket='/construct/omniloop/abcdefghijklmnop';
const guiPath=ticket+'/gui/index.html?token=fixture-token';
const workflow={id:'wf_browser',name:'Browser fixture',status:'running',created_at:'2026-09-05T00:00:00Z',updated_at:'2026-09-05T00:00:00Z',started_at:'2026-09-05T00:00:00Z',total_tokens:{input:0,output:0,cache_creation:0,cache_read:0}};
const detail={workflow,nodes:[],approvals:[]};
const requests=[];
const streams=new Set();
function dashboard(req,res) {
 requests.push(req.url);
 // Do not normalize doubled slashes: the real proxy route does not match them.
 const url=new URL('http://fixture'+req.url);
 const rest=url.pathname.slice(ticket.length);
 if(!url.pathname.startsWith(ticket+'/')){res.writeHead(404);res.end('proxy route missed');return;}
 if(rest.startsWith('/gui/')) {
  const filename=path.basename(rest);
  if(!['index.html','app.js','splitters.js','workflow-structure.js'].includes(filename)){res.writeHead(404);res.end();return;}
  res.setHeader('Content-Type',filename.endsWith('.js')?'application/javascript':'text/html');
  res.end(rewriteOmniloopAsset(fs.readFileSync(path.join(gui,filename),'utf8'),ticket));return;
 }
 if(rest.startsWith('/sse/')) {
  res.writeHead(200,{'Content-Type':'text/event-stream','Cache-Control':'no-cache'});
  const global=rest==='/sse/workflows';
  res.write(`event: ${global?'workflow_list':'workflow_detail'}\ndata: ${JSON.stringify(global?{workflows:[workflow]}:detail)}\n\n`);
  streams.add(res);req.on('close',()=>streams.delete(res));return;
 }
 res.setHeader('Content-Type','application/json');res.end(JSON.stringify(rest.endsWith('/approvals')?{approvals:[]}:detail));
}
execFileSync('openssl',['req','-x509','-newkey','rsa:2048','-nodes','-keyout',path.join(tmp,'key.pem'),'-out',path.join(tmp,'cert.pem'),'-days','1','-subj','/CN=localhost'],{stdio:'ignore'});
const remote=http.createServer(dashboard);
const secure=https.createServer({key:fs.readFileSync(path.join(tmp,'key.pem')),cert:fs.readFileSync(path.join(tmp,'cert.pem'))},dashboard);
const parent=http.createServer((req,res)=>{
 res.setHeader('Content-Type','text/html');
 const url=new URL(req.url,'http://parent');
 const input={scheme:'t3code',targetOrigin:new URL('http://localhost/'),backendOrigin:new URL('http://localhost/'),clerkFrontendApiHostname:undefined};
 res.setHeader('Content-Security-Policy',(url.searchParams.has('old')?oldPolicy:makePolicy)(input));
 res.end('<!doctype html><body><div id="panel"></div></body>');
});
const servers=[remote,secure,parent];
for(const s of servers)await new Promise(r=>s.listen(0,'127.0.0.1',r));
const browser=await chromium.launch({headless:true,executablePath:process.env.T3_TEST_CHROMIUM,args:['--no-sandbox']});
const page=await browser.newPage({ignoreHTTPSErrors:true});
const errors=[];page.on('pageerror',e=>{errors.push(e.message);console.log('PAGE ERROR',e.message)});
if(process.env.T3_TEST_DEBUG)page.on('console',m=>{if(m.type()==='error')console.log(m.text())});
const parentUrl=`http://127.0.0.1:${parent.address().port}`;
const open=async(src,old=false)=>{
 await page.goto(parentUrl+(old?'/?old':''));
 await page.evaluate(src=>{
  window.violations=[];
  document.addEventListener('securitypolicyviolation',e=>window.violations.push(e.effectiveDirective));
  const frame=document.createElement('iframe');frame.title='Omniloop';frame.style.cssText='width:1100px;height:700px';frame.src=src;document.querySelector('#panel').append(frame);
 },src);
};
try {
 const base=`http://127.0.0.1:${remote.address().port}/`;
 const good=frameSrc(base,{guiPath},null);
 assert.equal(new URL(good).pathname,ticket+'/gui/index.html');
 await open(good,true);
 await page.waitForFunction(()=>window.violations.includes('frame-src'));
 assert.equal(requests.length,0,'old desktop CSP blocks the dashboard before a request');
 console.log('PASS reproduced blank frame: original CSP blocks the remote VM');
 const bad=base+guiPath;
 await open(bad);
 await page.waitForFunction(()=>window.violations.includes('frame-src'));
 // Without a desktop CSP the doubled-slash URL also misses the proxy route.
 assert.equal((await fetch(bad)).status,404);
 console.log('PASS reproduced doubled-slash URL missing the ticketed proxy');
 for(const [scheme,server] of [['http',remote],['https',secure]]) {
  for(const workflowId of [null,'wf_browser']) {
   const base=`${scheme}://127.0.0.1:${server.address().port}`;
   const src=frameSrc(base+'/',{guiPath},workflowId);
   assert.equal(src,frameSrc(base,{guiPath},workflowId));
   await open(src);
   const frame=page.frameLocator('iframe');
   await frame.locator('.sidebar-wf-name').filter({hasText:'Browser fixture'}).waitFor();
   if(!workflowId)await frame.locator('.sidebar-wf-name').filter({hasText:'Browser fixture'}).click();
   await frame.locator('#wf-name').filter({hasText:'Browser fixture'}).waitFor();
   await page.locator('iframe').evaluate(frame=>frame.style.width='400px');
   await frame.locator('#wf-name').filter({hasText:'Browser fixture'}).waitFor();
   assert.deepEqual(await page.evaluate(()=>window.violations),[]);
   console.log(`PASS ${channel}: ${scheme} dashboard ${workflowId?'workflow deep link':'sidebar navigation'}, JS assets and live SSE data`);
  }
 }
 await open(`http://127.0.0.1:${remote.address().port}/unrelated/`);
 await page.waitForFunction(()=>window.violations.includes('frame-src'));
 assert.equal(requests.some(r=>r==='/unrelated/'),false,'unrelated cross-origin frames stay blocked');
 assert.deepEqual(errors,[],'dashboard must run without JS errors');
 console.log('PASS unrelated remote frames remain blocked');
} finally {
 await browser.close();for(const s of streams)s.end();
 for(const s of servers){s.closeAllConnections();await new Promise(r=>s.close(r));}
 fs.rmSync(tmp,{recursive:true,force:true});
}
