import { describe, expect, it, vi } from 'vitest';
import type { VNode } from 'preact';
import type { Backend, State, Vm } from '../../src/backend/types';
import { FakeBackend } from '../../src/backend/fake';

let currentCtx: {
  be: Backend;
  s: State;
  surf: 'win';
  route: { page: 'settings'; arg?: string };
  go: () => void;
  vm: Vm | null;
  selectVm: () => void;
  shell: { openWindow: () => void; close: () => void };
  toast: () => void;
};

vi.mock('preact/hooks', () => ({
  useState: <T,>(init: T) => [init, () => {}],
  useContext: () => currentCtx,
}));

vi.mock('../../src/ui/ctx', async () => {
  const actual = await vi.importActual<typeof import('../../src/ui/ctx')>('../../src/ui/ctx');
  return { ...actual, useApp: () => currentCtx };
});

import { AgentActions, Settings } from '../../src/ui/pages/vm';

/** Walks a Preact VNode tree (expanding plain function components) and returns matching elements. */
function findNodes(node: unknown, pred: (v: VNode<Record<string, unknown>>) => boolean, out: VNode<Record<string, unknown>>[] = []): VNode<Record<string, unknown>>[] {
  if (!node || typeof node !== 'object') return out;
  if (Array.isArray(node)) {
    for (const x of node) findNodes(x, pred, out);
    return out;
  }
  const v = node as VNode<Record<string, unknown>>;
  if (typeof v.type === 'function') {
    const rendered = (v.type as (p: Record<string, unknown>) => unknown)(v.props ?? {});
    findNodes(rendered, pred, out);
    return out;
  }
  if (pred(v)) out.push(v);
  if (v.props && 'children' in v.props) findNodes(v.props.children, pred, out);
  return out;
}

function textOf(node: unknown): string {
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(textOf).join('');
  if (node && typeof node === 'object' && 'props' in node) return textOf((node as VNode<{ children?: unknown }>).props?.children);
  return '';
}

function makeCtx(be: Backend, s: State, vm: Vm) {
  return {
    be, s, surf: 'win' as const, route: { page: 'settings' as const, arg: 'agents' },
    go: () => {}, vm, selectVm: () => {}, shell: { openWindow: () => {}, close: () => {} }, toast: () => {},
  };
}

describe('VM Coding agents update buttons', () => {
  it('clicking Update on one coding agent calls updateAgents with only that agent id (and not reprovision)', () => {
    const fake = new FakeBackend({ scene: 'calm', timers: false });
    const s = fake.snapshot();
    const vm = s.vms[0];
    const codex = vm.agents.find(a => a.id === 'codex')!;
    const behindCodex = { ...codex, version: '0.98.0', latest: '0.100.0', newer: true };
    const updateAgents = vi.fn();
    const reprovision = vi.fn();
    const be = { ...fake, updateAgents, reprovision } as unknown as Backend;
    currentCtx = makeCtx(be, s, vm);

    const tree = AgentActions({ vm, a: behindCodex, busy: false });
    const buttons = findNodes(tree, v => v.type === 'button' && textOf(v).includes('Update'));
    expect(buttons).toHaveLength(1);
    (buttons[0].props.onClick as () => void)();

    expect(updateAgents).toHaveBeenCalledTimes(1);
    expect(updateAgents).toHaveBeenCalledWith(vm.id, 'codex');
    expect(reprovision).not.toHaveBeenCalled();
  });

  it('clicking Update all in Settings -> Agents calls updateAgents without a single agent id (and not reprovision)', () => {
    const fake = new FakeBackend({ scene: 'calm', timers: false });
    const base = fake.snapshot();
    const vm: Vm = {
      ...base.vms[0],
      agents: base.vms[0].agents.map(a => (a.id === 'codex' ? { ...a, version: '0.98.0', latest: '0.100.0', newer: true } : a)),
    };
    const s: State = { ...base, vms: [vm, ...base.vms.slice(1)] };
    const updateAgents = vi.fn();
    const reprovision = vi.fn();
    const be = { ...fake, updateAgents, reprovision } as unknown as Backend;
    currentCtx = makeCtx(be, s, vm);

    const tree = Settings({ vm });
    const buttons = findNodes(tree, v => v.type === 'button' && textOf(v).includes('Update all'));
    expect(buttons).toHaveLength(1);
    (buttons[0].props.onClick as () => void)();

    expect(updateAgents).toHaveBeenCalledTimes(1);
    expect(updateAgents).toHaveBeenCalledWith(vm.id);
    expect(reprovision).not.toHaveBeenCalled();
  });
});
