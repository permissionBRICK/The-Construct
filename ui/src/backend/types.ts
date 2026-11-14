/* The contract between the screens and whatever serves them. The screens read one immutable `State`
   snapshot, re-render when `subscribe` reports a change, and call the actions. Nothing here knows about
   transport: `FakeBackend` serves invented data, `ApiBackend` the device agent and the engines behind it. */

export type Power = 'on' | 'off' | 'starting' | 'stopping';
export type Open = 't3' | 'vscode' | 'ssh';
export type ThemeMode = 'auto' | 'light' | 'dark';
export type Scheme = 'green' | 'blue' | 'copper' | 'mono';
export type Channel = 'stable' | 'nightly';

/** null: the engine did not report it. */
export interface Usage {
  cpuPct: number | null;
  memUsedGb: number | null;
  diskUsedGb: number | null;
}

/** Settings of one VM. `hw` applies on restart, `disk` on reinstall, `services` and `identity` on reprovision. */
export interface VmSettings {
  hw: { memGb: number; cpus: number; checkpoints: boolean; startWithHost: boolean };
  disk: { diskGb: number; ubuntu: string };
  idle: { afterMin: number; then: 'save' | 'shutdown' | 'never' };
  services: { t3Web: boolean; vscodeWeb: boolean; vscodeTunnel: boolean; smb: boolean; mic: boolean; claudeLive: boolean; opencodeWatcher: boolean; t3ProxyUrl: string };
  identity: { gitName: string; gitEmail: string; keepCredentials: boolean; historyDays: '30' | '90' | 'all' };
}

/** `newer`: the engine says a newer version is out; without it the versions are compared. */
export interface Agent { name: string; version: string; latest: string; installed: boolean; newer?: boolean }
export interface ChildVm { name: string; os: string; left: string; power: Power }
/** A forward of this device to a VM; `closable`: this device opened it and can close it. */
export interface Forward { id: string; port: number; label: string; to: 'This device' | 'LAN'; url: string; closable?: boolean; status?: string }
/** `protected`: kept by the retention rule whatever `keep` says (the newest host-requested backup past the horizon). */
export interface Backup { id: string; at: string; kind: 'Scheduled' | 'Requested' | 'Before reinstall' | 'Slot'; size: string; files: number | null; pinned: boolean; protected: boolean }

export interface Vm {
  id: string;
  name: string;
  hostId: string;
  power: Power;
  address: string;
  /** The SSH port on `address`, when it is not 22. */
  sshPort?: number | null;
  os: string;
  installed: string;
  reprovisioned: string;
  /** Construct version of the VM's guest payload. */
  version: string;
  /** What changed since the last reprovision (a project or a settings group); null when nothing waits. */
  changeWaits: string | null;
  settings: VmSettings;
  /** Hardware the VM runs with now; differs from `settings.hw` while a restart is pending. */
  appliedHw: { memGb: number; cpus: number };
  usage: Usage;
  memTotalGb: number;
  diskTotalGb: number;
  agents: Agent[];
  children: ChildVm[];
  projectsOn: string[];
  forwards: Forward[];
  sharing: { vscodeWeb: boolean; smb: boolean; mic: boolean };
  /** This device's link to the VM: its SSH alias and whether the alias is in the SSH config; null when the device has none. */
  link?: { alias: string; sshConfig: string | null; share: string | null } | null;
  backups: Backup[];
  backupSchedule: { everyH: number; keep: number };
  /** The version Update moves this VM to, as its engine reports it. Undefined: derived from the host and the release. */
  updateTo?: string | null;
  /** Parts of this VM's pages whose route failed, with the error; the rest of the page shows what it has. */
  problems?: Partial<Record<'backups' | 'status' | 'settings' | 'agents' | 'connection', string>>;
}

