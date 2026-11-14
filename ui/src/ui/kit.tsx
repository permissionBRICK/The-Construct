import type { ComponentChildren, JSX } from 'preact';
import { useState } from 'preact/hooks';
import { Icon, type IconName } from './icons';
import type { Dot as DotKind } from '../model/vm';
import { copy, useApp } from './ctx';

/* Small building blocks. Every colour, radius and face comes from the tokens. */

type BtnProps = {
  children?: ComponentChildren;
  icon?: IconName;
  kind?: '' | 'pri' | 'red' | 'amber';
  sm?: boolean;
  onClick?: () => void;
  disabled?: boolean;
  title?: string;
  class?: string;
  type?: 'button' | 'submit';
};
export function Btn({ children, icon, kind = '', sm, onClick, disabled, title, class: cls = '', type = 'button' }: BtnProps) {
  return (
    <button type={type} class={`b ${kind} ${sm ? 'sm' : ''} ${cls}`} onClick={onClick} disabled={disabled} title={title} aria-label={!children ? title : undefined}>
      {icon && <Icon n={icon} />}
      {children}
    </button>
  );
}

export function IconBtn({ icon, title, onClick, lit, disabled }: { icon: IconName; title: string; onClick?: () => void; lit?: boolean; disabled?: boolean }) {
  return <button type="button" class={`ic ${lit ? 'lit' : ''}`} title={title} aria-label={title} onClick={onClick} disabled={disabled}><Icon n={icon} /></button>;
}

export function Switch({ on, onChange, label, disabled }: { on: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean }) {
  return <button type="button" role="switch" class={`sw ${on ? 'on' : ''}`} aria-checked={on} aria-label={label} disabled={disabled} onClick={() => onChange(!on)} />;
}

export function Seg<T extends string | number>({ opts, value, onChange, label, labels }: { opts: readonly T[]; value: T; onChange: (v: T) => void; label: string; labels?: Partial<Record<string, string>> }) {
  return (
    <span class="seg" role="radiogroup" aria-label={label}>
      {opts.map(o => (
        <button type="button" role="radio" aria-checked={o === value} class={o === value ? 'on' : ''} onClick={() => onChange(o)}>{labels?.[String(o)] ?? String(o)}</button>
      ))}
    </span>
  );
}

export const Tag = ({ children, warn }: { children: ComponentChildren; warn?: boolean }) => <em class={`tg ${warn ? 'warn' : ''}`}>{children}</em>;
export const UpTag = ({ v }: { v: string }) => <em class="tg up"><Icon n="up" />{v}</em>;
export const Pip = () => <i class="pip" aria-label="waiting" />;
export const Dot = ({ s }: { s: DotKind }) => <i class={`dot ${s}`} aria-hidden="true" />;

export function Sec({ title, right, children, class: cls = '' }: { title?: ComponentChildren; right?: ComponentChildren; children: ComponentChildren; class?: string }) {
  return (
    <section class={`sec ${cls}`}>
      {(title || right) && <div class="cap"><span>{title}</span>{right}</div>}
      <div class="sb">{children}</div>
    </section>
  );
}

export const Row = ({ l, children }: { l: ComponentChildren; children?: ComponentChildren }) => (
  <div class="r"><span class="rl">{l}</span><span class="rr">{children}</span></div>
);
export const KV = ({ k, children }: { k: ComponentChildren; children: ComponentChildren }) => (
  <div class="r"><span class="rl d">{k}</span><span class="rr rv">{children}</span></div>
);

export type Col = { h: ComponentChildren; cls?: string };
export function Table({ cols, rows, onRow, empty = 'None' }: { cols: Col[]; rows: ComponentChildren[][]; onRow?: (i: number) => void; empty?: string }) {
  if (!rows.length) return <div class="line">{empty}</div>;
  return (
    <table class="tb">
      <thead><tr>{cols.map(c => <th class={c.cls}>{c.h}</th>)}</tr></thead>
      <tbody>
        {rows.map((r, i) => (
          <tr class={onRow ? 'go' : ''} onClick={onRow ? () => onRow(i) : undefined} tabIndex={onRow ? 0 : undefined}
            onKeyDown={onRow ? (e: KeyboardEvent) => { if (e.key === 'Enter') onRow(i); } : undefined}>
            {r.map((c, j) => <td class={cols[j]?.cls}>{c}</td>)}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function Tabs<T extends string>({ items, value, onChange }: { items: [T, string][]; value: T; onChange: (v: T) => void }) {
  return (
    <div class="tabs" role="tablist">
      {items.map(([id, label]) => <button type="button" role="tab" aria-selected={id === value} class={id === value ? 'on' : ''} onClick={() => onChange(id)}>{label}</button>)}
    </div>
  );
}

/** A page header: title, an optional back button (narrow surfaces), actions on the right. */
export function Head({ title, back, acts }: { title: ComponentChildren; back?: () => void; acts?: ComponentChildren }) {
  return (
    <header class="ph">
      {back && <button type="button" class="ic back" onClick={back} aria-label="Back"><Icon n="left" /></button>}
      <h2>{title}</h2>
      {acts}
    </header>
  );
}

/** A part the backend cannot serve yet, in place of its content. */
export const NotYet = () => <div class="line d">Not available yet</div>;

export const Page = ({ children }: { children: ComponentChildren }) => <section class="pg">{children}</section>;

export const Meter = ({ pct }: { pct: number }) => <span class="meter"><i style={{ width: `${Math.max(0, Math.min(100, pct))}%` }} /></span>;
export const Bar = ({ pct }: { pct: number }) => <div class="bar"><i style={{ width: `${Math.max(0, Math.min(100, pct))}%` }} /></div>;

export function Field({ value, onInput, label, type = 'text', placeholder, mono, wide, onEnter }: {
  value: string; onInput: (v: string) => void; label: string; type?: string; placeholder?: string; mono?: boolean; wide?: boolean; onEnter?: () => void;
}) {
  return (
    <input class={`in ${mono ? 'm' : ''} ${wide ? 'wide' : ''}`} type={type} value={value} placeholder={placeholder} aria-label={label}
      onInput={(e: JSX.TargetedEvent<HTMLInputElement>) => onInput(e.currentTarget.value)}
      onKeyDown={onEnter ? (e: KeyboardEvent) => { if (e.key === 'Enter') onEnter(); } : undefined} />
  );
}

/** An action that asks first, in place: the button turns into "Cancel / <verb>". */
export function Confirm({ label, verb, icon, danger = true, sm = true, onYes, disabled }: { label: string; verb?: string; icon?: IconName; danger?: boolean; sm?: boolean; onYes: () => void; disabled?: boolean }) {
  const [asking, setAsking] = useState(false);
  if (!asking) return <Btn sm={sm} icon={icon} onClick={() => setAsking(true)} disabled={disabled}>{label}</Btn>;
  return (
    <span class="confirm">
      <Btn sm={sm} onClick={() => setAsking(false)}>Cancel</Btn>
      <Btn sm={sm} kind={danger ? 'red' : 'pri'} onClick={() => { setAsking(false); onYes(); }}>{verb ?? label}</Btn>
    </span>
  );
}

/** A value with a copy button. */
export function Copyable({ text }: { text: string }) {
  const { toast } = useApp();
  return (
    <div class="line">
      <code class="grow sel">{text}</code>
      <Btn sm icon="copy" onClick={() => { copy(text); toast('Copied'); }}>Copy</Btn>
    </div>
  );
}
