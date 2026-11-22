import { useState } from 'preact/hooks';
import type { AgentId, Everything, Job, SignIn, VaultRequest, Vm } from '../backend/types';
import { spanChoices, spanText } from '../backend/api/map';
import { doneWord, failedTitle, jobWord, mainAction, repairWhen, running } from '../model/vm';
import { shortVersion, versionParts } from '../model/update';
import { AGENTS } from '../model/settings';
import { linkParts, lostSignIns } from '../model/signin';
import { openExternal } from './shell';
import { Icon } from './icons';
import { Bar, Btn, Confirm, Field, Pip, Switch, Tag, UpTag, useFollow } from './kit';
import { copy, na, useApp } from './ctx';

/* The blocks that sit on the stack, in the pop-out, the window's overview and the phone. */

/** A vault request: the only thing in the product that asks for attention. Yellow. The VM and host are named as
    the engine that holds the request names them; the agent's own words are marked as the agent's. A typed
    value lives only in this card's field and is emptied when the card answers. */
export function VaultCard({ r, onDone }: { r: VaultRequest; onDone?: () => void }) {
  const { be, s } = useApp();
  const [value, setValue] = useState('');
  const done = () => { setValue(''); onDone?.(); };
  const save = () => { if (value) { be.enterSecret(r.id, value); done(); } };
  // The span the VM asked for is the default; the user may pick another before allowing.
  const choose = !na(s, 'approval-span') && r.kind === 'lease' && !!r.ttlSeconds;
  const [ttl, setTtl] = useState<number | undefined>(undefined);
  const spanWord = ttl ? spanText(ttl) : r.span;
  const span = spanWord && !na(s, 'approval-span') ? ` ${spanWord}` : '';
  return (
    <div class="blk warn ask" role="alertdialog" aria-label={`Vault request for ${r.secret}`}>
      <div class="bh"><Icon n="key" /><b>{r.secret}</b><span>{r.vmName} · {r.hostName}</span></div>
      {r.reason && <div class="msg"><Tag>agent</Tag> <span class="quote">{r.reason}</span></div>}
      {r.kind === 'enter' ? (
        <>
          <Field type="password" value={value} onInput={setValue} label={`Value of ${r.secret}`} placeholder="Value" onEnter={save} />
          <div class="two">
            <Btn onClick={() => { be.enterSecret(r.id, null); done(); }}>Decline</Btn>
            <Btn kind="pri" disabled={!value} onClick={save}>Save</Btn>
          </div>
        </>
      ) : (
        <div class="two">
          <Btn onClick={() => { be.answerLease(r.id, false); done(); }}>Deny</Btn>
          {choose ? (
            <span class="split">
              <Btn kind="pri" onClick={() => { be.answerLease(r.id, true, ttl); done(); }}>{r.action || 'Allow'}{span}</Btn>
              <details class="more">
                <summary aria-label="Other spans"><Icon n="down" /></summary>
                <div class="vml">{spanChoices(r.ttlSeconds).filter(x => x !== (ttl ?? r.ttlSeconds)).map(x => (
                  <button type="button" onClick={e => { (e.currentTarget.closest('details') as HTMLDetailsElement).open = false; setTtl(x === r.ttlSeconds ? undefined : x); }}>{spanText(x)}</button>
                ))}</div>
              </details>
            </span>
          ) : <Btn kind="pri" onClick={() => { be.answerLease(r.id, true); done(); }}>{r.action || 'Allow'}{span}</Btn>}
        </div>
      )}
    </div>
  );
}

/** The last "update everything": each part and its state, as the agent reports it. */
export function EverythingBlock({ e }: { e: Everything }) {
  const word = { pending: 'waits', running: 'running', done: 'done', skipped: 'skipped', failed: 'failed' };
  return (
    <div class={`blk job ${e.parts.some(p => p.state === 'failed') ? 'bad' : ''}`} aria-label="Update everything">
      <div class="bh"><Icon n="up" /><b>{e.running ? 'Updating' : 'Update'}</b></div>
      {e.parts.map(p => (
        <div class="bf ev"><span class="grow">{p.name}{p.detail && <small class="d">{p.detail}</small>}</span><span class={p.state === 'failed' ? 'badc' : p.state === 'done' ? 'okc' : ''}>{word[p.state]}</span></div>
      ))}
    </div>
  );
}

