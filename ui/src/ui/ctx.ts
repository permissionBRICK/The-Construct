import { createContext } from 'preact';
import { useContext } from 'preact/hooks';
import type { Backend, State, Vm } from '../backend/types';
import type { Route } from '../model/route';

export type Surface = 'pop' | 'win' | 'phone' | 'alone';

/** What the app shell provides beyond the backend: open the full window (from the pop-out). */
export interface Shell {
  openWindow(route: Route): void;
  /** Close the pop-out or the lone vault card (desktop shell); a no-op elsewhere. */
  close(): void;
}

export interface Ctx {
  be: Backend;
  s: State;
  surf: Surface;
  route: Route;
  go(r: Route): void;
  vm: Vm | null;
  selectVm(id: string): void;
  shell: Shell;
  /** A short message at the bottom, gone after two seconds. */
  toast(text: string): void;
}

export const AppCtx = createContext<Ctx>(null as unknown as Ctx);
export const useApp = () => useContext(AppCtx);

/** Copy to the clipboard; ignored where the page has no clipboard access. */
export function copy(text: string) {
  try { void navigator.clipboard?.writeText(text).catch(() => {}); } catch { /* no clipboard */ }
}
