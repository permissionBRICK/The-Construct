import type { VaultFile } from '../backend/types';

/* Files that still hold a secret ask as one card per VM. */

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