export interface Profile {
  name: string;
  /** The store's revision this was read at; a save names it so a newer revision is merged, not overwritten. */
  revision?: number;
  /** An open conflict names this profile: an agent in a VM resolves it (S7). */
  conflict?: boolean;
  /** The profile document as the store holds it, with fields this view does not know. */
  doc?: Record<string, unknown>;
  repos: { url: string; directory: string }[];
  sdks: Record<string, string>;
  mcp: { name: string; type: string; command: string; args: string[] }[];
  hostPackages: string[];
  provisionCommands: string[];
  forwards: { port: number; label: string }[];
}

export type JobKind = 'reprovision' | 'reinstall' | 'update' | 'restore' | 'create';
export type JobState = 'running' | 'failed' | 'done' | 'stopped';

/** One line of the repair agent's output: what it says, what the user sent, a note of the guest script, one action of the agent. */
export interface RepairMessage { from: 'agent' | 'user' | 'note' | 'tool'; text: string }
export interface Repair {
  agent: string;
  /** working: it acts on its own; asking: it waits for a reply; done: the engine reports it fixed the cause;
      gave-up: it ended without a fix; stopped: the user or another job on the VM ended it. */
  state: 'working' | 'asking' | 'done' | 'gave-up' | 'stopped';
  messages: RepairMessage[];
  /** Set when the agent runs as a T3 Code thread, which can be opened there instead. */
  t3Thread: boolean;
  /** After the end: how it ended and what the engine did with the job. */
  outcome?: string | null;
}

export interface Job {
  id: string;
  vmId: string;
  kind: JobKind;
  state: JobState;
  step: string;
  stepIndex: number;
  stepCount: number;
  pct: number;
  log: string[];
  error: string | null;
  /** For an update: from and to version. */
  from?: string;
  to?: string;
  /** For an update: 'all' was started by Update everything, 'vm' by "only this VM" (this device stays). */
  scope?: 'all' | 'vm';
  /** The repair agent, started when the job failed. Null when the VM cannot run an agent. */
  repair: Repair | null;
  /** A failed job without a running repair: "Let an agent fix it" starts one. */
  canRepair?: boolean;
}

/** An agent on a VM asks for something only the user can give: a lease of a secret, or a new secret's value. */
export interface VaultRequest {
  id: string;
  kind: 'lease' | 'enter';
  secret: string;
  vmId: string;
  /** The VM and the host as the engine that filed the request names them. */
  vmName: string;
  hostName: string;
  /** The agent's own words: for a lease why it needs it, for enter its description and reason. Shown marked as the agent's. */
  reason: string;
  /** The span the approval covers, when the backend knows it. */
  span: string;
  /** The word on the allow button, from the engine (Approve, Allow once, Delete, ...); empty: Allow. */
  action?: string;
  /** The span the VM asked for, in seconds; the default of the approval's span. Null: the request names none. */
  ttlSeconds?: number | null;
}

export interface Secret { name: string; note: string; usedBy: string[]; last: string; auto: boolean }
/** A host's vault mode: `available` holds the vault key and serves approved requests; `locked` waits for a device. */
export interface VaultMode { hostId: string; hostName: string; mode: 'available' | 'locked' | null; problem?: string | null }
export interface Lease { id: string; secret: string; vmName: string; reason: string; left: string }
export interface Device { id: string; name: string; kind: string; added: string; seen: string; me: boolean; revoked: boolean }
export interface VaultEvent { at: string; vmName: string; secret: string; event: string }

export type Pairing =
  /** This device approves: the user enters the code the new device shows. */
  | { state: 'code'; problem?: string }
  | { state: 'showing'; words: string[]; expiresInS: number }
  | { state: 'confirm'; words: string[]; deviceName: string }
  | { state: 'done'; deviceName: string };

/** `hosts`: every engine that served the device; one not yet confirmed keeps serving it until it syncs. */
export interface Revocation { deviceId: string; deviceName: string; hosts: { name: string; confirmed: boolean }[]; rotate: { secret: string; last: string; done: boolean }[] }

