import { useEffect, useRef, useState } from 'preact/hooks';
import type { Job, VaultRequest, Vm } from '../backend/types';
import { failedTitle, jobWord, mainAction } from '../model/vm';
import { Icon } from './icons';
import { Bar, Btn, Field, Pip, Switch, UpTag } from './kit';
import { copy, useApp } from './ctx';

/* The blocks that sit on the stack, in the pop-out, the window's overview and the phone. */

/** A vault request: the only thing in the product that asks for attention. Yellow. */
export function VaultCard({ r, onDone }: { r: VaultRequest; onDone?: () => void }) {
  const { be } = useApp();
  const [value, setValue] = useState('');
  const done = () => onDone?.();
  return (
    <div class="blk warn ask" role="alertdialog" aria-label={`Vault request for ${r.secret}`}>
      <div class="bh"><Icon n="key" /><b>{r.secret}</b><span>{r.vmName}</span></div>
      <div class="msg quote">{r.reason}</div>
      {r.kind === 'enter' ? (
        <>
          <Field type="password" value={value} onInput={setValue} label="Value" placeholder="Value" onEnter={() => { if (value) { be.enterSecret(r.id, value); done(); } }} />
          <div class="two">
            <Btn onClick={() => { be.enterSecret(r.id, null); done(); }}>Skip</Btn>
            <Btn kind="pri" disabled={!value} onClick={() => { be.enterSecret(r.id, value); done(); }}>Save</Btn>
          </div>
        </>
      ) : (
        <div class="two">
          <Btn onClick={() => { be.answerLease(r.id, false); done(); }}>Deny</Btn>
          <Btn kind="pri" onClick={() => { be.answerLease(r.id, true); done(); }}>Allow {r.span}</Btn>
        </div>
      )}
    </div>
  );
}

export function VaultCards() {
  const { s } = useApp();
  return <>{s.vaultRequests.map(r => <VaultCard key={r.id} r={r} />)}</>;
}

function LogView({ job }: { job: Job }) {
  const { toast } = useApp();
  const text = job.log.join('\n');
  return (
    <div class="logbox">
      <pre class="code">{job.log.map(l => <span class="l">{l}</span>)}</pre>
      <div class="bf"><span /><Btn sm icon="copy" onClick={() => { copy(text); toast('Copied'); }}>Copy</Btn></div>
    </div>
  );
}

/** The repair agent under a failed job: its output and one reply box. */
function Repair({ job }: { job: Job }) {
  const { be } = useApp();
  const r = job.repair!;
  const [text, setText] = useState('');
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => { end.current?.scrollIntoView?.({ block: 'nearest' }); }, [r.messages.length]);
  const send = () => { if (text.trim()) { be.replyRepair(job.id, text); setText(''); } };
  return (
    <div class="repair">
      <div class="bh"><Icon n="fix" /><b>{r.agent}</b>{r.t3Thread && <Btn sm icon="open" onClick={() => be.open(job.vmId, 't3')}>T3 Code</Btn>}</div>
      <div class="chat" aria-live="polite">
        {r.messages.map(m => <p class={m.from}>{m.text}</p>)}
        {r.state === 'working' && <p class="agent typing" aria-label="working" />}
        <div ref={end} />
      </div>
      {r.state !== 'done' && (
        <div class="reply">
          <Field value={text} onInput={setText} label="Reply" placeholder="Reply" onEnter={send} wide />
          <Btn kind={r.state === 'asking' ? 'pri' : ''} icon="send" title="Send" onClick={send} disabled={!text.trim()} />
        </div>
      )}
    </div>
  );
}

export function JobBlock({ job, vm }: { job: Job; vm: Vm }) {
  const { be } = useApp();
  const [log, setLog] = useState(false);
  if (job.state === 'failed') {
    return (
      <div class="blk bad job">
        <div class="bh"><Icon n="x" /><b>{failedTitle(job)}</b><Btn sm icon="log" onClick={() => setLog(!log)}>Log</Btn></div>
        <div class="msg">{job.step}: {job.error}</div>
        {(log || !job.repair) && <LogView job={job} />}
        {job.repair && <Repair job={job} />}
        <div class="two">
          <Btn icon="retry" onClick={() => be.retryJob(job.id)}>Retry</Btn>
          <Btn onClick={() => be.dismissJob(job.id)}>Close</Btn>
        </div>
      </div>
    );
  }
  const word = jobWord(job);
  return (
    <div class="blk job" aria-label={`${word} ${vm.name}`}>
      <div class="bh">
        <Icon n={job.kind === 'update' ? 'up' : job.kind === 'reinstall' ? 'wipe' : 'repro'} />
        <b>{word}</b>
        <span class="m">{job.kind === 'update' ? `${job.from} → ${job.to}` : `${job.stepIndex + 1} / ${job.stepCount} · ${job.pct}%`}</span>
      </div>
      <Bar pct={job.pct} />
      <div class="bf"><span>{job.step}</span><Btn sm onClick={() => setLog(!log)}>Log</Btn><Btn sm onClick={() => be.stopJob(job.id)}>Stop</Btn></div>
      {log && <LogView job={job} />}
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
  return (
    <div class="two">
      {a.kind === 'update' ? (
        <div class="tilewrap">
          <button type="button" class="b tile" onClick={() => be.updateEverything()}>
            <span class="upv"><Icon n="up" />{a.to}</span><span>Update</span>
          </button>
          <UpdateMore vm={vm} />
        </div>
      ) : (
        <button type="button" class="b tile" onClick={() => be.reprovision(vm.id)} title={a.waits ? `${a.waits} changed` : undefined}>
          <Icon n="repro" /><span>Reprovision</span>{a.waits && <Pip />}
        </button>
      )}
      <button type="button" class="b tile" onClick={onReinstall}><Icon n="wipe" /><span>Reinstall</span></button>
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
          <Btn icon="up" onClick={() => be.updateEverything()} disabled={busy}>Update<UpTag v={a.to} /></Btn>
          <UpdateMore vm={vm} />
        </span>
      ) : (
        <button type="button" class="b" onClick={() => be.reprovision(vm.id)} disabled={busy} title={a.waits ? `${a.waits} changed` : undefined}>
          <Icon n="repro" />Reprovision{a.waits && !busy && <Pip />}
        </button>
      )}
      <Btn icon="wipe" onClick={onReinstall} disabled={busy}>Reinstall</Btn>
    </>
  );
}

export function PowerBtn({ vm }: { vm: Vm }) {
  const { be } = useApp();
  const on = vm.power === 'on' || vm.power === 'starting';
  const busy = vm.power === 'starting' || vm.power === 'stopping';
  return (
    <button type="button" class={`ic ${on ? 'lit' : ''}`} disabled={busy} title={on ? 'Shut down' : 'Start'} aria-label={on ? 'Shut down' : 'Start'} onClick={() => be.power(vm.id, !on)}>
      <Icon n="power" />
    </button>
  );
}
