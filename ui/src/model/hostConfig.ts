/* Typed schema, formatting, and validation for host configuration sections (`GET/PUT /api/v1/host/config`).
   Mirrors `service/src/Constructd.Api/Infrastructure/HostConfigValidation.cs` so the Host -> Configuration
   tab presents native GUI controls instead of raw JSON. */

export type HostConfigField =
  | {
    key: string;
    label: string;
    help: string;
    type: 'bool';
    nullable?: boolean;
    platform?: 'proxmox';
  }
  | {
    key: string;
    label: string;
    help: string;
    type: 'enum';
    options: readonly string[];
    labels?: Record<string, string>;
    platform?: 'proxmox';
  }
  | {
    key: string;
    label: string;
    help: string;
    type: 'channel';
    platform?: 'proxmox';
  }
  | {
    key: string;
    label: string;
    help: string;
    type: 'int' | 'bytes';
    min: number;
    max: number;
    scale?: number;
    unit?: string;
    nullable?: boolean;
    nullLabel?: string;
    platform?: 'proxmox';
  }
  | {
    key: string;
    label: string;
    help: string;
    type: 'string';
    nullable?: boolean;
    nullLabel?: string;
    placeholder?: string;
    pattern?: RegExp;
    patternError?: string;
    httpsUrl?: boolean;
    platform?: 'proxmox';
  };

export interface HostConfigSectionSpec {
  key: string;
  label: string;
  description: string;
  fields: readonly HostConfigField[];
  validate?: (obj: Record<string, unknown>) => string | null;
}

const MIB = 1024 ** 2;
const GIB = 1024 ** 3;
const MAX_INT = 2147483647;
const MAX_SAFE = Number.MAX_SAFE_INTEGER;
const REPO_RE = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/;

/** Sections handled by their own dedicated cards in Host -> Configuration (`HostStoragePath` and Repair switches). */
export const DEDICATED_HOST_CONFIG_SECTIONS = new Set(['storage', 'repair']);

