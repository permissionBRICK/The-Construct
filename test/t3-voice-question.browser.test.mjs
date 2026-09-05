// Run the actual inventory's voice callbacks and upstream replacement callback
// in React in Chromium. Audio/STT transport is the only mocked boundary.
// T3_TEST_SOURCE: matching upstream checkout; T3_TEST_TOOLS: package directory
// providing esbuild + playwright. T3_TEST_CHANNEL defaults to release.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {execFileSync} from 'node:child_process';
const root = path.resolve(import.meta.dirname, '..');
const source = process.env.T3_TEST_SOURCE;
if (!source) throw new Error('Set T3_TEST_SOURCE to the matching upstream checkout');
const toolRequire = createRequire(path.resolve(process.env.T3_TEST_TOOLS || root, 'package.json'));
const sourceRequire = createRequire(path.join(source, 'apps/web/package.json'));
const {build} = toolRequire('esbuild');
const {chromium} = toolRequire('playwright');
const channel = process.env.T3_TEST_CHANNEL || 'release';
const manifestPath = `patches/t3code-${channel}/source-transforms.json`;
const manifest = JSON.parse(process.env.T3_TEST_BASELINE
  ? execFileSync('git', ['show', `${process.env.T3_TEST_BASELINE}:${manifestPath}`], {cwd:root, encoding:'utf8'})
  : fs.readFileSync(path.join(root, manifestPath), 'utf8'));