export function VaultCards() {
  const { s } = useApp();
  return <>{s.vaultRequests.map(r => <VaultCard key={r.id} r={r} />)}</>;
}

/** A lost sign-in (S36), like the vault's card: the agent was signed in and the VM reports it signed out. Sign in
    starts its sign-in, and the card turns into the sign-in's panel; the cross puts the card away. */
export function SignInCard({ vm, agent, onDone }: { vm: Vm; agent: AgentId; onDone?: () => void }) {
  const { s, be } = useApp();
  const name = AGENTS.find(a => a.id === agent)?.name ?? agent;
  if (s.signIn?.vmId === vm.id && s.signIn.agent === agent) return <SignInBlock si={s.signIn} />;
  return (
    <div class="blk warn ask" role="alertdialog" aria-label={`${vm.name}: ${name} is signed out`}>
      <div class="bh"><Icon n="key" /><b>{vm.name}: {name} is signed out</b>
        <Btn sm icon="x" title="Dismiss" onClick={() => { be.dismissSignInNotice(vm.id, agent); onDone?.(); }} /></div>
      <Btn kind="pri" icon="key" onClick={() => be.signIn(vm.id, agent)}>Sign in</Btn>
    </div>
  );
}

/** What the move to the new Construct left to know (open vault leases ended, a VM not reached), after it: a calm card
    until put away, never a gate (R9). */
export function MovedNotes() {
  const { s, be } = useApp();
  if (!s.movedNotes?.length) return null;
  return (
    <div class="blk ok" role="status" aria-label="Moved over">
      <div class="bh"><Icon n="check" /><b>Moved over</b><Btn sm icon="x" title="Dismiss" onClick={() => be.readMovedNotes()} /></div>
      {s.movedNotes.map(n => <div class="msg">{n}</div>)}
    </div>
  );
}

export function SignInCards() {
  const { s } = useApp();
  return <>{lostSignIns(s.vms).map(l => <SignInCard key={l.vm.id + '/' + l.id.id} vm={l.vm} agent={l.id.id} />)}</>;
}

function LogView({ job }: { job: Job }) {
  const { toast } = useApp();
  const text = job.log.join('\n');
  const follow = useFollow<HTMLPreElement>(job.log.length);
  return (
    <div class="logbox">
      <pre class="code" ref={follow.ref} onScroll={follow.onScroll}>{job.log.map(l => <span class="l">{l}</span>)}</pre>
      <div class="bf"><span /><Btn sm icon="copy" onClick={() => { copy(text); toast('Copied'); }}>Copy</Btn></div>
    </div>
  );
}

/** The repair agent under a failed job: its output as plain text, Stop, and how it ended. By default it runs as a
    T3 Code thread in the VM, which the user answers in T3 Code: the block opens it there. In Construct's chat
    (`repair.chat`) it has one reply box while it waits for an answer. */
function Repair({ job }: { job: Job }) {
  const { be } = useApp();
  const r = job.repair!;
  const [text, setText] = useState('');
  const follow = useFollow<HTMLDivElement>(`${r.messages.length}/${r.state}`);
  const asking = r.state === 'asking';
  const live = r.state === 'working' || asking;
  const t3 = r.t3Thread;
  const send = () => { if (asking && text.trim()) { be.replyRepair(job.id, text); setText(''); } };
  return (
    <div class="repair" aria-label="Repair agent">
      <div class="bh"><Icon n="fix" /><b>{r.agent}</b><span class="d">{repairWhen(r)}</span>
        {live && <Btn sm onClick={() => be.stopRepair(job.id)}>Stop</Btn>}
      </div>
      <div class="chat" aria-live="polite" ref={follow.ref} onScroll={follow.onScroll}>
        {r.messages.map(m => <p class={m.from}>{m.text}</p>)}
        {r.state === 'working' && <p class="agent typing" aria-label="working" />}
        {r.outcome && <p class={`end ${r.state === 'done' ? 'okc' : ''}`}>{r.outcome}</p>}
      </div>
      {t3 && (
        <div class="open">
          <span>{asking ? 'It waits for your answer in T3 Code.' : live ? 'It runs as a thread in this VM\'s T3 Code.' : 'The thread stays in T3 Code.'}</span>
          <Btn sm kind={asking ? 'pri' : ''} icon="open" onClick={() => be.open(job.vmId, 't3', t3)}>Open in T3 Code</Btn>
        </div>
      )}
      {asking && !t3 && (
        <div class="reply">
          <Field value={text} onInput={setText} label="Reply" placeholder="Reply" onEnter={send} wide />
          <Btn kind="pri" icon="send" title="Send" onClick={send} disabled={!text.trim()} />
        </div>
      )}
    </div>
  );
}

