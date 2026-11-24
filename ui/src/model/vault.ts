import type { VaultFile, VaultRequest } from '../backend/types';

/* Files that still hold a secret ask as one card per VM; the lone tray card skips requests shown inline elsewhere. */

/** The next vault request for the lone tray card: skips requests already shown in another app (e.g. T3 Code) or handled here. */
export function nextAloneRequest(requests: VaultRequest[], gone: readonly string[]): VaultRequest | undefined {
  return requests.find(x => !x.displayedElsewhere && !gone.includes('v:' + x.id));
}

/** The files grouped by the VM that holds them, in the order the first file of each VM came. */
export function filesByVm(files: VaultFile[]): VaultFile[][] {
  const groups = new Map<string, VaultFile[]>();
  for (const f of files) {
    const key = `${f.hostId}/${f.vmName.toLowerCase()}`;
    const group = groups.get(key);
    if (group) group.push(f);
    else groups.set(key, [f]);
  }
  return [...groups.values()];
}
