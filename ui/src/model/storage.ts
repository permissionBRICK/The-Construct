/* Helpers for Windows Hyper-V VM storage paths (host default directory and individual VM disk moves). */

export const DEFAULT_WINDOWS_DISK_DIR = 'C:\\ProgramData\\Microsoft\\Windows\\Virtual Hard Disks';

const INVALID_CHARS = /[\x00-\x1f"<>|?*]/;

/** Returns a human-readable validation error for a Windows folder path, or null when acceptable. */
export function windowsDirError(path: string, allowEmpty = true): string | null {
  const v = path.trim();
  if (!v) return allowEmpty ? null : 'Enter an absolute Windows folder path (e.g. D:\\ConstructVMs).';
  if (v.length < 3 || v.length > 4096 || !/^[A-Za-z]:[\\/]/.test(v)) {
    return 'Use an absolute Windows folder path on a drive letter (e.g. D:\\ConstructVMs).';
  }
  if (INVALID_CHARS.test(v)) return 'Path must not contain quotes, wildcards or control characters.';
  const rest = v.slice(3).replace(/\//g, '\\');
  if (rest.includes(':')) return 'Path must not contain extra colons.';
  for (const seg of rest.split('\\').filter(Boolean)) {
    if (seg === '.' || seg === '..' || seg.endsWith('.') || seg.endsWith(' ')) {
      return 'Path must not contain relative segments or trailing dots/spaces.';
    }
  }
  return null;
}

/** Normalizes a valid Windows directory path to backslashes without a trailing slash (except `D:\`). */
export function normalizeWindowsDir(path: string): string {
  const v = path.trim().replace(/\//g, '\\');
  const drive = v[0].toUpperCase() + ':\\';
  const parts = v.slice(3).split('\\').filter(Boolean);
  return parts.length === 0 ? drive : drive + parts.join('\\');
}

/** Extracts the normalized parent directory of a Windows file path, or null if not a Windows path. */
export function dirOfWindowsPath(filePath: string | null | undefined): string | null {
  if (!filePath) return null;
  const v = filePath.trim().replace(/\//g, '\\');
  if (!/^[A-Za-z]:\\/.test(v)) return null;
  const idx = v.lastIndexOf('\\');
  return idx <= 2 ? v[0].toUpperCase() + ':\\' : normalizeWindowsDir(v.slice(0, idx));
}

/** Case-insensitive directory comparison for Windows paths. */
export function sameWindowsDir(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return false;
  return normalizeWindowsDir(a).toLowerCase() === normalizeWindowsDir(b).toLowerCase();
}