export function JobBlock({ job, vm }: { job: Job; vm: Vm }) {
  const { be, s, surf, shell } = useApp();
  const [log, setLog] = useState(false);
  if (job.state === 'failed') {
    // The pop-out has no room for the chat: its button opens the window on this VM, where the chat is.
    const pop = surf === 'pop';
    return (
      <div class="blk bad job">
        <div class="bh"><Icon n="x" /><b>{failedTitle(job)}</b><Btn sm icon="log" onClick={() => setLog(!log)}>Log</Btn>
          <Btn sm icon="x" title="Dismiss" onClick={() => be.dismissJob(job.id)} /></div>
        <div class="msg">{job.step}: {job.error}</div>
        {(log || !job.repair) && <LogView key={job.id} job={job} />}
        {pop && job.repair?.t3Thread && !job.canRepair && (
          // The repair runs in T3 Code: the pop-out opens the thread there.
          <Btn icon="open" onClick={() => be.open(job.vmId, 't3', job.repair!.t3Thread!)}>{`Repair in T3 Code · ${repairWhen(job.repair)}`}</Btn>
        )}
        {pop && (job.repair || job.canRepair) && !(job.repair?.t3Thread && !job.canRepair) && (
          <Btn icon="fix" onClick={() => { if (!job.repair || job.canRepair) be.startRepair(job.id); shell.openWindow({ page: 'home', arg: vm.id }); }}>
            {job.repair && !job.canRepair ? `${job.repair.agent} · ${repairWhen(job.repair)}` : 'Let an agent fix it'}
          </Btn>
        )}
        {!pop && job.repair && <Repair key={job.id} job={job} />}
        {!pop && job.canRepair && <Btn icon="fix" onClick={() => be.startRepair(job.id)}>Let an agent fix it</Btn>}
        {job.kind === 'reinstall' || job.kind === 'restore'
          ? <Confirm label="Retry" icon="retry" verb={job.kind === 'reinstall' ? 'Reinstall again' : 'Restore again'} sm={false} onYes={() => be.retryJob(job.id)} />
          : <Btn icon="retry" onClick={() => be.retryJob(job.id)}>Retry</Btn>}
      </div>
    );
  }
  const word = jobWord(job);
  const ch = s.prefs.channel;
  return (
    <div class="blk job" aria-label={`${word} ${vm.name}`}>
      <div class="bh">
        <Icon n={job.kind === 'update' ? 'up' : job.kind === 'reinstall' ? 'wipe' : 'repro'} />
        <b>{word}</b>
        <span class="m">{job.pending ? '' : job.kind === 'update' && job.from && job.to ? `${shortVersion(job.from, ch)} → ${shortVersion(job.to, ch)}` : `${job.pct}%`}</span>
      </div>
      <Bar pct={job.pct} />
      {job.repair?.state === 'done' && <div class="msg okc">{job.repair.agent}: {job.repair.outcome}</div>}
      <div class="bf"><span class="lbl">{job.step}</span>{!job.pending && <><Btn sm onClick={() => setLog(!log)}>Log</Btn><Btn sm onClick={() => be.stopJob(job.id)}>Stop</Btn></>}</div>
      {log && <LogView key={job.id} job={job} />}
    </div>
  );
}

/** A job that succeeded although optional steps failed (a forward that did not open): what failed, its log, put away
    with the cross. Calm: nothing to fix before the VM is used. */
