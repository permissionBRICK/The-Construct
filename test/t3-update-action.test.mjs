// Run with T3_TEST_TOOLS pointing at a checkout with esbuild installed.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
const require=createRequire(resolve(process.env.T3_TEST_TOOLS || '.', 'package.json'));
const {build}=require('esbuild');
for(const channel of ['release','nightly']) {
 const result=await build({entryPoints:[`patches/t3code-${channel}/overlays/apps/web/src/components/constructUpdate.ts`],
  bundle:true,write:false,platform:'node',format:'esm',plugins:[{name:'toast',setup(b){
   b.onResolve({filter:/\/ui\/toast$/},()=>({path:'toast',namespace:'fixture'}));
   b.onLoad({filter:/.*/,namespace:'fixture'},()=>({contents:'export const toastManager={add(){}}; export const stackedThreadToast=x=>x;'}));
  }}]});
 const {startConstructUpdate}=await import('data:text/javascript;base64,'+Buffer.from(result.outputFiles[0].text).toString('base64'));
 let calls=0;
 const info={action:'reprovision',runningAction:null};
 const bridge={downloadUpdate:async()=>{calls++;return {accepted:true,completed:false,state:{construct:{...info,runningAction:'reprovision'}}};}};
 assert.equal(await startConstructUpdate(bridge,info),true);
 assert.equal(calls,1,'one explicit action must launch without a second confirmation');
 assert.equal(await startConstructUpdate(bridge,{...info,runningAction:'reprovision'}),false);
 assert.equal(calls,1);
 console.log(`PASS ${channel}: one click launches once; running action is guarded`);
}
