/* Shared sample data (research/sample-data.md) plus invented detail that stays consistent with it. */
window.D = {
  now: '14:18',
  pc: { name: 'PC-1', os: 'Windows 11', ram: 64, mic: { device: 'Shure MV7', armed: true, live: false } },
  update: {
    installed: 'b5c4348', latest: '3a91f0e',
    commits: [
      ['3a91f0e', 'Default VM CPU allocation to the host allowance'],
      ['9c04d12', 'Initialize vTPM identity before capturing reusable VM baselines'],
      ['71be5a0', 'Clarify optional CPU sizing in the child VM design'],
      ['e28f7c3', 'Show Windows activation status on a recurring timer']
    ]
  },
  instances: {
    'agent-vm': {
      id: 'agent-vm', backend: 'hyperv-local', where: 'Hyper-V on PC-1', whereShort: 'local Hyper-V',
      state: 'running', uptime: '3h 12m', vcpu: 8, ram: 16, ramUsed: 11.2, disk: 146, diskPct: 88,
      os: 'Ubuntu 26.04 LTS', provisioned: 'c21978b', installed: 'b5c4348', behind: 2,
      ip: '172.29.112.14'
    },
    'dev-2': {
      id: 'dev-2', backend: 'hyperv-remote', where: 'buildbox.example.local:7462', whereShort: 'on buildbox',
      state: 'saved', savedAgo: '41 min', savedBy: 'idle policy', vcpu: 4, ram: 12, ramUsed: 0, disk: 80, diskPct: 41,
      os: 'Ubuntu 26.04 LTS', provisioned: '3a91f0e', installed: '3a91f0e', behind: 0,
      idle: { after: 60, action: 'save' }
    }
  },
  child: { id: 'win-test', parent: 'agent-vm', os: 'Windows 11 eval', ram: 4, vcpu: 2, lease: '5h 20m', leaseLeftPct: 67, state: 'running', licence: 'evaluation · 88 days left' },
  agents: [
    { id: 'claude', name: 'Claude Code', mark: 'Cl', version: '2.4.1', update: '2.4.3', state: 'working', repo: 'construct', since: '6 min', task: 'Refactoring forwarder retry backoff', cost: 3.40, tokens: '1.9M' },
    { id: 'codex', name: 'Codex', mark: 'Cx', version: '0.61.0', update: null, state: 'idle', repo: 'gitgudlab', last: '48 min ago', task: 'Last turn: roll back deploy script', cost: 0.58, tokens: '410K' },
    { id: 'opencode', name: 'OpenCode', mark: 'Oc', version: '1.9.2', update: null, state: 'idle', serve: ':4096', task: 'serve running on :4096', cost: 0.14, tokens: '96K' },
    { id: 't3', name: 'T3 Code', mark: 'T3', version: '0.9.14-construct.3', update: null, state: 'web', threads: 2, task: 'Web UI enabled · 2 threads active', cost: null }
  ],
  forwards: [
    { id: 'f5173', inst: 'agent-vm', vm: 5173, local: 5173, label: 'vite dev server', state: 'open', scope: 'client', by: 'Claude', ago: '12 min' },
    { id: 'f8080', inst: 'agent-vm', vm: 8080, local: 18800, label: 'api preview', state: 'open', scope: 'client', by: 'Codex', ago: '1h 05m', note: '8080 is busy on PC-1, so it was remapped to 18800' },
    { id: 'f3000', inst: 'dev-2', vm: 3000, local: 3000, label: 'host forward on buildbox LAN', state: 'queued', scope: 'host', by: 'Claude', ago: '52 min', note: 'Opens when dev-2 resumes' }
  ],
  inbox: [
    { id: 'n2', t: '13:40', level: 'error', text: 'Deploy failed, rolling back', agent: 'codex', repo: 'gitgudlab' },
    { id: 'n1', t: '14:02', level: 'info', text: 'Test suite finished — 3 failures', agent: 'claude', repo: 'construct' },
    { id: 'n3', t: '12:15', level: 'info', text: 'PR #31 opened', agent: 'claude', repo: 'omniloop' }
  ],
  projects: [
    { id: 'construct', on: true, repos: [['https://github.com/permissionBRICK/construct.git', 'construct']], sdks: 'dotnet = 10\nnode = 22\npwsh = 7.5', mcp: '[]', pkgs: 'jq\nshellcheck', cmds: 'cd /root/repos/construct/extension && npm install', dirty: '3 uncommitted files' },
    { id: 'omniloop', on: true, repos: [['https://github.com/permissionBRICK/omniloop.git', 'omniloop']], sdks: 'node = 22\npython = 3.12', mcp: '[\n  { "name": "omniloop", "url": "http://localhost:7070/mcp" }\n]', pkgs: '', cmds: 'cd /root/repos/omniloop && npm install', dirty: '1 unpushed commit' },
    { id: 'gitgudlab', on: true, repos: [['https://gitlab.example.local/tools/gitgudlab.git', 'gitgudlab']], sdks: 'go = 1.24', mcp: '[]', pkgs: '', cmds: 'cd /root/repos/gitgudlab && go mod download' },
    { id: 'emili-simulator', on: false, repos: [['https://github.com/permissionBRICK/emili-simulator.git', '']], sdks: 'python = 3.12, 3.13', mcp: '[]', pkgs: 'ffmpeg', cmds: '' },
    { id: 'jarvis', on: true, isNew: true, repos: [['https://github.com/permissionBRICK/jarvis.git', 'jarvis']], sdks: 'node = 22', mcp: '[]', pkgs: '', cmds: '' }
  ],
  sync: { last: '3 min ago', newFromVm: 1, remotes: [['github.com/permissionBRICK/construct-config', 'in sync']] },
  services: [
    { id: 'serveweb', name: 'VS Code serve-web', port: ':8000', on: true, applies: 'reprov', desc: 'Browser IDE, token-gated port 8000' },
    { id: 't3', name: 'T3 Code web', port: ':5177', on: true, applies: 'live', desc: 'Browser control plane for the coding agents' },
    { id: 'opencode', name: 'OpenCode serve', port: ':4096', on: true, applies: 'reprov', desc: 'Headless OpenCode server' },
    { id: 'smb', name: 'SMB share', port: 'Z:', on: true, applies: 'reprov', desc: 'Workspace share mapped to drive Z:' },
    { id: 'tunnel', name: 'VS Code tunnel', port: 'vscode.dev', on: false, applies: 'reprov', desc: 'No inbound port; needs a device sign-in' }
  ],
  usage: { today: 4.12, split: [['claude', 3.40], ['codex', 0.58], ['opencode', 0.14]], month: 86.30, all: 412.77,
    week: [['Thu', 9.8], ['Fri', 12.4], ['Sat', 6.1], ['Sun', 14.0], ['Mon', 11.3], ['Tue', 8.7], ['Wed', 4.1]] },

  /* ---------------- Shared host "buildbox" ---------------- */
  host: {
    name: 'buildbox', version: '1.14.2', latest: '1.15.0', cores: 32, ram: 128, resident: 96, committed: 142, admission: 118,
    pressure: { state: 'elevated', elevatedAt: 85, criticalAt: 110, lastSave: '22 min ago', lastSaveVm: 'mara/scratch-3' },
    capacityMode: 'enforce', disk: { name: 'D:', size: '3.6 TB', pct: 61 }
  },
  owners: [
    { id: 'host', label: 'Host', kind: 'system' },
    { id: 'alice', label: 'alice', kind: 'user', allowRam: 32 },
    { id: 'dana', label: 'dana', kind: 'user', allowRam: null },
    { id: 'bob', label: 'bob', kind: 'user', allowRam: 24 },
    { id: 'mara', label: 'mara', kind: 'user', allowRam: 16 },
    { id: 'unmanaged', label: 'Unmanaged', kind: 'system' }
  ],
  /* state: busy | idle | creating | overdue | saved-idle | saved-pressure | unknown | reserve */
  hvms: [
    { id: 'host/os', owner: 'host', name: 'Windows host + constructd', gb: 12, state: 'reserve', resident: true, idle: null },
    { id: 'alice/work-vm', owner: 'alice', name: 'work-vm', gb: 16, state: 'busy', resident: true, idle: 0, vcpu: 8, demand: 13.1, cost: 141.20, kind: 'primary' },
    { id: 'alice/bench', owner: 'alice', name: 'bench', gb: 12, state: 'idle', resident: true, idle: 124, vcpu: 4, demand: 3.2, cost: 58.90, kind: 'primary' },
    { id: 'alice/win11-eval', owner: 'alice', name: 'win11-eval', gb: 4, state: 'creating', resident: true, idle: null, vcpu: 4, demand: 2.1, cost: 12.30, kind: 'child', parent: 'alice/work-vm', job: 62 },
    { id: 'dana/agent-vm-2', owner: 'dana', name: 'agent-vm-2', gb: 16, state: 'idle', resident: true, idle: 35, vcpu: 8, demand: 9.6, cost: 61.10, kind: 'primary' },
    { id: 'dana/dev-2', owner: 'dana', name: 'dev-2', gb: 12, state: 'saved-idle', resident: false, idle: 101, vcpu: 4, cost: 25.20, kind: 'primary' },
    { id: 'bob/api-vm', owner: 'bob', name: 'api-vm', gb: 12, state: 'idle', resident: true, idle: 18, vcpu: 6, demand: 7.8, cost: 49.60, kind: 'primary' },
    { id: 'bob/win-qa', owner: 'bob', name: 'win-qa', gb: 8, state: 'overdue', resident: true, idle: 70, vcpu: 2, demand: 5.4, cost: 4.50, kind: 'child', parent: 'bob/api-vm', lease: '2h overdue' },
    { id: 'mara/scratch-3', owner: 'mara', name: 'scratch-3', gb: 16, state: 'saved-pressure', resident: false, idle: 64, vcpu: 4, cost: 139.90, kind: 'primary' },
    { id: 'unmanaged/sql-lab', owner: 'unmanaged', name: 'sql-lab', gb: 16, state: 'unmanaged', resident: true, idle: null, note: 'Hyper-V VM not created by Construct' },
    { id: 'unmanaged/old-runner', owner: 'unmanaged', name: 'old-runner', gb: 18, state: 'unknown', resident: false, idle: null, note: 'Inventory incomplete: configuration file unreadable' }
  ],
  users: [
    { id: 'alice', role: 'dev', prim: [2, 3], child: [1, 4], ram: [28, 32], vcpu: [12, 16], cost: 212.40, tools: [178.10, 30.20, 4.10], flags: [] },
    { id: 'bob', role: 'dev', prim: [1, 2], child: [2, 2], ram: [20, 24], vcpu: [8, 8], cost: 54.10, tools: [31.00, 20.40, 2.70], flags: ['lease overdue'] },
    { id: 'dana', role: 'admin', prim: [2, null], child: [1, null], ram: [28, null], vcpu: [12, null], cost: 86.30, tools: [71.20, 12.90, 2.20], flags: [] },
    { id: 'mara', role: 'contractor', prim: [1, 1], child: [1, 1], ram: [16, 16], vcpu: [4, 4], cost: 139.90, tools: [96.50, 43.40, 0], flags: ['legacy credential'] },
    { id: 'jonas', role: 'dev', prim: [0, 2], child: [0, 2], ram: [0, 24], vcpu: [0, 8], cost: 0, tools: [0, 0, 0], flags: ['disabled'] }
  ],
  userDefaults: { ram: 24, vcpu: 8 },
  jobs: [
    { id: 'j1', text: 'Create child win11-eval for alice', state: 'running', pct: 62, started: '13:51', vm: 'alice/win11-eval' },
    { id: 'j2', text: 'Reprovision dev-2', state: 'queued', started: '—', vm: 'dana/dev-2' },
    { id: 'j3', text: 'Delete child bob/win-qa', state: 'failed', started: '09:14', vm: 'bob/win-qa', err: 'Access denied (0x80070005) removing the VHDX; a backup agent held a handle' }
  ],
  audit: [
    ['13:51', 'alice', 'created child win11-eval'],
    ['13:48', 'memory-pressure', 'saved mara/scratch-3'],
    ['13:22', 'dana', 'rotated token for mara'],
    ['12:40', 'dana', 'set idle override on bob/api-vm (save after 30 min)'],
    ['11:05', 'bob', 'extended lease of win-qa by 4h'],
    ['09:14', 'system', 'delete child bob/win-qa failed: access denied'],
    ['08:30', 'dana', 'staged constructd 1.15.0 (not applied)']
  ],
  licences: {
    keys: [
      { id: 'A', type: 'MAK', used: 7, of: 10, bound: ['bob/win-qa'] },
      { id: 'B', type: 'MAK', used: 2, of: 5, bound: [] },
      { id: 'R', type: 'Retail', used: 1, of: 1, bound: ['retained identity'] }
    ],
    guests: [
      { vm: 'alice/win11-eval', state: 'grace', detail: 'grace period, 29 days left', key: '—' },
      { vm: 'bob/win-qa', state: 'activated', detail: 'activated', key: 'MAK A' },
      { vm: 'retained: dana/win-smoke', state: 'retained', detail: 'machine identity kept for reuse (opt-in)', key: 'Retail' }
    ],
    failure: { when: '2 days ago', code: '0xC004C008', text: 'key has exceeded its unlock limit', vm: 'alice/win11-eval (earlier attempt)' }
  },
  media: {
    isos: [['Ubuntu 26.04 LTS server', '3.1 GB', 'current'], ['Ubuntu 24.04.3 LTS server', '2.9 GB', '']],
    child: [['Windows 11 24H2 evaluation', '6.2 GB', '90-day eval'], ['Windows Server 2025 evaluation', '5.8 GB', '180-day eval']]
  }
};