/* Host administration */
export interface HostUser {
  name: string;
  role: 'admin' | 'user';
  enabled: boolean;
  maxVms: number;
  hostForwards: 'default' | 'allowed' | 'refused';
  httpsForwards: boolean;
  nested: 'inherit' | 'on' | 'off';
  allowance: { childVms: boolean; maxChildren: number; cpu: number | null; ramGb: number | null; diskGb: number | null; maxLifetime: string; neverLifetime: boolean; sharing: boolean };
  primaries: number;
  children: number;
  tokensMonth: string;
  tokens: { id: string; label: string; created: string; lastUsed: string }[];
}
export interface HostVm {
  name: string;
  owner: string;
  role: 'primary' | 'child';
  parent?: string;
  power: Power;
  cpuPct: number;
  memGb: number;
  memMaxGb: number;
  diskGb: number;
  cpus: number;
  network: 'default' | 'relayed' | 'direct';
  address: string;
  lease: string;
  shared: boolean;
  tokensToday: string;
}
export interface HostMedia { name: string; owner: string; role: string; state: string; size: string; refs: number; created: string }
/** `retry`: the engine retries this failed job (a provision or reinstall). */
export interface HostJob { id: string; kind: string; vm: string; owner: string; state: 'running' | 'failed' | 'done'; phase: string; created: string; retry?: boolean }
export interface AuditEntry { at: string; actor: string; action: string; target: string }
export interface ConfigSection { name: string; json: string; updated: string }
export interface Certificate { subject: string; validUntil: string; daysLeft: number; sha256: string; issuer: string }
export interface WinKey { id: string; product: string; edition: string; kind: 'Retail' | 'MAK' | 'KMS'; partial: string; used: string; held: boolean }
export interface WinGuest { name: string; stage: string; activation: 'activated' | 'grace' | 'not activated'; keyId: string | null }
export interface HostUpdate { at: string; from: string; to: string; state: 'done' | 'rolled back' | 'running' | 'failed'; by: string }

export interface Host {
  id: string;
  name: string;
  kind: string;
  version: string;
  /** The host's own update channel, as it names it. */
  channel: string;
  /** The current user administers this host. Only then are the fields below filled. */
  admin: boolean;
  address: string;
  /** Sign-in a new user's client uses here. */
  signIn: 'windows' | 'token';
  vmCount: number;
  health: { hypervisor: string; database: string; media: string; inventory: string };
  capacity: { mode: 'observe' | 'enforce'; ram: [number, number]; cpu: [number, number]; disk: [number, number] };
  users: HostUser[];
  vms: HostVm[];
  media: HostMedia[];
  isos: { name: string; current: boolean; size: string }[];
  winKeys: WinKey[];
  winGuests: WinGuest[];
  jobs: HostJob[];
  audit: AuditEntry[];
  usage: { window: 'today' | 'month' | 'all'; byUser: { user: string; vms: number; tokens: string; cost: string }[]; byVm: { vm: string; owner: string; tokens: string; cost: string }[] };
  config: ConfigSection[];
  cert: Certificate | null;
  updates: HostUpdate[];
  /** Set while the host's own update runs. */
  updating: boolean;
  /** The release this host's own updater offers. Undefined: derived from this device's release. */
  updateTo?: string | null;
  /** Set when the engine could not be read; the host's pages show what they have and this. */
  problem?: string | null;
  /** A solo host (the engine on the user's own PC): whether other devices on the LAN reach it. Undefined elsewhere. */
  lan?: boolean;
  /** Admin only: the repair agent starts by itself when a job fails (`repair.auto`). Undefined: not read. */
  repairAuto?: boolean;
}

/** Shown once after adding a user: what that user's client needs. */
export interface NewUserHandout { hostId: string; user: string; address: string; token: string | null }

export interface Part { name: string; version: string; to: string | null; note: 'admin' | 'channel' | null }

/** A run of "update everything" as the agent reports it: each part (this device, a host, the user's VMs on a host) and its state. */
export interface Everything {
  running: boolean;
  parts: { part: 'device' | 'engine' | 'vms'; name: string; state: 'pending' | 'running' | 'done' | 'skipped' | 'failed'; detail: string | null }[];
}