export function WarnedBlock({ job }: { job: Job }) {
  const { be } = useApp();
  const [log, setLog] = useState(false);
  const n = job.warnings?.length ?? 0;
  return (
    <div class="blk job" aria-label={`${doneWord(job)} with warnings`}>
      <div class="bh"><Icon n="check" /><b>{doneWord(job)} · {n === 1 ? '1 warning' : `${n} warnings`}</b>
        <Btn sm icon="log" onClick={() => setLog(!log)}>Log</Btn>
        <Btn sm icon="x" title="Dismiss" onClick={() => be.dismissJob(job.id)} /></div>
      <div class="msg">{job.warnings?.join(' · ')}</div>
      {log && <LogView key={job.id} job={job} />}
    </div>
  );
}

/** A coding agent's own sign-in on the VM: its terminal's text, the links in it opened in the browser, one field
    for what it asks (Enter sends, also empty; the arrow keys move a list it shows), Cancel. While the sign-in goes on
    in the browser (this device forwards its redirect), one line and Cancel; the terminal stays behind Terminal. */
export function SignInBlock({ si }: { si: SignIn }) {
  if (si.state === 'key') return <OpenRouterKey si={si} />;
  return <SignInTerminal si={si} />;
}

/** OpenCode's sign-in starts with an OpenRouter API key (11.8); OpenCode's own list of providers stays one click away.
    The key leaves the field when it is sent. */
function OpenRouterKey({ si }: { si: SignIn }) {
  const { be } = useApp();
  const [key, setKey] = useState('');
  const go = () => { const k = key.trim(); setKey(''); if (k) be.signInWithKey(si.vmId, k); };
  return (
    <div class="blk signin" role="group" aria-label="Sign in to OpenCode">
      <div class="bh"><Icon n="key" /><b>OpenCode</b><Btn sm onClick={() => be.closeSignIn()}>Cancel</Btn></div>
      <Field type="password" value={key} onInput={setKey} label="OpenRouter API key" placeholder="OpenRouter API key" mono wide onEnter={go} />
      <div class="two">
        <Btn onClick={() => be.signIn(si.vmId, 'opencode', 'list')}>Other providers</Btn>
        <Btn kind="pri" disabled={!key.trim()} onClick={go}>Sign in</Btn>
      </div>
    </div>
  );
}

function SignInTerminal({ si }: { si: SignIn }) {
  const { be } = useApp();
  const [text, setText] = useState('');
  const term = useFollow<HTMLPreElement>(si.text);
  const name = AGENTS.find(a => a.id === si.agent)?.name ?? si.agent;
  const running = si.state === 'running' || si.state === 'starting';
  const send = () => { be.signInInput({ text }); setText(''); };
  const keys = (e: KeyboardEvent) => {
    if (e.key === 'Enter') { e.preventDefault(); send(); }
    else if (!text && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) { e.preventDefault(); be.signInInput({ key: e.key === 'ArrowUp' ? 'up' : 'down' }); }
  };
  const open = (url: string) => (e: MouseEvent) => { e.preventDefault(); openExternal(window, url); };
  const terminal = (
    <>
      <pre class="term" ref={term.ref} onScroll={term.onScroll} aria-live="polite">
        {linkParts(si.text).map(p => ('url' in p ? <a href={p.url} target="_blank" rel="noopener noreferrer" onClick={open(p.url)}>{p.url}</a> : p.text))}
        {si.state === 'starting' && <span class="typing" aria-label="starting" />}
      </pre>
      {si.state === 'running' && (
        <div class="reply">
          <input class="in m" value={text} placeholder="Reply" aria-label={`Reply to ${name}`} autoComplete="off" spellcheck={false}
            onInput={e => setText(e.currentTarget.value)} onKeyDown={keys} />
          <Btn kind="pri" icon="send" title="Send" onClick={send} />
        </div>
      )}
    </>
  );
  return (
    <div class={`blk signin ${running ? '' : 'bad'}`} role="group" aria-label={`Sign in to ${name}`}>
      <div class="bh"><Icon n="key" /><b>{name}</b><Btn sm onClick={() => be.closeSignIn()}>{running ? 'Cancel' : 'Close'}</Btn></div>
      {si.browser && si.state === 'running' ? (
        <>
          <div class="msg"><span class="typing">Continue in your browser</span></div>
          <details class="more-term"><summary><Icon n="term" />Terminal</summary>{terminal}</details>
        </>
      ) : terminal}
    </div>
  );
}

