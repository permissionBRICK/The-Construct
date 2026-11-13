import type { Profile } from '../backend/types';

/** Why a parsed project profile cannot be saved, or null when its shape is right. */
export function profileError(o: unknown): string | null {
  if (!o || typeof o !== 'object' || Array.isArray(o)) return 'A profile is a JSON object';
  const p = o as Partial<Record<keyof Profile, unknown>>;
  const strs = (x: unknown) => Array.isArray(x) && x.every(v => typeof v === 'string');
  if (typeof p.name !== 'string' || !p.name) return '"name" must be a string';
  if (!Array.isArray(p.repos) || !p.repos.every(r => r && typeof r.url === 'string' && typeof r.directory === 'string')) return '"repos" must be a list of { url, directory }';
  if (!p.sdks || typeof p.sdks !== 'object' || Array.isArray(p.sdks) || !Object.values(p.sdks).every(v => typeof v === 'string')) return '"sdks" must map names to versions';
  if (!Array.isArray(p.mcp) || !p.mcp.every(m => m && typeof m.name === 'string' && typeof m.type === 'string' && typeof m.command === 'string' && strs(m.args))) return '"mcp" must be a list of { name, type, command, args }';
  if (!strs(p.hostPackages)) return '"hostPackages" must be a list of strings';
  if (!strs(p.provisionCommands)) return '"provisionCommands" must be a list of strings';
  if (!Array.isArray(p.forwards) || !p.forwards.every(f => f && Number.isInteger(f.port) && typeof f.label === 'string')) return '"forwards" must be a list of { port, label }';
  return null;
}
