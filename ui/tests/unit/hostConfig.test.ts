import { describe, expect, it } from 'vitest';
import {
  buildConfigPayload,
  channelChoice,
  HOST_CONFIG_SPEC_BY_KEY,
  toFieldDrafts,
  visibleFields,
} from '../../src/model/hostConfig';

describe('hostConfig GUI model', () => {
  it('converts byte fields between GiB/MiB inputs and exact byte counts', () => {
    const spec = HOST_CONFIG_SPEC_BY_KEY.capacity;
    const raw = {
      mode: 'enforce',
      ramHeadroomBytes: null,
      storageHeadroomBytes: 20 * 1024 ** 3,
      cpuBudget: 64,
      maxVcpusPerVm: 16,
      reconcileSeconds: 60,
      orphanReservationTimeoutSeconds: 600,
      extraUnknownKey: 'keep-me',
    };
    const fields = visibleFields(spec, 'Hyper-V', raw);
    const drafts = toFieldDrafts(fields, raw);
    expect(drafts.ramHeadroomBytes).toBe('');
    expect(drafts.storageHeadroomBytes).toBe('20');
    expect(drafts.mode).toBe('enforce');

    const res = buildConfigPayload(spec, fields, raw, {
      ...drafts,
      mode: 'observe',
      ramHeadroomBytes: '4',
      storageHeadroomBytes: '32',
      cpuBudget: '',
    });
    expect(res.error).toBeNull();
    expect(res.value).toEqual({
      mode: 'observe',
      ramHeadroomBytes: 4 * 1024 ** 3,
      storageHeadroomBytes: 32 * 1024 ** 3,
      cpuBudget: null,
      maxVcpusPerVm: 16,
      reconcileSeconds: 60,
      orphanReservationTimeoutSeconds: 600,
      extraUnknownKey: 'keep-me',
    });
  });

  it('enforces cross-field rules on memoryPressure and backups', () => {
    const mpSpec = HOST_CONFIG_SPEC_BY_KEY.memoryPressure;
    const mpRaw = { enabled: true, highWaterPercent: 90, lowWaterPercent: 80, swapHighWaterPercent: 50, minSecondsBetweenSaves: 60, cooldownMinutesAfterSave: 10 };
    const mpFields = visibleFields(mpSpec, 'Proxmox', mpRaw);
    const mpDrafts = toFieldDrafts(mpFields, mpRaw);
    expect(buildConfigPayload(mpSpec, mpFields, mpRaw, { ...mpDrafts, lowWaterPercent: '95' }).error).toMatch(/lower than high water/i);

    const bkSpec = HOST_CONFIG_SPEC_BY_KEY.backups;
    const bkRaw = { enabled: true, intervalHours: 24, keep: 4, maxKeep: 10, pinDays: 14, retainDeletedDays: 14, minFreeBytes: 5 * 1024 ** 3, maxBytesPerVm: null };
    const bkFields = visibleFields(bkSpec, 'Hyper-V', bkRaw);
    const bkDrafts = toFieldDrafts(bkFields, bkRaw);
    expect(buildConfigPayload(bkSpec, bkFields, bkRaw, { ...bkDrafts, keep: '12' }).error).toMatch(/cannot exceed/i);
    expect(buildConfigPayload(bkSpec, bkFields, bkRaw, { ...bkDrafts, maxBytesPerVm: '0.01' }).error).toMatch(/at least/i);
  });

  it('hides Proxmox-only fields on Hyper-V hosts and shows them on Proxmox hosts', () => {
    const netSpec = HOST_CONFIG_SPEC_BY_KEY.network;
    const netRaw = { hostForwardsEnabled: true, directAddressReporting: true, defaultMode: 'relayed', ownerMaySwitchMode: false };
    expect(visibleFields(netSpec, 'Hyper-V', netRaw).map(f => f.key)).toEqual(['hostForwardsEnabled', 'directAddressReporting']);
    expect(visibleFields(netSpec, 'Proxmox', netRaw).map(f => f.key)).toEqual(['hostForwardsEnabled', 'directAddressReporting', 'defaultMode', 'ownerMaySwitchMode']);

    const virtSpec = HOST_CONFIG_SPEC_BY_KEY.virtualization;
    const virtRaw = { nestedDefault: true, nestedSelectable: true, minimumRamBytes: 6 * 1024 ** 3 };
    expect(visibleFields(virtSpec, 'Hyper-V', virtRaw).map(f => f.key)).toEqual(['nestedDefault', 'nestedSelectable']);
    expect(visibleFields(virtSpec, 'Proxmox', virtRaw).map(f => f.key)).toEqual(['nestedDefault', 'nestedSelectable', 'minimumRamBytes']);
  });

  it('handles update channels (stable, legacy main, nightly, and custom https URL)', () => {
    expect(channelChoice('main')).toBe('stable');
    expect(channelChoice('stable')).toBe('stable');
    expect(channelChoice('nightly')).toBe('nightly');
    expect(channelChoice('https://mirror.example.net/manifest.json')).toBe('custom');

    const spec = HOST_CONFIG_SPEC_BY_KEY.updates;
    const raw = { repository: 'permissionBRICK/The-Construct', channel: 'main', auto: false, drainTimeoutMinutes: 60, healthTimeoutSeconds: 120 };
    const fields = visibleFields(spec, 'Hyper-V', raw);
    const drafts = toFieldDrafts(fields, raw);
    expect(drafts.channel).toBe('stable');

    expect(buildConfigPayload(spec, fields, raw, { ...drafts, channel: 'http://insecure.example.net/manifest.json' }).error).toMatch(/https:\/\//i);
    const ok = buildConfigPayload(spec, fields, raw, { ...drafts, channel: 'https://mirror.example.net/manifest.json', auto: true });
    expect(ok.error).toBeNull();
    expect(ok.value?.channel).toBe('https://mirror.example.net/manifest.json');
    expect(ok.value?.auto).toBe(true);
  });

  it('supports nullable booleans in userCaps and https URL validation in vault', () => {
    const capsSpec = HOST_CONFIG_SPEC_BY_KEY.userCaps;
    const capsRaw = { maxRetainedChildren: null, cpuBudget: null, ramBudgetBytes: null, storageBudgetBytes: null, maxChildLifetimeSeconds: null, allowNeverLifetime: null, allowSharing: true };
    const capsFields = visibleFields(capsSpec, 'Proxmox', capsRaw);
    const capsDrafts = toFieldDrafts(capsFields, capsRaw);
    expect(capsDrafts.allowNeverLifetime).toBeNull();
    expect(capsDrafts.allowSharing).toBe(true);
    const res = buildConfigPayload(capsSpec, capsFields, capsRaw, { ...capsDrafts, allowNeverLifetime: false, allowSharing: null });
    expect(res.error).toBeNull();
    expect(res.value?.allowNeverLifetime).toBe(false);
    expect(res.value?.allowSharing).toBeNull();

    const vaultSpec = HOST_CONFIG_SPEC_BY_KEY.vault;
    const vaultRaw = { webUrl: null };
    const vaultFields = visibleFields(vaultSpec, 'Proxmox', vaultRaw);
    expect(buildConfigPayload(vaultSpec, vaultFields, vaultRaw, { webUrl: 'http://bad.example.net' }).error).toMatch(/https:\/\//i);
    expect(buildConfigPayload(vaultSpec, vaultFields, vaultRaw, { webUrl: 'https://vault.example.net' }).value).toEqual({ webUrl: 'https://vault.example.net' });
  });
});