export const HOST_CONFIG_SECTIONS: readonly HostConfigSectionSpec[] = [
  {
    key: 'capacity',
    label: 'Capacity',
    description: 'Resource admission limits and host headroom.',
    fields: [
      {
        key: 'mode',
        label: 'Admission mode',
        help: 'Observe reports capacity; Enforce refuses requests exceeding available capacity.',
        type: 'enum',
        options: ['observe', 'enforce'],
        labels: { observe: 'Observe', enforce: 'Enforce' },
      },
      {
        key: 'ramHeadroomBytes',
        label: 'RAM headroom',
        help: 'RAM kept free for the host. Empty uses automatic headroom.',
        type: 'bytes',
        min: 0,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
        nullable: true,
        nullLabel: 'Automatic',
      },
      {
        key: 'storageHeadroomBytes',
        label: 'Storage headroom',
        help: 'Disk space kept free for the host.',
        type: 'bytes',
        min: 0,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
      },
      {
        key: 'cpuBudget',
        label: 'Host CPU budget',
        help: 'Maximum total active vCPUs across all VMs.',
        type: 'int',
        min: 0,
        max: MAX_INT,
        unit: 'vCPU',
        nullable: true,
        nullLabel: 'No limit',
      },
      {
        key: 'maxVcpusPerVm',
        label: 'Maximum vCPUs per VM',
        help: 'Host ceiling for each VM’s CPU count.',
        type: 'int',
        min: 1,
        max: MAX_INT,
        unit: 'vCPU',
        nullable: true,
        nullLabel: 'No limit',
      },
      {
        key: 'reconcileSeconds',
        label: 'Reconcile interval',
        help: 'How often capacity is reconciled with the hypervisor.',
        type: 'int',
        min: 30,
        max: MAX_INT,
        unit: 's',
      },
      {
        key: 'orphanReservationTimeoutSeconds',
        label: 'Orphan reservation timeout',
        help: 'How long an orphan reservation is kept before cleanup.',
        type: 'int',
        min: 30,
        max: MAX_INT,
        unit: 's',
      },
    ],
  },
  {
    key: 'memoryPressure',
    label: 'Memory pressure',
    description: 'Automatic VM saving when host memory usage gets high.',
    fields: [
      {
        key: 'enabled',
        label: 'Enable memory pressure handling',
        help: 'Save eligible VMs when measured host memory pressure is too high.',
        type: 'bool',
      },
      {
        key: 'highWaterPercent',
        label: 'High water',
        help: 'Start handling pressure above this RAM usage.',
        type: 'int',
        min: 2,
        max: 100,
        unit: '%',
      },
      {
        key: 'lowWaterPercent',
        label: 'Low water',
        help: 'Stop handling pressure below this RAM usage (must be below high water).',
        type: 'int',
        min: 1,
        max: 99,
        unit: '%',
      },
      {
        key: 'swapHighWaterPercent',
        label: 'Swap high water',
        help: 'Swap usage threshold. 0 ignores swap.',
        type: 'int',
        min: 0,
        max: 100,
        unit: '%',
      },
      {
        key: 'minSecondsBetweenSaves',
        label: 'Minimum save interval',
        help: 'Minimum time between pressure-triggered saves.',
        type: 'int',
        min: 1,
        max: MAX_INT,
        unit: 's',
      },
      {
        key: 'cooldownMinutesAfterSave',
        label: 'Cooldown after save',
        help: 'Wait before handling pressure again after saving a VM.',
        type: 'int',
        min: 0,
        max: MAX_INT,
        unit: 'min',
      },
    ],
    validate: obj => {
      const low = obj.lowWaterPercent;
      const high = obj.highWaterPercent;
      if (typeof low === 'number' && typeof high === 'number' && low >= high) {
        return 'Low water must be lower than high water.';
      }
      return null;
    },
  },
  {
    key: 'backups',
    label: 'Config backups',
    description: 'Encrypted VM configuration backups stored on this host.',
    fields: [
      {
        key: 'enabled',
        label: 'Enable config backups',
        help: 'Off refuses new backups while keeping existing ones until rotation.',
        type: 'bool',
      },
      {
        key: 'intervalHours',
        label: 'Backup interval',
        help: 'Hours between scheduled backups. 0 turns scheduled backups off.',
        type: 'int',
        min: 0,
        max: 720,
        unit: 'h',
      },
      {
        key: 'keep',
        label: 'Backups kept per VM',
        help: 'Default number of backups kept per VM (at most the maximum below).',
        type: 'int',
        min: 1,
        max: 50,
      },
      {
        key: 'maxKeep',
        label: 'Maximum backups kept per VM',
        help: 'Highest number of backups an owner may keep per VM.',
        type: 'int',
        min: 1,
        max: 50,
      },
      {
        key: 'pinDays',
        label: 'Keep pre-reinstall backup',
        help: 'Days a pre-reinstall backup is pinned before rotating out.',
        type: 'int',
        min: 1,
        max: 365,
        unit: 'days',
      },
      {
        key: 'retainDeletedDays',
        label: 'Keep after VM deletion',
        help: 'Days a deleted VM’s backups are kept.',
        type: 'int',
        min: 0,
        max: 3650,
        unit: 'days',
      },
      {
        key: 'minFreeBytes',
        label: 'Free space kept',
        help: 'Free disk space reserved on the backup volume.',
        type: 'bytes',
        min: 0,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
      },
      {
        key: 'maxBytesPerVm',
        label: 'Storage limit per VM',
        help: 'Maximum backup storage one VM may use (at least 64 MiB).',
        type: 'bytes',
        min: 64 * MIB,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
        nullable: true,
        nullLabel: 'No limit',
      },
    ],
    validate: obj => {
      const keep = obj.keep;
      const maxKeep = obj.maxKeep;
      if (typeof keep === 'number' && typeof maxKeep === 'number' && keep > maxKeep) {
        return 'Backups kept per VM cannot exceed maximum backups kept per VM.';
      }
      return null;
    },
  },
  {
    key: 'network',
    label: 'Network',
    description: 'Host forwards and guest address reporting.',
    fields: [
      {
        key: 'hostForwardsEnabled',
        label: 'Enable host forwards',
        help: 'Disabling refuses new primary host forwards immediately.',
        type: 'bool',
      },
      {
        key: 'directAddressReporting',
        label: 'Report direct addresses',
        help: 'Report guest direct network addresses.',
        type: 'bool',
      },
      {
        key: 'defaultMode',
        label: 'Default VM mode',
        help: 'Applies to VMs following the host default on their next full stop/start.',
        type: 'enum',
        options: ['relayed', 'direct'],
        labels: { relayed: 'Relayed', direct: 'Direct' },
        platform: 'proxmox',
      },
      {
        key: 'ownerMaySwitchMode',
        label: 'Owners may switch VM mode',
        help: 'Allow owners to choose relayed or direct mode for their VMs.',
        type: 'bool',
        platform: 'proxmox',
      },
    ],
  },
  {
    key: 'https',
    label: 'HTTPS forwards',
    description: 'Shared HTTPS listener and wake-on-request behavior.',
    fields: [
      {
        key: 'sharedPort',
        label: 'Shared HTTPS port',
        help: 'Usually 443. Serves each VM’s main HTTPS forward under its own name. Empty turns it off.',
        type: 'int',
        min: 1,
        max: 65535,
        nullable: true,
        nullLabel: 'Off',
      },
      {
        key: 'startOnRequest',
        label: 'Start VMs on request',
        help: 'Opening a stopped or saved VM’s HTTPS URL starts it automatically.',
        type: 'bool',
      },
    ],
  },
  {
    key: 'vault',
    label: 'Key vault',
    description: 'Public approval address for phone sign-off.',
    fields: [
      {
        key: 'webUrl',
        label: 'Approval page address',
        help: 'HTTPS address phones open for key vault approvals. Empty uses this host’s own address.',
        type: 'string',
        nullable: true,
        nullLabel: 'Host address',
        placeholder: 'https://vault.example.net',
        httpsUrl: true,
      },
    ],
  },
  {
    key: 'virtualization',
    label: 'Virtualization',
    description: 'Nested virtualization defaults and memory ballooning.',
    fields: [
      {
        key: 'nestedDefault',
        label: 'Enable nesting by default',
        help: 'Expose virtualization extensions to new VMs by default.',
        type: 'bool',
      },
      {
        key: 'nestedSelectable',
        label: 'Allow users to select nesting',
        help: 'Users may toggle nesting unless their user policy overrides it.',
        type: 'bool',
      },
      {
        key: 'minimumRamBytes',
        label: 'Minimum RAM under host pressure',
        help: 'Auto-ballooning floor for running primary VMs (at least 1 GiB). Empty disables ballooning.',
        type: 'bytes',
        min: GIB,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
        nullable: true,
        nullLabel: 'Off',
        platform: 'proxmox',
      },
    ],
  },
  {
    key: 'updates',
    label: 'Updates',
    description: 'Release channel, automatic updates, and rollout timeouts.',
    fields: [
      {
        key: 'auto',
        label: 'Automatic host updates',
        help: 'Apply newer releases from the selected channel automatically.',
        type: 'bool',
      },
      {
        key: 'channel',
        label: 'Update channel',
        help: 'Choose Stable, Nightly, or a custom HTTPS manifest URL.',
        type: 'channel',
      },
      {
        key: 'repository',
        label: 'Update repository',
        help: 'GitHub owner/repository for releases.',
        type: 'string',
        placeholder: 'permissionBRICK/The-Construct',
        pattern: REPO_RE,
        patternError: 'Use GitHub owner/repository format.',
      },
      {
        key: 'drainTimeoutMinutes',
        label: 'Drain timeout',
        help: 'Time allowed to drain active host jobs before updating.',
        type: 'int',
        min: 1,
        max: MAX_INT,
        unit: 'min',
      },
      {
        key: 'healthTimeoutSeconds',
        label: 'Health timeout',
        help: 'Time allowed for the updated service to become healthy.',
        type: 'int',
        min: 30,
        max: MAX_INT,
        unit: 's',
      },
    ],
  },
  {
    key: 'userDefaults',
    label: 'User defaults',
    description: 'Default quotas and permissions for users without custom overrides.',
    fields: [
      {
        key: 'maxPrimaries',
        label: 'Maximum primary VMs',
        help: 'Default primary VM limit per user.',
        type: 'int',
        min: 0,
        max: MAX_INT,
      },
      {
        key: 'allowChildCreation',
        label: 'Allow child VMs',
        help: 'Allow users to create child VMs.',
        type: 'bool',
      },
      {
        key: 'maxRetainedChildren',
        label: 'Retained children',
        help: 'Default maximum retained child VMs per user.',
        type: 'int',
        min: 0,
        max: MAX_INT,
      },
      {
        key: 'cpuBudget',
        label: 'CPU budget',
        help: 'Total vCPUs allowed across a user’s VMs.',
        type: 'int',
        min: 0,
        max: MAX_INT,
        unit: 'vCPU',
        nullable: true,
        nullLabel: 'No limit',
      },
      {
        key: 'ramBudgetBytes',
        label: 'RAM budget',
        help: 'Total reserved RAM allowed across a user’s VMs.',
        type: 'bytes',
        min: 0,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
        nullable: true,
        nullLabel: 'No limit',
      },
      {
        key: 'storageBudgetBytes',
        label: 'Storage budget',
        help: 'Total reserved storage allowed across a user’s VMs.',
        type: 'bytes',
        min: 0,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
        nullable: true,
        nullLabel: 'No limit',
      },
      {
        key: 'maxChildLifetimeSeconds',
        label: 'Maximum child lifetime',
        help: 'Longest finite child VM lifetime (minimum 300 seconds).',
        type: 'int',
        min: 300,
        max: MAX_SAFE,
        unit: 's',
        nullable: true,
        nullLabel: 'No limit',
      },
      {
        key: 'allowNeverLifetime',
        label: 'Allow no expiry',
        help: 'Allow child VMs with a never-expiring lifetime.',
        type: 'bool',
      },
      {
        key: 'allowSharing',
        label: 'Allow sharing',
        help: 'Allow child VMs to be shared host-wide.',
        type: 'bool',
      },
    ],
  },
  {
    key: 'userCaps',
    label: 'Host caps on users',
    description: 'Hard ceilings enforced after user defaults and per-user overrides.',
    fields: [
      {
        key: 'maxRetainedChildren',
        label: 'Retained children',
        help: 'Hard ceiling on retained child VMs per user.',
        type: 'int',
        min: 0,
        max: MAX_INT,
        nullable: true,
        nullLabel: 'No cap',
      },
      {
        key: 'cpuBudget',
        label: 'CPU budget',
        help: 'Hard ceiling on total vCPUs per user.',
        type: 'int',
        min: 0,
        max: MAX_INT,
        unit: 'vCPU',
        nullable: true,
        nullLabel: 'No cap',
      },
      {
        key: 'ramBudgetBytes',
        label: 'RAM budget',
        help: 'Hard ceiling on total reserved RAM per user.',
        type: 'bytes',
        min: 0,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
        nullable: true,
        nullLabel: 'No cap',
      },
      {
        key: 'storageBudgetBytes',
        label: 'Storage budget',
        help: 'Hard ceiling on total reserved storage per user.',
        type: 'bytes',
        min: 0,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
        nullable: true,
        nullLabel: 'No cap',
      },
      {
        key: 'maxChildLifetimeSeconds',
        label: 'Maximum child lifetime',
        help: 'Hard ceiling on finite child VM lifetime (minimum 300 seconds).',
        type: 'int',
        min: 300,
        max: MAX_SAFE,
        unit: 's',
        nullable: true,
        nullLabel: 'No cap',
      },
      {
        key: 'allowNeverLifetime',
        label: 'Allow no expiry',
        help: 'Cap whether users may create never-expiring child VMs.',
        type: 'bool',
        nullable: true,
      },
      {
        key: 'allowSharing',
        label: 'Allow sharing',
        help: 'Cap whether users may share child VMs host-wide.',
        type: 'bool',
        nullable: true,
      },
    ],
  },
  {
    key: 'lifecycle',
    label: 'Lifecycle',
    description: 'Shutdown and child VM lease intervals.',
    fields: [
      {
        key: 'gracefulShutdownTimeoutSeconds',
        label: 'Graceful shutdown timeout',
        help: 'Time allowed for a guest to shut down gracefully.',
        type: 'int',
        min: 30,
        max: MAX_INT,
        unit: 's',
      },
      {
        key: 'leaseTickSeconds',
        label: 'Lease check interval',
        help: 'How often child VM leases are checked.',
        type: 'int',
        min: 30,
        max: MAX_INT,
        unit: 's',
      },
      {
        key: 'leaseRetrySeconds',
        label: 'Lease retry interval',
        help: 'Time before retrying an overdue child VM’s shutdown.',
        type: 'int',
        min: 30,
        max: MAX_INT,
        unit: 's',
      },
    ],
  },
  {
    key: 'media',
    label: 'Media',
    description: 'ISO upload limits, timeouts, and cleanup.',
    fields: [
      {
        key: 'maxBytes',
        label: 'Maximum media per user',
        help: 'Total media storage allowed per user.',
        type: 'bytes',
        min: 1,
        max: MAX_SAFE,
        scale: GIB,
        unit: 'GiB',
      },
      {
        key: 'maxItemsPerUser',
        label: 'Media items per user',
        help: 'Maximum stored media items per user.',
        type: 'int',
        min: 1,
        max: MAX_INT,
      },
      {
        key: 'uploadChunkBytes',
        label: 'Upload chunk size',
        help: 'Chunk size for media uploads (1–64 MiB).',
        type: 'bytes',
        min: MIB,
        max: 64 * MIB,
        scale: MIB,
        unit: 'MiB',
      },
      {
        key: 'uploadTtlHours',
        label: 'Upload TTL',
        help: 'Hours allowed to complete a media upload.',
        type: 'int',
        min: 1,
        max: MAX_INT,
        unit: 'h',
      },
      {
        key: 'acquireTimeoutMinutes',
        label: 'Acquire timeout',
        help: 'Minutes allowed when downloading remote media.',
        type: 'int',
        min: 1,
        max: MAX_INT,
        unit: 'min',
      },
      {
        key: 'allowHttp',
        label: 'Allow HTTP downloads',
        help: 'Allow acquiring media from plain HTTP URLs.',
        type: 'bool',
      },
      {
        key: 'unreferencedTtlHours',
        label: 'Unreferenced media TTL',
        help: 'Delete unreferenced media after this many hours. Empty retains it.',
        type: 'int',
        min: 1,
        max: MAX_INT,
        unit: 'h',
        nullable: true,
        nullLabel: 'Disabled',
      },
    ],
  },
  {
    key: 'usage',
    label: 'Usage',
    description: 'Token usage history retention.',
    fields: [
      {
        key: 'retentionDays',
        label: 'Usage retention',
        help: 'Days of daily usage history kept before cleanup.',
        type: 'int',
        min: 1,
        max: 36500,
        unit: 'days',
      },
    ],
  },
];

