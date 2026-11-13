import type { Host, HostUser, State, Vm, VmSettings } from './types';

/* Invented sample data for the fake backend. Every name, address and value is made up; addresses use the
   documentation ranges (example.net, 192.0.2.0/24). */

const settings = (memGb: number, cpus: number): VmSettings => ({
  hw: { memGb, cpus, checkpoints: false, startWithHost: true },
  disk: { diskGb: 120, ubuntu: '24.04' },
  idle: { afterMin: 30, then: 'never' },
  services: { t3Web: true, vscodeWeb: true, vscodeTunnel: false, smb: false, mic: false, claudeLive: true, opencodeWatcher: true, t3ProxyUrl: 'https://t3.example.net' },
  identity: { gitName: 'Alex Example', gitEmail: 'alex@example.net', keepCredentials: false, historyDays: '90' },
});

const agents = () => [
  { name: 'Claude Code', version: '2.1.317', latest: '2.1.317', installed: true },
  { name: 'Codex', version: '0.48.0', latest: '0.49.1', installed: true },
  { name: 'OpenCode', version: '1.0.132', latest: '1.0.132', installed: true },
  { name: 'T3 Code', version: '0.1.4', latest: '0.1.4', installed: true },
];

function vm(p: Partial<Vm> & Pick<Vm, 'id' | 'name' | 'hostId' | 'power' | 'address'>): Vm {
  return {
    os: 'Ubuntu 24.04.2 LTS', installed: '12 Jun 2026', reprovisioned: '2 days ago', version: '2.4.0', changeWaits: null,
    settings: settings(16, 8), appliedHw: { memGb: 16, cpus: 8 },
    usage: { cpuPct: 12, memUsedGb: 6.2, diskUsedGb: 48 }, memTotalGb: 16, diskTotalGb: 120,
    agents: agents(), children: [], projectsOn: [], forwards: [],
    sharing: { vscodeWeb: true, smb: false, mic: false },
    backups: [], backupSchedule: { everyH: 24, keep: 10 },
    ...p,
  };
}

const user = (name: string, role: 'admin' | 'user', primaries: number, children: number, tokensMonth: string, tokens: HostUser['tokens'] = []): HostUser => ({
  name, role, enabled: true, maxVms: role === 'admin' ? 4 : 1, hostForwards: 'default', httpsForwards: role === 'admin', nested: 'inherit',
  allowance: { childVms: true, maxChildren: 2, cpu: 16, ramGb: 32, diskGb: 400, maxLifetime: '3d', neverLifetime: role === 'admin', sharing: true },
  primaries, children, tokensMonth, tokens,
});

const emptyAdmin = {
  health: { hypervisor: 'ok', database: 'ok', media: 'ok', inventory: 'ok' },
  capacity: { mode: 'observe' as const, ram: [0, 0] as [number, number], cpu: [0, 0] as [number, number], disk: [0, 0] as [number, number] },
  users: [], vms: [], media: [], isos: [], winKeys: [], jobs: [], audit: [],
  usage: { window: 'month' as const, byUser: [], byVm: [] }, config: [], cert: null, updates: [], updating: false,
};

const json = (o: unknown) => JSON.stringify(o, null, 2);

