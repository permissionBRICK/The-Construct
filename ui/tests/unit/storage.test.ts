import { describe, expect, it } from 'vitest';
import { DEFAULT_WINDOWS_DISK_DIR, dirOfWindowsPath, normalizeWindowsDir, sameWindowsDir, windowsDirError } from '../../src/model/storage';

describe('storage path helpers', () => {
  it('validates Windows directory paths', () => {
    expect(windowsDirError('', true)).toBeNull();
    expect(windowsDirError('', false)).not.toBeNull();
    expect(windowsDirError('D:\\ConstructVMs', false)).toBeNull();
    expect(windowsDirError('d:/VMs/', false)).toBeNull();
    expect(windowsDirError('D:\\', false)).toBeNull();
    expect(windowsDirError('relative/path', false)).not.toBeNull();
    expect(windowsDirError('\\\\server\\share', false)).not.toBeNull();
    expect(windowsDirError('D:\\VMs\\..\\Other', false)).not.toBeNull();
    expect(windowsDirError('D:\\VMs"bad', false)).not.toBeNull();
    expect(windowsDirError('D:\\VMs\\*', false)).not.toBeNull();
  });

  it('normalizes and compares directories', () => {
    expect(normalizeWindowsDir('d:/ConstructVMs/')).toBe('D:\\ConstructVMs');
    expect(normalizeWindowsDir('d:/')).toBe('D:\\');
    expect(dirOfWindowsPath(`${DEFAULT_WINDOWS_DISK_DIR}\\atlas.vhdx`)).toBe(DEFAULT_WINDOWS_DISK_DIR);
    expect(dirOfWindowsPath('D:\\atlas.vhdx')).toBe('D:\\');
    expect(sameWindowsDir('d:/ConstructVMs/', 'D:\\constructvms')).toBe(true);
    expect(sameWindowsDir('D:\\A', 'D:\\B')).toBe(false);
  });
});
