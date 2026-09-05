// Actual patched runtime, isolated HTTP API and accelerated timers; no live turns.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createServer } from 'node:http';
const tmp = mkdtempSync(join(tmpdir(), 't3-capacity-'));
const bundle = join(tmp, 'bin.mjs');
writeFileSync(bundle, `function fixture(context, message) {
\t\tif (message.type === "rate_limit_event") {
    return;
  }
\t\tconst status = turnStatusFromResult(message);
\t\tconst errorMessage = resultUserFacingError(message);
}`);
writeFileSync(join(tmp, 'token'), 'test-token');
execFileSync(process.execPath, ['extension/vm/construct-t3park-patch.mjs', 'apply', '--bundle', bundle],
 {env: {...process.env, T3PARK_SKIP_TOKEN: 'true'}});
Object.assign(process.env, {T3CODE_HOME:tmp, T3PARK_TOKEN_FILE:join(tmp,'token'),
 T3PARK_TEST_DELAY_MS:'50', T3PARK_RETRY_MS:'20', T3PARK_VISIBILITY_SETTLE_MS:'0', T3PARK_VISIBILITY_RETRY_MS:'5'});
const threads = new Map();
const commands = [];
const delays = [];
const capacity = () => JSON.parse(readFileSync(join(tmp,'userdata/t3park-capacity.json')));
const pending = () => JSON.parse(readFileSync(join(tmp,'userdata/t3park-pending.json')));
const message = 'Selected model is at capacity. Please try a different model.';
const event = (thread, type, payload) => globalThis.__t3park.onCodexEvent(thread,
 {provider:'codex', type, turnId:thread.latestTurn.turnId, payload});