function orchard(): Host {
  return {
    id: 'orchard', name: 'orchard', kind: 'Proxmox', version: '2.4.0', channel: 'stable', admin: true,
    address: 'https://orchard.example.net:8443', signIn: 'token', vmCount: 4,
    health: { hypervisor: 'ok', database: 'ok', media: 'ok', inventory: 'ok' },
    capacity: { mode: 'enforce', ram: [212, 256], cpu: [38, 64], disk: [1.9, 4] },
    users: [
      user('alex', 'admin', 1, 1, '41.2 M', [{ id: 't-81f2', label: 'laptop', created: '3 Aug', lastUsed: '4 d' }]),
      user('sam', 'user', 1, 0, '12.8 M', [{ id: 't-0c9a', label: 'workstation', created: '20 Sep', lastUsed: '2 h' }]),
      user('robin', 'user', 0, 0, '0'),
    ],
    vms: [
      { name: 'harbor', owner: 'alex', role: 'primary', power: 'on', cpuPct: 22, memGb: 9.4, memMaxGb: 16, diskGb: 88, cpus: 8, network: 'relayed', address: '192.0.2.21', lease: 'no expiry', shared: false, tokensToday: '1.2 M' },
      { name: 'harbor-win', owner: 'alex', role: 'child', parent: 'harbor', power: 'on', cpuPct: 5, memGb: 3.1, memMaxGb: 8, diskGb: 40, cpus: 4, network: 'relayed', address: '192.0.2.22', lease: '5 h 20 m', shared: false, tokensToday: '0' },
      { name: 'moss', owner: 'sam', role: 'primary', power: 'off', cpuPct: 0, memGb: 0, memMaxGb: 8, diskGb: 61, cpus: 4, network: 'direct', address: '192.0.2.30', lease: 'no expiry', shared: false, tokensToday: '0' },
      { name: 'reed', owner: 'sam', role: 'child', parent: 'moss', power: 'off', cpuPct: 0, memGb: 0, memMaxGb: 4, diskGb: 22, cpus: 2, network: 'relayed', address: '192.0.2.31', lease: 'expired', shared: true, tokensToday: '0' },
    ],
    media: [
      { name: 'win11-pro-en.iso', owner: 'alex', role: 'install', state: 'prepared', size: '6.1 GB', refs: 1, created: '2 Nov' },
      { name: 'debian-13.iso', owner: 'sam', role: 'install', state: 'ready', size: '0.7 GB', refs: 0, created: '28 Oct' },
    ],
    isos: [{ name: 'construct-ubuntu-24.04-2.4.0.iso', current: true, size: '3.2 GB' }, { name: 'construct-ubuntu-24.04-2.3.2.iso', current: false, size: '3.2 GB' }],
    winKeys: [
      { id: 'k1', product: 'Windows 11', edition: 'Pro', kind: 'Retail', partial: '•••••-•••••-•••••-•••••-7QK2X', used: '1 / 5', held: true },
      { id: 'k2', product: 'Server 2025', edition: 'Standard', kind: 'KMS', partial: '•••••-•••••-•••••-•••••-M4R8P', used: '0', held: false },
    ],
    jobs: [
      { id: 'j-4411', kind: 'provision', vm: 'harbor', owner: 'alex', state: 'done', phase: 'done', created: 'Today 09:12' },
      { id: 'j-4410', kind: 'delete-child', vm: 'reed-old', owner: 'sam', state: 'failed', phase: 'disk busy', created: 'Yesterday 17:40' },
      { id: 'j-4409', kind: 'media-cleanup', vm: '–', owner: 'host', state: 'done', phase: 'done', created: 'Yesterday 03:00' },
    ],
    audit: [
      { at: 'Today 09:12', actor: 'alex', action: 'vm.provision', target: 'harbor' },
      { at: 'Yesterday 17:40', actor: 'sam', action: 'vm.delete', target: 'reed-old' },
      { at: '11 Nov 10:02', actor: 'alex', action: 'user.add', target: 'robin' },
      { at: '10 Nov 08:30', actor: 'alex', action: 'config.save', target: 'backups' },
    ],
    usage: {
      window: 'month',
      byUser: [{ user: 'alex', vms: 2, tokens: '41.2 M', cost: '$61.80' }, { user: 'sam', vms: 2, tokens: '12.8 M', cost: '$19.20' }],
      byVm: [{ vm: 'harbor', owner: 'alex', tokens: '40.9 M', cost: '$61.35' }, { vm: 'moss', owner: 'sam', tokens: '12.8 M', cost: '$19.20' }, { vm: 'harbor-win', owner: 'alex', tokens: '0.3 M', cost: '$0.45' }],
    },
    config: [
      { name: 'capacity', updated: '2 Nov', json: json({ mode: 'enforce', ramHeadroomBytes: null, storageHeadroomBytes: 21474836480, cpuBudget: 64, maxVcpusPerVm: 16, reconcileSeconds: 60, orphanReservationTimeoutSeconds: 600 }) },
      { name: 'memoryPressure', updated: '2 Nov', json: json({ enabled: true, highWaterPercent: 90, lowWaterPercent: 80, swapHighWaterPercent: 50, minSecondsBetweenSaves: 60, cooldownMinutesAfterSave: 10 }) },
      { name: 'userDefaults', updated: '14 Oct', json: json({ maxPrimaries: 1, allowChildCreation: true, maxRetainedChildren: 1, cpuBudget: null, ramBudgetBytes: null, storageBudgetBytes: null, maxChildLifetimeSeconds: null, allowNeverLifetime: true, allowSharing: true }) },
      { name: 'userCaps', updated: '14 Oct', json: json({ maxRetainedChildren: null, cpuBudget: null, ramBudgetBytes: null, storageBudgetBytes: null, maxChildLifetimeSeconds: null, allowNeverLifetime: null, allowSharing: null }) },
      { name: 'lifecycle', updated: '14 Oct', json: json({ gracefulShutdownTimeoutSeconds: 300, leaseTickSeconds: 30, leaseRetrySeconds: 600 }) },
      { name: 'media', updated: '14 Oct', json: json({ maxBytes: 17179869184, maxItemsPerUser: 20, uploadChunkBytes: 8388608, uploadTtlHours: 24, acquireTimeoutMinutes: 180, allowHttp: true, unreferencedTtlHours: null }) },
      { name: 'network', updated: '14 Oct', json: json({ hostForwardsEnabled: true, directAddressReporting: true, defaultMode: 'relayed', ownerMaySwitchMode: false }) },
      { name: 'virtualization', updated: '14 Oct', json: json({ nestedDefault: true, nestedSelectable: true, minimumRamBytes: 6442450944 }) },
      { name: 'updates', updated: '14 Oct', json: json({ channel: 'stable', auto: false, drainTimeoutMinutes: 60, healthTimeoutSeconds: 120 }) },
      { name: 'usage', updated: '14 Oct', json: json({ retentionDays: 400 }) },
      { name: 'vault', updated: '14 Oct', json: json({ webUrl: null }) },
      { name: 'https', updated: '14 Oct', json: json({ sharedPort: 8443, startOnRequest: true }) },
      { name: 'backups', updated: '10 Nov', json: json({ enabled: true, intervalHours: 24, keep: 4, maxKeep: 10, pinDays: 14, retainDeletedDays: 14, minFreeBytes: 5368709120, maxBytesPerVm: null }) },
    ],
    cert: { subject: 'CN=orchard.example.net', validUntil: '2 Feb 2027', daysLeft: 81, sha256: '4F:1C:9A:22:…:E0:7B', issuer: 'Example Issuing CA' },
    updates: [
      { at: '2 Nov 21:04', from: '2.3.2', to: '2.4.0', state: 'done', by: 'alex' },
      { at: '18 Oct 20:51', from: '2.3.1', to: '2.3.2', state: 'done', by: 'alex' },
      { at: '18 Oct 20:40', from: '2.3.1', to: '2.3.2', state: 'rolled back', by: 'alex' },
    ],
    updating: false,
  };
}