export interface Prefs {
  channel: Channel;
  theme: ThemeMode;
  scheme: Scheme;
  autoUpdate: boolean;
  startWithOs: boolean;
  notifications: boolean;
}

/** The one-time upgrade's steps in their order: the engine on this PC, the recovery code, the old programs
    closed, the conversion. */
export type UpgradeStep = 'engine' | 'recovery' | 'close' | 'convert' | 'done';
export interface Upgrade {
  /** The step the dialog is at, as the agent reports it: the first one not done. */
  step: UpgradeStep;
  /** The step runs now. */
  running: boolean;
  /** The current step failed with this; it shows in place with Retry. */
  error: string | null;
  /** Step `recovery`: the code, shown once until the user confirmed it was kept; it is kept nowhere else. */
  code: string[] | null;
  /** Step `close`: the programs that still have the old files open. */
  writers: string[];
  /** Step `convert`: each item the converter copies. */
  items: { name: string; state: 'done' | 'running' | 'waiting' | 'skipped' | 'failed'; count: string; pct: number }[];
  /** What the old install holds. */
  found: { vms: string[]; profiles: number; vault: boolean } | null;
  /** Step `engine` installs an engine on this PC, with one admin prompt. */
  adminPrompt: boolean;
  /** Step `convert`: shared hosts that still run the old Construct; their admin updates them from the host page. */
  oldHosts: string[];
  /** Step `done`: what the conversion could not finish, shown once; the step is there only when there are any. */
  notes: string[];
}

/** Parts of the product the backend cannot serve yet. The screens show such a part as not available. */
export type Feature =
  | 'vault' | 'repair-chat' | 'forwards' | 'sharing' | 'projects' | 'agents' | 'open' | 'console'
  | 'vm-password' | 'vm-services' | 'vm-disk' | 'vm-boot' | 'reinstall' | 'restore' | 'device-update'
  | 'channel' | 'os-prefs' | 'windows-sign-in' | 'export-code' | 'code-reveal' | 'vm-token' | 'host-retry'
  | 'take-back' | 'project-on' | 'approval-span' | 'agent-update' | 'notifications' | 'log' | 'vm-history' | 't3-proxy';

/** First run of a device with no identity yet (11.6): set up the account, pair, or recover; then create a VM. */
export interface FirstRun {
  step: 'start' | 'code' | 'pairing';
  /** The engine the agent found on this machine, offered as the address. */
  localAddress: string | null;
  /** While a call runs. */
  busy: boolean;
  problem: string | null;
  /** Step `code`: the recovery code, shown once; gone after Continue. */
  code: string[] | null;
  /** Step `code`: the account the code recovers. */
  account?: string | null;
  /** Step `pairing`: the code the approving device enters, and the words both show. */
  pairing: { code: string; words: string[]; expiresAt: string } | null;
}

export interface FirstRunRequest {
  how: 'setup' | 'pair' | 'recover';
  address: string;
  deviceName: string;
  /** setup: the user's sign-in at the engine: a token, or Windows sign-in where this device offers it. */
  token?: string;
  signIn?: 'windows' | 'token';
  /** recover: the account and the recovery code. */
  account?: string;
  code?: string;
  /** The fingerprint the user confirmed for `address`; not needed for this machine's own engine. */
  pin?: string;
}

export interface State {
  /** `sshKey`: this device's SSH public key; `can`: what this device's platform offers (undefined: all). */
  device: { name: string; sshKey?: string | null; can?: { mic: boolean; share: boolean; startWithOs: boolean } };
  version: string;
  /** A newer release on this device's channel, or null. */
  release: string | null;
  vms: Vm[];
  hosts: Host[];
  projects: Profile[];
  sync: { when: string; remote: string; rev: string };
  jobs: Job[];
  vaultRequests: VaultRequest[];
  secrets: Secret[];
  leases: Lease[];
  devices: Device[];
  history: VaultEvent[];
  /** The vault mode of each host; empty where the backend has none. */
  vaultModes: VaultMode[];
  /** Parts of the vault and projects whose route failed, with the error; each shows on its own page. */
  problems: Partial<Record<'secrets' | 'requests' | 'history' | 'projects', string>>;
  pairing: Pairing | null;
  revocation: Revocation | null;
  handout: NewUserHandout | null;
  prefs: Prefs;
  upgrade: Upgrade | null;
  /** A short message from the last action, shown as a toast. Each new one has a new id. */
  notice: { id: number; text: string } | null;
  /** Parts not available from this backend. */
  na: Feature[];
  /** Set while this device has no identity: the first-run screen stands in for everything else. */
  firstRun: FirstRun | null;
  /** The last "update everything" run since this device started; null before one. */
  everything?: Everything | null;
}