const newThread = id => {
 const t = {id, latestUserMessageAt:new Date(Date.now()-1000).toISOString(),
  latestTurn:{turnId:id+'-initial',state:'error',completedAt:new Date().toISOString()},
  session:{status:'error',lastError:message,updatedAt:new Date().toISOString()},
  modelSelection:{instanceId:'codex',model:'gpt-6'},runtimeMode:'full-access',interactionMode:'plan'};
 threads.set(id,t);return t;
};
const fail = t => {
 event(t,'runtime.error',{message});
 const text = event(t,'turn.completed',{state:'failed'});
 const c = capacity()[t.id];
 if (c && !c.exhausted) delays.push(c.delayMs);
 return text;
};
let mode = 'fail';
const api = createServer(async(req,res)=>{
 assert.equal(req.headers.authorization,'Bearer test-token');
 if(req.url.endsWith('/shell')) return res.end(JSON.stringify({threads:[...threads.values()]}));
 let body='';for await(const chunk of req)body+=chunk;
 const cmd=JSON.parse(body);
 if(cmd.type==='thread.turn.start') {
  commands.push(cmd);
  const t=threads.get(cmd.threadId);
  if(mode==='reject-once') { mode='accept'; res.statusCode=503;res.end('{}');return; }
  Object.assign(t,{latestUserMessageAt:cmd.createdAt,latestTurn:{turnId:cmd.message.messageId,state:'error',completedAt:new Date().toISOString()}});
  if(mode==='fail') {
   event(t,'turn.started',{});
   fail(t); // Result arrives BEFORE the previous HTTP dispatch resolves.
  }
  if(mode==='lost-response') { mode='accept';req.socket.destroy();return; }
 }
 res.end('{}');
});
await new Promise(r=>api.listen(0,'127.0.0.1',r));
process.env.T3CODE_PORT=String(api.address().port);
const wait = async predicate => {const until=Date.now()+5000;while(!predicate()){if(Date.now()>until)throw Error('timeout');await new Promise(r=>setTimeout(r,10));}};
try {
 await import(pathToFileURL(bundle).href);
 const t=newThread('ten');
 assert.match(fail(t), /retry 1\/10/);
 const first=capacity().ten;
 assert.match(event(t,'turn.completed',{state:'failed',errorMessage:message}), /retry 1\/10/);
 assert.deepEqual(capacity().ten,first,'duplicate terminal event must not consume another retry');
 await wait(()=>capacity().ten?.exhausted);
 await new Promise(r=>setTimeout(r,100));
 assert.equal(commands.length,10,'hard maximum of ten automatic turns');
 assert.deepEqual(delays,[5000,10000,20000,40000,60000,60000,60000,60000,60000,60000]);
 assert.equal(pending().ten,undefined);
 assert.match(event(t,'turn.completed',{state:'failed',errorMessage:message}), /stopped after 10/);
 assert.ok(commands.every(c=>c.modelSelection.model==='gpt-6' && c.runtimeMode==='full-access' && c.interactionMode==='plan'));
 console.log('PASS increasing delays, exact ten-retry cap, duplicate results, fast-result race and same model');
 // Manual turn resets an exhausted chain, success clears it.
 t.latestUserMessageAt=new Date(Date.now()+1000).toISOString();t.latestTurn.turnId='manual';
 event(t,'turn.started',{});
 assert.equal(capacity().ten,undefined);
 assert.match(fail(t),/retry 1\/10/);
 event(t,'turn.completed',{state:'completed'});
 assert.equal(capacity().ten,undefined);assert.equal(pending().ten,undefined);
 console.log('PASS manual continuation and successful completion reset the budget');
 const cancelled=newThread('cancelled');fail(cancelled);
 cancelled.latestUserMessageAt=new Date(Date.now()+1000).toISOString();
 const n=commands.length;
 await wait(()=>!pending().cancelled);
 await new Promise(r=>setTimeout(r,80));assert.equal(commands.length,n);
 console.log('PASS manual continuation cancels the pending automatic turn');
 const unrelated=newThread('unrelated');
 event(unrelated,'runtime.warning',{message});
 event(unrelated,'turn.completed',{state:'failed',errorMessage:'Network disconnected'});
 assert.equal(capacity().unrelated,undefined);
 globalThis.__t3park.onCodexEvent(unrelated,{provider:'claudeAgent',turnId:'x',type:'turn.completed',payload:{state:'failed',errorMessage:message}});
 assert.equal(capacity().unrelated,undefined);
 console.log('PASS ordinary errors, provider retries and other providers are ignored');
 mode='reject-once';const transport=newThread('transport');fail(transport);
 await wait(()=>commands.filter(c=>c.threadId==='transport').length===2 && !pending().transport);
 const retries=commands.filter(c=>c.threadId==='transport');
 assert.deepEqual(retries[0],retries[1],'transport retries must reuse the command and message IDs');
 console.log('PASS transport retries reuse the persisted command');
 mode='lost-response';const lost=newThread('lost');fail(lost);
 await wait(()=>commands.some(c=>c.threadId==='lost'));
 await wait(()=>capacity().lost.attempts===2); // recover the accepted failed turn from shell
 await wait(()=>!pending().lost);
 assert.equal(commands.filter(c=>c.threadId==='lost').length,2,'one dispatch per failed turn even after lost response');
 // A new process must continue the saved retry budget, not start at one.
 writeFileSync(join(tmp,'restart-thread.json'),JSON.stringify(lost));
 const child=`await import(${JSON.stringify(pathToFileURL(bundle).href)}); const fs=await import('node:fs'); const t=JSON.parse(fs.readFileSync(${JSON.stringify(join(tmp,'restart-thread.json'))})); const s=JSON.parse(fs.readFileSync(${JSON.stringify(join(tmp,'userdata/t3park-capacity.json'))})); if(s.lost.attempts!==2)throw Error('lost retry budget'); globalThis.__t3park.onCodexEvent(t,{provider:'codex',type:'turn.completed',turnId:t.latestTurn.turnId,payload:{state:'failed',errorMessage:${JSON.stringify(message)}}}); const n=JSON.parse(fs.readFileSync(${JSON.stringify(join(tmp,'userdata/t3park-capacity.json'))})); if(n.lost.attempts!==3)throw Error('restart reset budget'); process.exit(0);`;
 execFileSync(process.execPath,['--input-type=module','-e',child],{env:process.env});
 console.log('PASS lost-response recovery and persisted retry budget across process restart');
} finally {
 await new Promise(r=>api.close(r));rmSync(tmp,{recursive:true,force:true});
}