export function sampleState(): State {
  return {
    device: { name: 'Workbench' },
    version: '2.4.0',
    release: null,
    vms: [
      vm({
        id: 'atlas', name: 'atlas', hostId: 'pc', power: 'on', address: 'atlas.example.net',
        settings: settings(16, 8),
        children: [{ name: 'win-test', os: 'Windows 11', left: '5 h 20 m', power: 'on' }],
        projectsOn: ['webshop', 'toolkit'],
        forwards: [
          { port: 5173, label: 'vite dev', to: 'This device', url: 'localhost:5173' },
          { port: 8080, label: 'api', to: 'This device', url: 'localhost:8080' },
          { port: 8000, label: 'VS Code web', to: 'LAN', url: 'atlas.example.net:8000' },
        ],
        backups: [
          { id: 'b4', at: 'Today 03:00', kind: 'Scheduled', size: '412 MB', pinned: false },
          { id: 'b3', at: 'Yesterday 03:00', kind: 'Scheduled', size: '409 MB', pinned: false },
          { id: 'b2', at: '2 Nov 14:12', kind: 'Before reinstall', size: '398 MB', pinned: true },
          { id: 'b1', at: '28 Oct 09:30', kind: 'Manual', size: '377 MB', pinned: true },
        ],
      }),
      vm({ id: 'pebble', name: 'pebble', hostId: 'pc', power: 'off', address: 'pebble.example.net', projectsOn: ['docs-site'], usage: { cpuPct: 0, memUsedGb: 0, diskUsedGb: 31 }, settings: settings(8, 4), appliedHw: { memGb: 8, cpus: 4 }, memTotalGb: 8 }),
      vm({ id: 'harbor', name: 'harbor', hostId: 'orchard', power: 'on', address: 'harbor.orchard.example.net', projectsOn: ['webshop'], usage: { cpuPct: 22, memUsedGb: 9.4, diskUsedGb: 88 },
        children: [{ name: 'harbor-win', os: 'Windows 11', left: '5 h 20 m', power: 'on' }],
        backups: [{ id: 'h1', at: 'Today 02:00', kind: 'Scheduled', size: '1.1 GB', pinned: false }] }),
      vm({ id: 'kiln', name: 'kiln', hostId: 'lab', power: 'on', address: 'kiln.lab.example.net', version: '2.6.0', projectsOn: ['toolkit'], usage: { cpuPct: 3, memUsedGb: 2.1, diskUsedGb: 19 }, settings: settings(8, 4), appliedHw: { memGb: 8, cpus: 4 }, memTotalGb: 8 }),
    ],
    hosts: [
      {
        id: 'pc', name: 'This PC', kind: 'Hyper-V', version: '2.4.0', channel: 'stable', admin: true, address: 'https://192.0.2.10:8443', signIn: 'windows', vmCount: 2,
        ...emptyAdmin,
        capacity: { mode: 'observe', ram: [24, 64], cpu: [12, 16], disk: [0.3, 1] },
        vms: [
          { name: 'atlas', owner: 'alex', role: 'primary', power: 'on', cpuPct: 12, memGb: 6.2, memMaxGb: 16, diskGb: 48, cpus: 8, network: 'default', address: '192.0.2.11', lease: 'no expiry', shared: false, tokensToday: '0.8 M' },
          { name: 'win-test', owner: 'alex', role: 'child', parent: 'atlas', power: 'on', cpuPct: 4, memGb: 2.8, memMaxGb: 8, diskGb: 38, cpus: 4, network: 'default', address: '192.0.2.12', lease: '5 h 20 m', shared: false, tokensToday: '0' },
          { name: 'pebble', owner: 'alex', role: 'primary', power: 'off', cpuPct: 0, memGb: 0, memMaxGb: 8, diskGb: 31, cpus: 4, network: 'default', address: '192.0.2.13', lease: 'no expiry', shared: false, tokensToday: '0' },
        ],
        users: [user('alex', 'admin', 2, 1, '18.4 M')],
        config: orchard().config.filter(c => ['capacity', 'lifecycle', 'backups', 'updates'].includes(c.name)),
        updates: [{ at: '2 Nov 20:58', from: '2.3.2', to: '2.4.0', state: 'done', by: 'alex' }],
      },
      orchard(),
      { id: 'lab', name: 'lab', kind: 'Proxmox', version: '2.6.0', channel: 'nightly', admin: false, address: 'https://lab.example.net:8443', signIn: 'token', vmCount: 1, ...emptyAdmin },
    ],
    projects: [
      {
        name: 'webshop', repos: [{ url: 'https://git.example.net/alex/webshop.git', directory: 'webshop' }], sdks: { node: '22', pnpm: '9' },
        mcp: [{ name: 'postgres', type: 'stdio', command: 'npx', args: ['-y', '@modelcontextprotocol/server-postgres'] }],
        hostPackages: ['postgresql-client', 'libvips-dev'],
        provisionCommands: ['cd /root/repos/webshop && pnpm install', 'pnpm exec playwright install --with-deps chromium'],
        forwards: [{ port: 5173, label: 'vite dev' }, { port: 8080, label: 'api' }],
      },
      { name: 'toolkit', repos: [{ url: 'https://git.example.net/alex/toolkit.git', directory: 'toolkit' }, { url: 'https://git.example.net/alex/toolkit-docs.git', directory: 'toolkit-docs' }], sdks: { dotnet: '9', node: '22' }, mcp: [], hostPackages: [], provisionCommands: [], forwards: [] },
      { name: 'flat-plan', repos: [{ url: 'https://git.example.net/alex/flat-plan.git', directory: 'flat-plan' }], sdks: { python: '3.12' }, mcp: [], hostPackages: ['blender'], provisionCommands: [], forwards: [] },
      { name: 'docs-site', repos: [{ url: 'https://git.example.net/alex/docs-site.git', directory: 'docs-site' }], sdks: { node: '22' }, mcp: [], hostPackages: [], provisionCommands: [], forwards: [] },
    ],
    sync: { when: '3 min ago', remote: 'orchard', rev: 'a41c9e2' },
    jobs: [],
    vaultRequests: [],
    secrets: [
      { name: 'forge-token', note: 'Git forge, push', usedBy: ['atlas'], last: 'now', auto: false },
      { name: 'staging-db', note: 'Staging database', usedBy: ['harbor'], last: '2 d', auto: false },
      { name: 'deploy-key', note: 'Production deploy', usedBy: [], last: '9 d', auto: false },
      { name: 'npm-publish', note: 'Registry, publish', usedBy: ['atlas', 'harbor'], last: '1 h', auto: true },
      { name: 'mail-api', note: 'Test mailbox', usedBy: [], last: '3 d', auto: true },
    ],
    leases: [
      { id: 'l1', secret: 'forge-token', vmName: 'atlas', reason: 'Push the release', left: '1:42' },
      { id: 'l2', secret: 'staging-db', vmName: 'harbor', reason: 'Run the migration', left: '0:18' },
    ],
    devices: [
      { id: 'd1', name: 'Workbench', kind: 'PC', added: '12 Jun', seen: 'now', me: true, revoked: false },
      { id: 'd2', name: 'Fennel', kind: 'Laptop', added: '3 Aug', seen: '4 d', me: false, revoked: false },
      { id: 'd3', name: 'Phone', kind: 'Phone', added: '20 Sep', seen: '1 h', me: false, revoked: false },
    ],
    history: [
      { at: '16:40', vmName: 'atlas', secret: 'forge-token', event: 'Allowed 2 h' },
      { at: '16:12', vmName: 'harbor', secret: 'staging-db', event: 'Allowed 30 min' },
      { at: '15:55', vmName: 'atlas', secret: 'npm-publish', event: 'Auto' },
      { at: '14:03', vmName: 'harbor', secret: 'deploy-key', event: 'Denied' },
      { at: '11:20', vmName: 'atlas', secret: 'forge-token', event: 'Released, scrubbed' },
      { at: '09:02', vmName: 'harbor', secret: 'mail-api', event: 'Auto' },
    ],
    pairing: null,
    revocation: null,
    handout: null,
    prefs: { channel: 'stable', theme: 'auto', scheme: 'green', autoUpdate: false, startWithOs: true, notifications: true },
    upgrade: null,
    notice: null,
  };
}

export const NEXT_RELEASE = '2.5.0';
export const RECOVERY_CODE = ['K7QD', 'M2XV', '9RTC', 'HW4N', 'PL6E', '3ZBA', 'YU8S', 'F5GJ'];
export const PAIR_WORDS = ['copper', 'lantern', 'orbit', 'meadow'];
export const REPROVISION_STEPS = ['Connecting', 'Fetching Construct', 'System packages', 'Installing tools', 'Projects', 'Coding agents', 'Services'];
export const REINSTALL_STEPS = ['Backing up', 'Wiping', 'Installing Ubuntu', 'Restoring', 'Installing tools', 'Projects', 'Services'];