const inserts = manifest.transforms.filter(t => t.path.endsWith('/ChatComposer.tsx')).map(t => t.insert || '');
const types = inserts.find(s => s.includes('type VoiceInsertionState')).split('type VoiceInsertionState')[1];
const voice = inserts.find(s => s.includes('const stopVoiceRecording = useCallback'));
const upstream = fs.readFileSync(path.join(source, 'apps/web/src/components/chat/ChatComposer.tsx'), 'utf8');
const replacement = upstream.slice(upstream.indexOf('  const applyPromptReplacement = useCallback('), upstream.indexOf('  const readComposerSnapshot = useCallback('));
const entry = `
import React, {useState, useRef, useCallback, useEffect} from ${JSON.stringify(sourceRequire.resolve('react'))};
import {createRoot} from ${JSON.stringify(sourceRequire.resolve('react-dom/client'))};
type VoiceInsertionState${types}
const environmentId = 'env'; const environmentUnavailable = null; const supportsVoiceInput = true;
const voiceInputSource = {source:'host'}; const composerDraftTarget = 'thread';
let nextSession = 0; const composerTargetKey = x => x; const randomUUID = () => String(++nextSession);
const AsyncResult = {isFailure: () => false}; const toastManager = {add: x => {throw Error(JSON.stringify(x))}};
const collapseExpandedComposerCursor = (v,c) => c; const expandCollapsedComposerCursor = (v,c) => c;
const detectComposerTrigger = () => null;
const replaceTextRange = (value,start,end,text) => ({text:value.slice(0,start)+text+value.slice(end),cursor:start+text.length});
const createVoiceAudioSender = () => {throw Error('unexpected client transport')};
const startClientAudioCapture = () => {throw Error('unexpected client capture')};
const MAX_PENDING_VOICE_CHUNKS = 8; const describeVoiceInputFailure = () => 'failure';
function Harness() {
 const [prompt,setPrompt] = useState('chat draft stays here');
 const [pending,setPending] = useState({requestId:'request',id:'question-1',customAnswer:'answer prefix'});
 const activePendingProgress = pending && {...pending, activeQuestion:{id:pending.id}};
 const activePendingUserInput = pending;
 const promptRef = useRef(prompt);
 const [composerCursor,setComposerCursor] = useState(0);
 const setComposerTrigger = () => {};
 const composerEditorRef = useRef(null);
 const element = useRef(null);
 composerEditorRef.current = {readSnapshot: () => ({value:element.current.value,expandedCursor:element.current.selectionStart}),focusAt:c=>{element.current.focus();element.current.setSelectionRange(c,c)}};
 const readComposerSnapshot = useCallback(() => composerEditorRef.current.readSnapshot(), []);
 const onChangeActivePendingUserInputCustomAnswer = (id,text) => {
   window.writes.push(id);
   setPending(previous => ({...previous, customAnswer:text}));
 };
 useEffect(() => {promptRef.current = activePendingProgress?.customAnswer ?? prompt}, [pending,prompt]);
 const [isVoiceRecording,setIsVoiceRecording] = useState(false);
 const [voiceLevel,setVoiceLevel] = useState(0);
 const voiceInsertionRef = useRef(null); const voiceShortcutRef = useRef(null);
 const runStartVoiceInput = ({onEvent}) => {window.transcript=text=>onEvent({type:'transcript',text});return new Promise(()=>{})};
 const runStopVoiceInput = useCallback(() => {window.stops++}, []); const runSendVoiceAudio = () => {};
 ${replacement}
 ${voice}
 window.changeQuestion = () => setPending(p => ({...p, id:'question-2'}));
 window.leaveQuestion = () => setPending(null);
 window.staleRef = () => {promptRef.current=prompt};
 window.changeChatDraft = () => setPrompt('saved background draft');
 return React.createElement('form',{'data-chat-composer-form':'true'},
   React.createElement('textarea',{ref:element,value:pending?.customAnswer ?? prompt,onChange:e=>pending?setPending({...pending,customAnswer:e.target.value}):setPrompt(e.target.value)}),
   React.createElement('button',{type:'button',onPointerDown:e=>e.preventDefault(),onClick:toggleVoiceRecording,'data-recording':String(isVoiceRecording)},'Mic'),
   React.createElement('output',null,prompt));
}
window.stops=0;window.writes=[];
createRoot(document.getElementById('root')).render(React.createElement(Harness));
`;
const browser = await chromium.launch({headless:true, ...(process.env.T3_TEST_CHROMIUM ? {executablePath:process.env.T3_TEST_CHROMIUM} : {})});
try {
 const bundle = await build({stdin:{contents:entry,loader:'tsx',resolveDir:source},bundle:true,write:false,platform:'browser',define:{'process.env.NODE_ENV':'"development"'}});
 const page = await browser.newPage();
 page.setDefaultTimeout(2000);
 const errors=[];page.on('pageerror',e=>{errors.push(e.message); console.error('Browser:',e.message)});
 async function reset() {
   await page.goto('about:blank');
   await page.setContent('<div id="root"></div>');
   await page.addScriptTag({content:bundle.outputFiles[0].text});
   await page.locator('textarea').waitFor();
 }
 async function recording(value) { await page.waitForFunction(v=>document.querySelector('button')?.dataset.recording===String(v), value, {timeout:2000}); }
 await reset();
 await page.locator('textarea').focus();
 await page.locator('textarea').evaluate(el=>el.setSelectionRange(el.value.length,el.value.length));
 await page.getByText('Mic',{exact:true}).click();
 await recording(true);
 await page.evaluate(()=>window.changeChatDraft());
 await recording(true);
 await page.evaluate(()=>{window.staleRef();window.transcript('spoken')});
 await page.waitForFunction(()=>document.querySelector('textarea').value==='answer prefix spoken');
 await recording(true);
 await page.evaluate(()=>window.transcript('spoken answer'));
 await page.waitForFunction(()=>document.querySelector('textarea').value==='answer prefix spoken answer');
 assert.equal(await page.locator('output').textContent(),'saved background draft');
 assert.deepEqual(await page.evaluate(()=>window.writes),['question-1','question-1']);
 assert.equal(await page.evaluate(()=>window.stops),0,'dictation must not stop itself');
 await page.locator('textarea').fill('my manual edit');
 await recording(false);
 await page.evaluate(()=>window.transcript('late result'));
 assert.equal(await page.locator('textarea').inputValue(),'my manual edit');
 await reset();
 await page.getByText('Mic',{exact:true}).click();await recording(true);
 await page.evaluate(()=>window.changeQuestion());await recording(false);
 await page.evaluate(()=>window.transcript('wrong question'));
 assert.equal(await page.locator('textarea').inputValue(),'answer prefix');
 assert.deepEqual(await page.evaluate(()=>window.writes),[]);
 await page.evaluate(()=>window.leaveQuestion());
 await page.locator('textarea').focus();
 await page.locator('textarea').evaluate(el=>el.setSelectionRange(el.value.length,el.value.length));
 await page.getByText('Mic',{exact:true}).click();await recording(true);
 await page.evaluate(()=>window.transcript('normal chat'));
 await page.waitForFunction(()=>document.querySelector('textarea').value==='chat draft stays here normal chat');
 assert.deepEqual(errors,[]);
 console.log('PASS: '+channel+' question dictation, cumulative transcripts, stale refs, manual edits, question switching, and normal chat');
} finally {await browser.close();}