export const HOST_CONFIG_SPEC_BY_KEY: Record<string, HostConfigSectionSpec> = Object.fromEntries(
  HOST_CONFIG_SECTIONS.map(s => [s.key, s]),
);

/** Return the visible fields for a section given the host's platform (`Proxmox`, `Hyper-V`, etc.) and raw stored keys. */
export function visibleFields(spec: HostConfigSectionSpec, hostKind: string, raw: Record<string, unknown>): HostConfigField[] {
  const isProxmox = hostKind.toLowerCase() === 'proxmox';
  return spec.fields.filter(f => {
    if (f.platform === 'proxmox' && !isProxmox) return false;
    // Skip fields absent from minimal sample payloads only if they are non-nullable strings like repository? No: show if present in raw or nullable/defaulted.
    if (f.key === 'repository' && !(f.key in raw)) return false;
    return true;
  });
}

/** Parse a config section JSON string into an object, or null if malformed. */
export function parseConfigSection(json: string): Record<string, unknown> | null {
  try {
    const v = JSON.parse(json);
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** Format a byte or integer field value into a clean text input string. */
export function formatNumericInput(field: Extract<HostConfigField, { type: 'int' | 'bytes' }>, value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value !== 'number' || !Number.isFinite(value)) return '';
  if (field.type === 'bytes' && field.scale) {
    const scaled = value / field.scale;
    if (Number.isInteger(scaled)) return String(scaled);
    return String(Number(scaled.toFixed(4)));
  }
  return String(value);
}

/** Map a stored updates.channel string to the segmented control choice. */
export function channelChoice(value: unknown): 'stable' | 'nightly' | 'custom' {
  if (value === 'stable' || value === 'main' || !value) return 'stable';
  if (value === 'nightly') return 'nightly';
  return 'custom';
}

export type FieldDrafts = Record<string, string | boolean | null>;

/** Build editable GUI draft values from a parsed config object. */
export function toFieldDrafts(fields: readonly HostConfigField[], raw: Record<string, unknown>): FieldDrafts {
  const out: FieldDrafts = {};
  for (const f of fields) {
    const v = raw[f.key];
    switch (f.type) {
      case 'bool':
        out[f.key] = f.nullable ? (typeof v === 'boolean' ? v : null) : Boolean(v);
        break;
      case 'enum':
        out[f.key] = typeof v === 'string' && f.options.includes(v) ? v : f.options[0];
        break;
      case 'channel':
        out[f.key] = typeof v === 'string' ? (v === 'main' ? 'stable' : v) : 'stable';
        break;
      case 'int':
      case 'bytes':
        out[f.key] = formatNumericInput(f, v);
        break;
      case 'string':
        out[f.key] = typeof v === 'string' ? v : '';
        break;
    }
  }
  return out;
}

/** Parse and validate all edited fields, returning the full updated JSON object or the first validation error. */
export function buildConfigPayload(
  spec: HostConfigSectionSpec,
  fields: readonly HostConfigField[],
  raw: Record<string, unknown>,
  drafts: FieldDrafts,
): { value: Record<string, unknown>; error: null } | { value: null; error: string } {
  const next: Record<string, unknown> = { ...raw };

  for (const f of fields) {
    const d = drafts[f.key];
    switch (f.type) {
      case 'bool':
        next[f.key] = f.nullable ? (typeof d === 'boolean' ? d : null) : Boolean(d);
        break;

      case 'enum':
        if (typeof d !== 'string' || !f.options.includes(d)) {
          return { value: null, error: `${f.label} must be one of ${f.options.join(', ')}.` };
        }
        next[f.key] = d;
        break;

      case 'channel': {
        const s = typeof d === 'string' ? d.trim() : '';
        if (s === 'stable' || s === 'nightly' || s === 'main') {
          next[f.key] = s;
        } else if (isValidHttpsUrl(s)) {
          next[f.key] = s;
        } else {
          return { value: null, error: 'Update channel must be Stable, Nightly, or a valid https:// manifest URL.' };
        }
        break;
      }

      case 'int': {
        const s = typeof d === 'string' ? d.trim() : '';
        if (s === '') {
          if (f.nullable) { next[f.key] = null; break; }
          return { value: null, error: `${f.label} is required.` };
        }
        if (!/^-?\d+$/.test(s)) {
          return { value: null, error: `${f.label} must be a whole number.` };
        }
        const n = Number(s);
        if (!Number.isSafeInteger(n) || n < f.min || n > f.max) {
          return { value: null, error: rangeMessage(f.label, f.min, f.max, f.unit) };
        }
        next[f.key] = n;
        break;
      }

      case 'bytes': {
        const s = typeof d === 'string' ? d.trim() : '';
        if (s === '') {
          if (f.nullable) { next[f.key] = null; break; }
          return { value: null, error: `${f.label} is required.` };
        }
        const num = Number(s);
        if (!Number.isFinite(num) || num < 0) {
          return { value: null, error: `${f.label} must be a non-negative number.` };
        }
        const bytes = Math.round(num * (f.scale ?? 1));
        if (!Number.isSafeInteger(bytes) || bytes < f.min || bytes > f.max) {
          return { value: null, error: byteRangeMessage(f) };
        }
        next[f.key] = bytes;
        break;
      }

      case 'string': {
        const s = typeof d === 'string' ? d.trim() : '';
        if (s === '') {
          if (f.nullable) { next[f.key] = null; break; }
          return { value: null, error: `${f.label} is required.` };
        }
        if (f.httpsUrl && !isValidHttpsUrl(s)) {
          return { value: null, error: `${f.label} must be a valid https:// address.` };
        }
        if (f.pattern && !f.pattern.test(s)) {
          return { value: null, error: f.patternError ?? `${f.label} is not valid.` };
        }
        next[f.key] = s;
        break;
      }
    }
  }

  const crossErr = spec.validate?.(next) ?? null;
  if (crossErr) return { value: null, error: crossErr };
  return { value: next, error: null };
}

function isValidHttpsUrl(raw: string): boolean {
  try {
    const u = new URL(raw);
    return u.protocol === 'https:' && u.hostname.length > 0;
  } catch {
    return false;
  }
}

function rangeMessage(label: string, min: number, max: number, unit?: string): string {
  const u = unit ? ` ${unit}` : '';
  if (max >= MAX_INT) return `${label} must be at least ${min}${u}.`;
  return `${label} must be between ${min} and ${max}${u}.`;
}

function byteRangeMessage(f: Extract<HostConfigField, { type: 'int' | 'bytes' }>): string {
  const scale = f.scale ?? 1;
  const u = f.unit ? ` ${f.unit}` : ' bytes';
  const minStr = Number((f.min / scale).toFixed(4));
  if (f.max >= MAX_SAFE) return `${f.label} must be at least ${minStr}${u}.`;
  const maxStr = Number((f.max / scale).toFixed(4));
  return `${f.label} must be between ${minStr} and ${maxStr}${u}.`;
}