export type SettingsPatch = { [K in keyof VmSettings]?: Partial<VmSettings[K]> };

export interface CreateVmRequest {
  where: 'local' | 'remote';
  name: string;
  hostAddress?: string;
  signIn?: 'windows' | 'token';
  token?: string;
  /** remote: the certificate fingerprint the user confirmed for `hostAddress` (from `probeHost`). */
  pin?: string;
}

/** What a host presents before it is added: its address as the agent reads it and its certificate's fingerprint. */
export interface HostProbe {
  url: string;
  pin: string;
  /** Already added; nothing to confirm. */
  known: boolean;
}

/** How a profile save ended. */
export type ProfileSave = { ok: true; text: string } | { ok: false; changed: boolean; text: string };

export interface Backend {
  snapshot(): State;
  /** Calls `fn` after every change; returns the unsubscribe function. */
  subscribe(fn: () => void): () => void;

  /* VM */
  open(vmId: string, what: Open): void;
  /** Open a forwarded port in the browser. */
  openForward(vmId: string, port: number): void;
  openChildConsole(vmId: string, child: string): void;
  setVmPassword(vmId: string, password: string): void;
  power(vmId: string, on: boolean): void;
  restart(vmId: string): void;
  reprovision(vmId: string): void;
  reinstall(vmId: string, freshImage: boolean): void;
  /** Updates everything the user owns: this device, hosts they administer, their own VMs. */
  updateEverything(): void;
  /** The secondary choice: update only this VM's payload and reprovision it. */
  updateVm(vmId: string): void;
  stopJob(jobId: string): void;
  retryJob(jobId: string): void;
  /** Starts the repair agent on a failed job ("Let an agent fix it"). */
  startRepair(jobId: string): void;
  /** Sends one reply while the repair agent waits for one. */
  replyRepair(jobId: string, text: string): void;
  stopRepair(jobId: string): void;
  dismissJob(jobId: string): void;
  /** Hardware changes ask restart now or later; `when` says which. Other groups save at once. */
  saveSettings(vmId: string, patch: SettingsPatch, when?: 'now' | 'later'): void;
  updateAgents(vmId: string): void;
  setProjectOn(vmId: string, project: string, on: boolean): void;
  /** Saves a profile document; `revision` is the one the editor read. Changed: the profile was reloaded. */
  saveProfile(name: string, doc: Record<string, unknown>, revision: number | undefined): Promise<ProfileSave>;
  addProject(name: string): void;
  syncProjects(): void;
  closeForward(vmId: string, forwardId: string): void;
  /** This device's SSH config: write the VM's `Host` alias again, or remove it. */
  setSshAlias(vmId: string, on: boolean): void;
  setSharing(vmId: string, key: keyof Vm['sharing'], on: boolean): void;
  backupNow(vmId: string): void;
  restoreBackup(vmId: string, backupId: string): void;
  deleteBackup(vmId: string, backupId: string): void;
  pinBackup(vmId: string, backupId: string, pinned: boolean): void;
  setBackupSchedule(vmId: string, schedule: Partial<Vm['backupSchedule']>): void;
  /** Returns the new VM's id, or null when nothing was started. */
  createVm(req: CreateVmRequest): string | null;
  /** Reads the certificate a host presents, for the user to confirm before it is added. */
  probeHost(address: string): Promise<HostProbe>;