export function ReinstallConfirm({ vm, onCancel }: { vm: Vm; onCancel: () => void }) {
  const { be } = useApp();
  const [fresh, setFresh] = useState(false);
  return (
    <div class="blk bad job">
      <div class="bh"><Icon n="wipe" /><b>Wipe {vm.name}?</b></div>
      <div class="bf"><span>Fresh image</span><Switch on={fresh} onChange={setFresh} label="Fresh image" /></div>
      <div class="two">
        <Btn onClick={onCancel}>Cancel</Btn>
        <Btn kind="red" onClick={() => { be.reinstall(vm.id, fresh); onCancel(); }}>Reinstall</Btn>
      </div>
    </div>
  );
}

/** "Only this VM", the secondary choice of Update. */
function UpdateMore({ vm }: { vm: Vm }) {
  const { be } = useApp();
  return (
    <details class="more">
      <summary aria-label="More update choices"><Icon n="down" /></summary>
      <div class="vml"><button type="button" onClick={e => { (e.currentTarget.closest('details') as HTMLDetailsElement).open = false; be.updateVm(vm.id); }}>Only {vm.name}</button></div>
    </details>
  );
}

/** The two large tiles of the stack: Reprovision (or Update) and Reinstall. */
export function Tiles({ vm, onReinstall }: { vm: Vm; onReinstall: () => void }) {
  const { be, s } = useApp();
  const a = mainAction(s, vm);
  const v = versionParts(a.kind === 'update' ? a.to : '', s.prefs.channel);
  return (
    <div class="two">
      {a.kind === 'update' ? (
        <div class="tilewrap">
          <button type="button" class="b tile" onClick={() => be.updateEverything()}>
            <span class="upv" title={a.to}><Icon n="up" /><span>{v.base}</span></span>{v.build && <span class="upb">{v.build}</span>}<span>Update</span>
          </button>
          <UpdateMore vm={vm} />
        </div>
      ) : (
        <button type="button" class="b tile" onClick={() => be.reprovision(vm.id)} title={a.waits ? `${a.waits} changed` : undefined}>
          <Icon n="repro" /><span>Reprovision</span>{a.waits && <Pip />}
        </button>
      )}
      <button type="button" class="b tile" onClick={onReinstall} disabled={na(s, 'reinstall') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}><Icon n="wipe" /><span>Reinstall</span></button>
    </div>
  );
}

/** The same two actions as one-line buttons, for the window's overview. */
export function ActionButtons({ vm, busy, onReinstall }: { vm: Vm; busy: boolean; onReinstall: () => void }) {
  const { be, s } = useApp();
  const a = mainAction(s, vm);
  return (
    <>
      {a.kind === 'update' ? (
        <span class="split">
          <Btn icon="up" onClick={() => be.updateEverything()} disabled={busy}>Update<UpTag v={shortVersion(a.to, s.prefs.channel)} /></Btn>
          <UpdateMore vm={vm} />
        </span>
      ) : (
        <button type="button" class="b" onClick={() => be.reprovision(vm.id)} disabled={busy} title={a.waits ? `${a.waits} changed` : undefined}>
          <Icon n="repro" />Reprovision{a.waits && !busy && <Pip />}
        </button>
      )}
      <Btn icon="wipe" onClick={onReinstall} disabled={busy || na(s, 'reinstall') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}>Reinstall</Btn>
    </>
  );
}

/** The VM's power button; while the VM is off it is the coloured Start, the one thing to do first. */
export function PowerBtn({ vm }: { vm: Vm }) {
  const { be } = useApp();
  const on = vm.power === 'on' || vm.power === 'starting';
  const busy = vm.power === 'starting' || vm.power === 'stopping';
  const start = vm.power === 'off' && vm.powerKnown !== false;
  return (
    <button type="button" class={`ic ${on ? 'lit' : ''} ${start ? 'go' : ''}`} disabled={busy} title={on ? 'Shut down' : 'Start'} aria-label={on ? 'Shut down' : 'Start'} onClick={() => be.power(vm.id, !on)}>
      <Icon n="power" />{start && <span>Start</span>}
    </button>
  );
}

/** What a VM that does not run says on the actions that need it running. */
export const NEEDS_RUNNING = 'Start the VM first';
