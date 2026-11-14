/* Routes live in the URL hash so a page can be linked and reloaded: #/projects, #/project/webshop. */

export type Page =
  | 'home' | 'projects' | 'project' | 'file' | 'connections' | 'backups' | 'settings'
  | 'vault' | 'access' | 'devices' | 'pair' | 'revoke' | 'history'
  | 'hosts' | 'host' | 'app' | 'create' | 'upgrade';

export interface Route { page: Page; arg?: string; tab?: string }

const PAGES = new Set<Page>(['home', 'projects', 'project', 'file', 'connections', 'backups', 'settings', 'vault', 'access', 'devices', 'pair', 'revoke', 'history', 'hosts', 'host', 'app', 'create', 'upgrade']);

export function parseRoute(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean).map(decodeURIComponent);
  const page = parts[0] as Page;
  if (!PAGES.has(page)) return { page: 'home' };
  const r: Route = { page };
  if (parts[1]) r.arg = parts[1];
  if (parts[2]) r.tab = parts[2];
  return r;
}

export function formatRoute(r: Route): string {
  return '#/' + [r.page, r.arg, r.tab].filter(Boolean).map(s => encodeURIComponent(s as string)).join('/');
}

/** Which sidebar entry a page belongs to. */
export type Section = 'home' | 'projects' | 'connections' | 'backups' | 'settings' | 'vault' | 'hosts' | 'app';
export function section(p: Page): Section {
  switch (p) {
    case 'project': case 'file': return 'projects';
    case 'access': case 'devices': case 'pair': case 'revoke': case 'history': return 'vault';
    case 'host': return 'hosts';
    case 'create': case 'upgrade': return 'home';
    default: return p as Section;
  }
}

/** Pages that belong to one VM; switching VM keeps the page. */
export const VM_PAGES: Page[] = ['home', 'projects', 'connections', 'backups', 'settings'];