  /* Key vault */
  /** `ttlSeconds`: the span the user chose; undefined keeps the span the VM asked for. */
  answerLease(requestId: string, allow: boolean, ttlSeconds?: number): void;
  /** `value` null declines the request. */
  enterSecret(requestId: string, value: string | null): void;
  addSecret(name: string, note: string, value: string): void;
  /** A new value for an existing secret. */
  replaceSecret(name: string, value: string): void;
  /** The value, for the page to show for a moment; the backend keeps no copy. */
  revealSecret(name: string): Promise<string>;
  deleteSecret(name: string): void;
  setVaultMode(hostId: string, mode: 'available' | 'locked'): void;
  setAutoApprove(name: string, on: boolean): void;
  takeBack(leaseId: string): void;
  startPairing(): void;
  /** On the approving device: the code the new device shows. */
  enterPairingCode(code: string): void;
  confirmPairing(accept: boolean): void;
  cancelPairing(): void;
  revokeDevice(deviceId: string): void;
  /** The page of a revoked device: the secrets it could read, to rotate. */
  openRevocation(deviceId: string): void;
  markRotated(secret: string): void;
  closeRevocation(): void;

  /* Hosts */
  updateHost(hostId: string): void;
  updateHostVms(hostId: string): void;
  /** A solo host: let devices on the LAN reach it (before a second device pairs). */
  setLanAccess(hostId: string, on: boolean): void;
  /** The host setting `repair.auto`: the repair agent starts by itself when a job fails. */
  setRepairAuto(hostId: string, on: boolean): void;
  addUser(hostId: string, user: { name: string; role: 'admin' | 'user'; maxVms: number; hostForwards: HostUser['hostForwards'] }): void;
  saveUser(hostId: string, user: HostUser): void;
  removeUser(hostId: string, name: string): void;
  issueToken(hostId: string, user: string, label: string): void;
  revokeToken(hostId: string, user: string, tokenId: string): void;
  closeHandout(): void;
  hostVmAction(hostId: string, vm: string, action: 'start' | 'restart' | 'shutdown' | 'delete' | 'console' | 'rotate-token' | 'toggle-shared' | 'renew-lease'): void;
  saveHostVm(hostId: string, vm: string, patch: Partial<Pick<HostVm, 'cpus' | 'memMaxGb' | 'network'>>): void;
  deleteMedia(hostId: string, name: string): void;
  cleanupMedia(hostId: string): void;
  acquireWindows(hostId: string, product: string, edition: string, language: string): void;
  addWinKey(hostId: string, key: Omit<WinKey, 'id' | 'partial' | 'used' | 'held'> & { key: string }): void;
  removeWinKey(hostId: string, id: string): void;
  assignWinKey(hostId: string, guest: string, keyId: string): void;
  activateWindows(hostId: string, guest: string): void;
  cancelHostJob(hostId: string, jobId: string): void;
  retryHostJob(hostId: string, jobId: string): void;
  setUsageWindow(hostId: string, window: Host['usage']['window']): void;
  /** Returns an error message for invalid JSON, else null. */
  saveConfig(hostId: string, section: string, json: string): string | null;
  installCertificate(hostId: string, pem: string): void;
  removeCertificate(hostId: string): void;

  /* This device and Construct */
  setPrefs(patch: Partial<Prefs>): void;
  /** The recovery code to show in place. A backend with `code-reveal` in `na` issues a new one instead. */
  showRecoveryCode(): string[] | Promise<string[]>;
  /** Hand the recovery code to the shell: a file picker for saving, or the print dialog. */
  exportRecoveryCode(how: 'file' | 'print'): void;
  openLog(): void;
  /** The one-time upgrade: runs the current step again or goes on from it; the backend decides which step that is.
      `deviceName` names this device at the recovery step. */
  upgradeNext(input?: { deviceName?: string }): void;
  /** First run: set up, pair or recover. */
  firstRunStart(req: FirstRunRequest): void;
  /** First run: the recovery code is stored, or the pairing is cancelled; go on. */
  firstRunNext(): void;
}
