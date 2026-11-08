const { icon, hydrateIcons, toast, dialog } = window.mockup;

const state = {
  page: 'timeline',
  filter: 'all',
  selectedEvent: 'vault-request',
  machine: 'agent-vm',
  workspace: 'Construct',
  vaultTab: 'requests',
  vaultItem: 'request-release',
  hostTab: 'overview',
  hostItem: 'service',
  settingsScope: 'vm',
  dirty: false,
  requestsPending: 2,
  resolvedRequests: new Set(),
};

const events = [
  { id: 'vault-request', kind: 'decisions', glyph: 'key', type: 'Vault / decision', time: '09:42 left', title: 'build-vm asks for 2 secrets', copy: 'github-release and staging-db · 10 uses for 2 hours', tag: 'APPROVAL NEEDED', tone: 'action' },
  { id: 'child-overdue', kind: 'decisions', glyph: 'alert', type: 'Host / lease', time: '13:48', title: 'linux-test did not shut down', copy: 'Graceful shutdown timed out. The host has not forced it off.', tag: 'OVERDUE', tone: 'bad' },
  { id: 'reprovision', kind: 'changes', glyph: 'refresh', type: 'VM / lifecycle', time: '13:32', title: 'agent-vm can be reprovisioned', copy: 'Installed Construct is one release ahead. VM data stays in place.', tag: 'UPDATE READY', tone: 'action' },
  { id: 'forward', kind: 'changes', glyph: 'link', type: 'Network / forward', time: '13:04', title: 'Web preview is open on localhost:5173', copy: 'agent-vm:5173 · claimed by this Companion', tag: 'OPEN', tone: 'good' },
  { id: 'sync', kind: 'jobs', glyph: 'git', type: 'Config / sync', time: '12:58', title: 'Six profiles synchronized', copy: 'Host and agent-vm match. No conflicts or validation problems.', tag: 'COMPLETE', tone: 'good' },
  { id: 'cleanup', kind: 'jobs', glyph: 'shield', type: 'Vault / cleanup', time: '12:12', title: 'Agent logs redacted on agent-vm', copy: 'No files outside agent logs contained the released value.', tag: 'SCRUB COMPLETE', tone: 'good' },
  { id: 'agent-update', kind: 'changes', glyph: 'download', type: 'Agents / update', time: '11:46', title: 'T3 Code 0.1.1 is available', copy: 'Quick update includes the matching Windows Desktop build.', tag: 'QUICK UPDATE', tone: 'action' },
  { id: 'usage', kind: 'jobs', glyph: 'activity', type: 'Usage / report', time: '11:15', title: 'Host usage report received', copy: '18.4M tokens today · $42.18 estimated across three agents', tag: 'REPORTED', tone: '' },
];

const page = document.getElementById('page');

function split(list, detail) {
  return `<div class="split-workspace"><section class="stream-pane">${list}</section><section class="detail-pane">${detail}</section></div>`;
}

function detailHeader(eyebrow, title, copy, glyph = 'activity', actions = '') {
  return `<div class="detail-eyebrow">${icon(glyph)}${eyebrow}</div><div class="detail-title"><div><h1>${title}</h1><p>${copy}</p></div></div>${actions ? `<div class="detail-actions">${actions}</div>` : ''}`;
}

function timelineList() {
  const shown = events.filter((event) => !state.resolvedRequests.has(event.id === 'vault-request' ? 'request-release' : event.id)).filter((event) => state.filter === 'all' || event.kind === state.filter);
  return `<div class="stream-head"><div class="stream-title-row"><h1>Live timeline</h1><small>${shown.length} EVENTS</small></div><p class="stream-description">Jobs, changes, and decisions across every connected instance. Select an event to inspect it without leaving the stream.</p><div class="filter-row">${[['all', 'ALL'], ['decisions', 'DECISIONS'], ['jobs', 'JOBS'], ['changes', 'CHANGES']].map(([id, label]) => `<button class="${state.filter === id ? 'active' : ''}" data-filter="${id}">${label}</button>`).join('')}</div></div><div class="stream-list">${shown.map((event) => `<button class="event-row ${state.selectedEvent === event.id ? 'selected' : ''}" data-event="${event.id}"><span class="event-top">${icon(event.glyph)}<span class="event-type">${event.type}</span><time class="event-time">${event.time}</time></span><h3>${event.title}</h3><p>${event.copy}</p><span class="event-footer"><span class="event-tag ${event.tone}">${event.tag}</span>${icon('chevronRight')}</span></button>`).join('')}</div>`;
}

function timelineDetail() {
  const id = state.selectedEvent;
  if (id === 'vault-request') return `<div class="detail-inner">${detailHeader('Decision / expires in 09:42', 'Release access on build-vm', 'An agent in a T3 Code release thread requests two secrets. Values stay out of this interface.', 'key', `<button class="secondary-action" data-action="adjust-request">LIMIT ACCESS</button><button class="secondary-action" data-request-decision="deny">DENY</button><button class="primary-action" data-request-decision="approve">${icon('check')}APPROVE</button>`)}<section class="data-panel"><div class="data-panel-head"><h2>Requested secrets</h2><small>2 ITEMS</small></div><div class="data-panel-body"><div class="secret-line">${icon('key')}<strong>github-release</strong><small>Release repository token</small></div><div class="secret-line">${icon('key')}<strong>staging-db</strong><small>Staging database administrator</small></div><div class="reason-code">REASON
“Deploy the release candidate and run the database migration.”</div></div></section><section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Access</small><strong>10 uses · 2 hours</strong></div><div class="data-cell"><small>Target</small><strong>build-vm only</strong></div><div class="data-cell"><small>Requesting user</small><strong>Local user · T3 Code Desktop</strong></div><div class="data-cell"><small>When access ends</small><strong>Automatic agent-log scrub</strong></div></div></section><div class="notice-line">${icon('info')}Approving replaces any existing lease for the same VM and secret. The first answer from this PC, T3 Desktop, or a paired phone wins.</div></div>`;
  if (id === 'child-overdue') return `<div class="detail-inner">${detailHeader('Host / lease overdue', 'linux-test is still running', 'The wall-clock lease expired. The service requested a graceful guest shutdown, but integration services did not complete it.', 'alert', `<button class="secondary-action" data-action="open-host-job">OPEN JOB</button><button class="primary-action" data-action="retry-shutdown">${icon('refresh')}RETRY GRACEFUL SHUTDOWN</button>`)}<div class="notice-line bad">${icon('alert')}No force-off, save, or delete fallback ran. Runtime CPU and RAM remain charged to the owner.</div><section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Parent</small><strong>build-vm</strong></div><div class="data-cell"><small>Sharing</small><strong>Private</strong></div><div class="data-cell"><small>Lease expired</small><strong>13:45 · 3 minutes overdue</strong></div><div class="data-cell"><small>Last outcome</small><strong>Guest shutdown timed out</strong></div></div></section><section class="data-panel"><div class="data-panel-head"><h2>Job output</h2><small>CREDENTIAL SAFE</small></div><div class="data-panel-body code-block">13:45:00  lease expired; graceful shutdown requested
13:47:30  waiting for Hyper-V terminal Off state
13:48:00  deadline reached; guest still Running
13:48:00  marked lease overdue; retry remains available</div></section></div>`;
  if (id === 'reprovision') return `<div class="detail-inner">${detailHeader('Lifecycle / update ready', 'Reprovision agent-vm', 'Apply the Construct installed on this PC while preserving repositories, auth, and VM data.', 'refresh', `<button class="secondary-action" data-action="compare-release">COMPARE RELEASE</button><button class="primary-action" data-action="run-reprovision">${icon('refresh')}REVIEW & RUN</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>VM provisioned</small><strong>main@a81d2f</strong></div><div class="data-cell"><small>PC installed</small><strong>main@b72c91</strong></div><div class="data-cell"><small>Profiles</small><strong>4 selected for this run</strong></div><div class="data-cell"><small>Data policy</small><strong>No VM deletion</strong></div></div></section><section class="data-panel"><div class="data-panel-head"><h2>Expected changes</h2></div><table class="compact-table"><tbody><tr><td><strong>Construct runtime</strong><small>Scripts and guest services updated</small></td><td><span class="status-label warn"><i></i>RESTART SERVICES</span></td></tr><tr><td><strong>T3 Code</strong><small>Matching Desktop build installed if needed</small></td><td><span class="status-label warn"><i></i>RESUME THREADS</span></td></tr><tr><td><strong>Repositories and auth</strong><small>Existing working trees and sign-ins</small></td><td><span class="status-label good"><i></i>PRESERVED</span></td></tr></tbody></table></section></div>`;
  if (id === 'forward') return `<div class="detail-inner">${detailHeader('Network / client forward', 'Web preview', 'An agent requested vm:5173. This Companion owns the tunnel and retries the same local port after a disconnect.', 'link', `<button class="secondary-action" data-action="copy-link">${icon('copy')}COPY LINK</button><button class="primary-action" data-action="open-link">${icon('arrowUpRight')}OPEN LOCALHOST:5173</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Source</small><strong>agent-vm:5173</strong></div><div class="data-cell"><small>Destination</small><strong>127.0.0.1:5173</strong></div><div class="data-cell"><small>State</small><strong>Open · SSH tunnel healthy</strong></div><div class="data-cell"><small>Owner</small><strong>This Companion window</strong></div></div></section><div class="notice-line good">${icon('check')}Loopback only. Other devices on the LAN cannot reach this forward.</div><div class="detail-actions"><button class="danger-action" data-action="close-forward">${icon('x')}CLOSE FORWARD</button></div></div>`;
  if (id === 'sync') return `<div class="detail-inner">${detailHeader('Configuration / sync complete', 'Host and VM profiles match', 'The sync tick compared VM edits, host history, remote tracking state, and selected profiles.', 'git', `<button class="secondary-action" data-action="open-config">${icon('folder')}OPEN CONFIG REPO</button><button class="primary-action" data-action="run-sync">${icon('refresh')}SYNC AGAIN</button>`)}<section class="data-panel"><div class="data-panel-head"><h2>Profiles in this snapshot</h2><small>6 TOTAL / 4 SELECTED</small></div><table class="compact-table"><tbody>${['Construct', 'Shopfront', 'Mobile notes', 'Documentation', 'Data lab', 'Legacy tools'].map((name, index) => `<tr><td><strong>${name}</strong><small>${index < 4 ? 'Selected for next provision' : 'Available, not selected'}</small></td><td><span class="status-label good"><i></i>SYNCED</span></td></tr>`).join('')}</tbody></table></section><div class="notice-line good">${icon('check')}No merge conflicts. Four profiles selected for agent-vm’s next provision.</div></div>`;
  if (id === 'cleanup') return `<div class="detail-inner">${detailHeader('Vault / cleanup complete', 'Agent logs redacted', 'The previous lease ended and the scrub searched the VM for exact, JSON-escaped, URL-encoded, and Basic-auth forms.', 'shield')}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>VM</small><strong>agent-vm</strong></div><div class="data-cell"><small>Agent logs</small><strong>Redacted automatically</strong></div><div class="data-cell"><small>Other files</small><strong>No matches</strong></div><div class="data-cell"><small>Git history</small><strong>Not rewritten</strong></div></div></section><div class="notice-line good">${icon('check')}No file decisions remain. A value committed to a repository or remote still needs separate handling.</div></div>`;
  if (id === 'agent-update') return `<div class="detail-inner">${detailHeader('Agents / quick update', 'T3 Code 0.1.1', 'Update the VM server and the matching Desktop app without a full reprovision.', 'download', `<button class="secondary-action" data-action="release-notes">RELEASE NOTES</button><button class="primary-action" data-action="update-t3">${icon('download')}UPDATE T3 CODE</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Current</small><strong>0.1.0 · Stable</strong></div><div class="data-cell"><small>Available</small><strong>0.1.1 · Construct build</strong></div><div class="data-cell"><small>Server</small><strong>agent-vm</strong></div><div class="data-cell"><small>Desktop</small><strong>This PC · Matching build</strong></div></div></section><div class="notice-line">${icon('info')}The server restarts. If startup fails, the launcher returns to the previous working build and Desktop is not changed.</div></div>`;
  return `<div class="detail-inner">${detailHeader('Usage / report received', '18.4M tokens today', 'Exact token counts from ccusage. The $42.18 total is an estimate from the collector’s price table.', 'activity', `<button class="secondary-action" data-action="export-usage">${icon('download')}EXPORT JSON</button>`)}<section class="data-panel"><div class="machine-cards"><div class="machine-stat"><small>CLAUDE CODE</small><strong>68%</strong><div class="progress-track"><i style="width:68%"></i></div></div><div class="machine-stat"><small>CODEX</small><strong>22%</strong><div class="progress-track"><i style="width:22%"></i></div></div><div class="machine-stat"><small>OPENCODE</small><strong>10%</strong><div class="progress-track"><i style="width:10%"></i></div></div></div></section><section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Input / output</small><strong>6.4M / 2.1M</strong></div><div class="data-cell"><small>Cached</small><strong>9.9M</strong></div><div class="data-cell"><small>Active agent time</small><strong>6h 24m</strong></div><div class="data-cell"><small>Last report</small><strong>11:15 · Host collector</strong></div></div></section></div>`;
}

function timelinePage() {
  return split(timelineList(), timelineDetail());
}

const machines = [
  ['agent-vm', 'Local Hyper-V', 'ONLINE', '4 vCPU · 8 GB · Ubuntu 24.04'],
  ['build-vm', 'host.example', 'ONLINE', '4 vCPU · 8 GB · Save after 45m'],
  ['test-vm', 'Local Hyper-V', 'OFF', '2 vCPU · 4 GB · Offline scrub queued'],
  ['windows-test', 'Child of build-vm', 'OFF', '4 vCPU · 8 GB · Shared host-wide'],
];

function machineDetail() {
  const machine = machines.find((item) => item[0] === state.machine) || machines[0];
  const offline = machine[2] === 'OFF';
  return `<div class="detail-inner">${detailHeader(`${machine[1]} / ${machine[2]}`, machine[0], offline ? 'The VM is off. Saved configuration and queued requests remain available.' : 'SSH is reachable. Live services, resources, and pending updates are shown below.', 'server', `<button class="secondary-action" data-action="machine-console">${icon('monitor')}CONSOLE</button><button class="secondary-action" data-action="machine-power">${icon('power')}${offline ? 'START' : 'SHUT DOWN'}</button><button class="primary-action" data-action="machine-open">${icon('arrowUpRight')}${offline ? 'START & CONNECT' : 'OPEN WORKSPACE'}</button>`)}<section class="machine-cards"><div class="machine-stat"><small>COMPUTE</small><strong>${machine[3].split(' · ')[0]}</strong><div class="progress-track"><i style="width:45%"></i></div></div><div class="machine-stat"><small>MEMORY</small><strong>${machine[3].split(' · ')[1]}</strong><div class="progress-track"><i style="width:38%"></i></div></div><div class="machine-stat"><small>STORAGE</small><strong>68 / 128 GB</strong><div class="progress-track"><i style="width:53%"></i></div></div></section><section class="data-panel"><div class="data-panel-head"><h2>Runtime and lifecycle</h2><button class="text-action" data-vm-settings="${machine[0]}">EDIT SETTINGS ${icon('arrowRight')}</button></div><div class="data-grid"><div class="data-cell"><small>Construct</small><strong>${offline ? 'main@a81d2f · Cached' : 'main@a81d2f · Update ready'}</strong></div><div class="data-cell"><small>Idle policy</small><strong>Save after 45 minutes</strong></div><div class="data-cell"><small>Microphone</small><strong>${offline ? 'Not armed while offline' : 'Armed · Capture on demand'}</strong></div><div class="data-cell"><small>Automatic checkpoints</small><strong>Off · Manual snapshots preserved</strong></div></div></section><section class="data-panel"><div class="data-panel-head"><h2>Agents and access</h2><small>${offline ? 'CACHED STATE' : 'LIVE'}</small></div><table class="compact-table"><tbody><tr><td><strong>Claude Code</strong><small>2.0.31 · Authenticated</small></td><td><span class="status-label good"><i></i>CURRENT</span></td></tr><tr><td><strong>Codex</strong><small>0.31.0 · Authenticated</small></td><td><span class="status-label good"><i></i>CURRENT</span></td></tr><tr><td><strong>T3 Code</strong><small>0.1.0 · Stable · HTTPS</small></td><td><button class="text-action" data-action="update-t3">UPDATE ${icon('arrowRight')}</button></td></tr><tr><td><strong>Port forwards</strong><small>Web preview and API server</small></td><td><span class="status-label ${offline ? '' : 'good'}"><i></i>${offline ? 'RETRY ON START' : '2 OPEN'}</span></td></tr></tbody></table></section><div class="detail-actions"><button class="secondary-action" data-action="run-reprovision">REPROVISION</button><button class="secondary-action" data-action="custom-reinstall">CUSTOM REINSTALL</button><button class="danger-action" data-action="remove-machine">REMOVE INSTANCE</button></div></div>`;
}

function machinesPage() {
  const list = `<div class="stream-head"><div class="stream-title-row"><h1>Machines</h1><small>${machines.length} REGISTERED</small></div><p class="stream-description">One list for local VMs, remote primaries, and visible children. The inspector owns every action.</p></div><div class="list-pane-header"><label class="list-search">${icon('search')}<input placeholder="Filter machines"></label></div><div class="stream-list">${machines.map(([name, location, status, meta]) => `<button class="machine-list-item ${state.machine === name ? 'selected' : ''}" data-machine="${name}"><span class="list-item-top">${icon('server')}<strong>${name}</strong><span class="status-label ${status === 'ONLINE' ? 'good' : ''}"><i></i>${status}</span></span><p>${location}</p><span class="list-item-meta"><span>${meta}</span></span></button>`).join('')}</div>`;
  return split(list, machineDetail());
}

const workspaces = [
  ['Construct', '2 repositories', 'PowerShell · .NET 10 · Node'],
  ['Shopfront', '1 repository', 'Node 22 · pnpm · Docker'],
  ['Mobile notes', '2 repositories', 'Flutter · Android SDK'],
  ['Documentation', '1 repository', 'Node 22 · Markdown'],
  ['Data lab', '1 repository', 'Python 3.12 · uv'],
];

function workspaceDetail() {
  return `<div class="detail-inner">${detailHeader('Project profile / synced', state.workspace, 'A reusable definition of repositories, runtimes, MCP servers, host packages, forwards, and setup commands.', 'folder', `<button class="secondary-action" data-action="edit-workspace">${icon('edit')}EDIT PROFILE</button><button class="primary-action" data-action="open-workspace">${icon('arrowUpRight')}OPEN WORKSPACE</button>`)}<div class="notice-line good">${icon('check')}Selected for agent-vm’s next provision. The profile itself is already synced.</div><section class="data-panel"><div class="data-panel-head"><h2>Profile contents</h2><small>VALIDATED JSON</small></div><table class="compact-table"><tbody><tr><td><strong>Repositories</strong><small>/root/repos/${state.workspace.toLowerCase().replaceAll(' ', '-')}</small></td><td>${state.workspace === 'Construct' ? '2 checkouts' : '1 checkout'}</td></tr><tr><td><strong>Runtimes</strong><small>Declared as project SDK requirements</small></td><td>Current</td></tr><tr><td><strong>Provision commands</strong><small>Idempotent, repeated on every provision</small></td><td>3 commands</td></tr><tr><td><strong>MCP servers</strong><small>Written into each coding agent’s own config</small></td><td>2 servers</td></tr><tr><td><strong>Persistent forwards</strong><small>Requested without blocking provision</small></td><td>2 ports</td></tr></tbody></table></section><section class="data-panel"><div class="data-panel-head"><h2>Sync and sharing</h2><small>HOST TRUTH + VM BRANCH</small></div><div class="data-grid"><div class="data-cell"><small>Host</small><strong>Synced 4 minutes ago</strong></div><div class="data-cell"><small>VM</small><strong>No unpublished edits</strong></div><div class="data-cell"><small>Remote tracking</small><strong>team/agent-configs</strong></div><div class="data-cell"><small>Sharing</small><strong>One-liner available</strong></div></div></section><div class="detail-actions"><button class="secondary-action" data-action="run-sync">${icon('git')}SYNC NOW</button><button class="secondary-action" data-action="share-profile">${icon('upload')}SHARE</button><button class="secondary-action" data-action="push-profile">${icon('git')}PUSH FOR REVIEW</button></div></div>`;
}

function workspacesPage() {
  const list = `<div class="stream-head"><div class="stream-title-row"><h1>Workspaces</h1><small>${workspaces.length} PROFILES</small></div><p class="stream-description">Open a checkout or edit the profile that recreates it. Selection stays separate from sync.</p><button class="primary-action" data-action="add-workspace" style="margin-top:12px">${icon('plus')}ADD PROJECT</button></div><div class="list-pane-header"><label class="list-search">${icon('search')}<input placeholder="Filter workspaces"></label></div><div class="stream-list">${workspaces.map(([name, repos, stack]) => `<button class="workspace-list-item ${state.workspace === name ? 'selected' : ''}" data-workspace="${name}"><span class="list-item-top">${icon('folder')}<strong>${name}</strong><span class="workspace-open">${icon('chevronRight')}</span></span><p>${stack}</p><span class="list-item-meta"><span>${repos}</span><span>SELECTED</span></span></button>`).join('')}</div>`;
  return split(list, workspaceDetail());
}

const vaultItems = {
  requests: [['request-release', 'build-vm · 2 secrets', '10 uses · 2 hours'], ['request-publish', 'agent-vm · npm-publish', '1 use · 10 minutes']],
  secrets: [['github-release', 'Release repository token', '1 active lease'], ['staging-db', 'Staging database administrator', '1 active lease'], ['npm-read', 'Read-only package mirror', 'Auto-approved'], ['ssh-deploy', 'Deployment private key', 'No active lease']],
  access: ['github-release', 'staging-db'].map((name) => [name, 'build-vm · 8 uses left', 'Ends today at 16:30']),
  hosts: [['host.example', 'Always available · 24 secrets', '1 paired phone'], ['secondary-host.example', 'Locked to my PC · 24 secrets', 'No paired phones']],
};

function vaultList() {
  const items = vaultItems[state.vaultTab].filter(([id]) => !state.resolvedRequests.has(id));
  const content = items.length ? items.map(([id, line, meta]) => `<button class="vault-list-item ${state.vaultItem === id ? 'selected' : ''}" data-vault-item="${id}"><span class="list-item-top">${icon(state.vaultTab === 'hosts' ? 'network' : state.vaultTab === 'access' ? 'clock' : 'key')}<strong>${id.startsWith('request-') ? (id === 'request-release' ? 'Release deployment' : 'One-time publish') : id}</strong></span><p>${line}</p><span class="list-item-meta"><span>${meta}</span></span></button>`).join('') : '<div style="padding:30px 18px;color:var(--green);font-size:11px">No requests waiting.</div>';
  return `<div class="stream-head"><div class="stream-title-row"><h1>Key vault</h1><small>DPAPI / LOCAL</small></div><p class="stream-description">Requests, secret metadata, active leases, and host trust. Values never enter this webview.</p><div class="subtabs">${[['requests', 'REQUESTS'], ['secrets', 'SECRETS'], ['access', 'ACCESS'], ['hosts', 'HOSTS']].map(([id, label]) => `<button class="${state.vaultTab === id ? 'active' : ''}" data-vault-tab="${id}">${label}</button>`).join('')}</div></div><div class="list-pane-header"><button class="primary-action" style="width:100%" data-action="${state.vaultTab === 'secrets' ? 'add-secret' : state.vaultTab === 'hosts' ? 'sync-hosts' : 'refresh-vault'}">${icon(state.vaultTab === 'secrets' ? 'plus' : 'refresh')}${state.vaultTab === 'secrets' ? 'ADD SECRET' : state.vaultTab === 'hosts' ? 'SYNC HOSTS' : 'REFRESH'}</button></div><div class="stream-list">${content}</div>`;
}

function vaultDetail() {
  const item = state.vaultItem;
  if (state.vaultTab === 'requests' && state.resolvedRequests.has(item)) return `<div class="detail-inner">${detailHeader('Approval queue / clear', 'No requests waiting', 'New agent requests appear here, in the timeline, and beside the Windows tray icon.', 'check')}</div>`;
  if (state.vaultTab === 'requests') return `<div class="detail-inner">${detailHeader('Approval / oldest first', item === 'request-release' ? 'Release deployment access' : 'One-time package publish', item === 'request-release' ? 'build-vm asks for github-release and staging-db.' : 'agent-vm asks to read npm-publish once.', 'key', `<button class="secondary-action" data-action="adjust-request">LIMIT</button><button class="secondary-action" data-request-decision="deny">DENY</button><button class="primary-action" data-request-decision="approve">${icon('check')}APPROVE</button>`)}<section class="data-panel"><div class="data-panel-body">${item === 'request-release' ? '<div class="secret-line">' + icon('key') + '<strong>github-release</strong><small>Release repository token</small></div><div class="secret-line">' + icon('key') + '<strong>staging-db</strong><small>Staging database administrator</small></div>' : '<div class="secret-line">' + icon('key') + '<strong>npm-publish</strong><small>Test registry publishing</small></div>'}<div class="reason-code">AGENT REASON
“${item === 'request-release' ? 'Deploy the release candidate and run the database migration.' : 'Publish the patched package to the test registry.'}”</div></div></section><section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Access</small><strong>${item === 'request-release' ? '10 uses · 2 hours' : '1 use · 10 minutes'}</strong></div><div class="data-cell"><small>Destination</small><strong>${item === 'request-release' ? 'build-vm' : 'agent-vm'} only</strong></div><div class="data-cell"><small>After expiry</small><strong>Automatic cleanup</strong></div><div class="data-cell"><small>Other destinations</small><strong>T3 Desktop · Paired phone</strong></div></div></section></div>`;
  if (state.vaultTab === 'secrets') return `<div class="detail-inner">${detailHeader('Secret / value hidden', item, 'Agents see this name and description. Values and usernames are copied only through native dialogs.', 'key', `<button class="secondary-action" data-action="copy-secret">${icon('copy')}COPY SECRET</button><button class="primary-action" data-action="edit-secret">${icon('edit')}EDIT</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Description</small><strong>${item === 'github-release' ? 'Release repository token' : 'Credential for automated agent tasks'}</strong></div><div class="data-cell"><small>Username</small><strong>service-account · Native copy only</strong></div><div class="data-cell"><small>Approval policy</small><strong>${item === 'npm-read' ? 'Auto-approved on every VM' : 'Ask for each lease'}</strong></div><div class="data-cell"><small>Active access</small><strong>${item === 'github-release' || item === 'staging-db' ? '1 VM holds a lease' : 'No active lease'}</strong></div></div></section><div class="notice-line">${icon('alert')}Changing the value revokes all leases and scrubs VMs that saw the old value. Metadata edits keep access.</div><div class="detail-actions"><button class="secondary-action" data-action="toggle-auto">${icon('shield')}${item === 'npm-read' ? 'DISABLE AUTO-APPROVE' : 'ENABLE AUTO-APPROVE'}</button><button class="danger-action" data-action="delete-secret">${icon('trash')}DELETE SECRET</button></div></div>`;
  if (state.vaultTab === 'access') return `<div class="detail-inner">${detailHeader('Active lease / one VM', item, 'The VM can read this secret until the use count or time limit ends. Revoking starts cleanup.', 'clock', `<button class="danger-action" data-action="revoke-lease">${icon('x')}REVOKE & SCRUB</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>VM</small><strong>build-vm</strong></div><div class="data-cell"><small>Uses left</small><strong>8 of 10</strong></div><div class="data-cell"><small>Expires</small><strong>Today at 16:30</strong></div><div class="data-cell"><small>Reason</small><strong>Release deployment</strong></div></div></section><section class="data-panel"><div class="data-panel-head"><h2>Cleanup after this lease</h2></div><div class="data-panel-body code-block">1. Agent logs are redacted without asking.
2. Other files are listed with the matching secrets.
3. Keep is the default. Redact and Delete need your decision.
4. A VM that is offline is scrubbed when it returns.</div></section></div>`;
  return `<div class="detail-inner">${detailHeader('Vault host / sync healthy', item, item === 'host.example' ? 'The host can answer approvals and scrub VMs while this PC is off.' : 'The host stores no vault key. This PC unlocks a VM while connecting.', 'network', `<button class="secondary-action" data-action="host-mode">CHANGE MODE</button><button class="primary-action" data-action="pair-phone">${icon('phone')}PAIR PHONE</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Mode</small><strong>${item === 'host.example' ? 'Always available' : 'Locked to my PC'}</strong></div><div class="data-cell"><small>Last sync</small><strong>4 minutes ago · 24 secrets</strong></div><div class="data-cell"><small>Approval page</small><strong>${item === 'host.example' ? 'vault.example.net' : 'Not configured'}</strong></div><div class="data-cell"><small>Paired devices</small><strong>${item === 'host.example' ? '1 phone' : 'No phones'}</strong></div></div></section><div class="notice-line">${icon('info')}Names and descriptions stay readable on a locked host. Values and usernames remain encrypted under your vault key.</div><div class="detail-actions"><button class="secondary-action" data-action="show-key">${icon('key')}SHOW VAULT KEY</button><button class="secondary-action" data-action="import-key">IMPORT KEY</button></div></div>`;
}

function vaultPage() {
  return split(vaultList(), vaultDetail());
}

const hostLists = {
  overview: [['service', 'constructd 1.8.0', 'Healthy · 2 active jobs'], ['capacity', 'Runtime capacity', '16 / 32 vCPU · 24 / 64 GB'], ['attention', '1 overdue child lease', 'Graceful shutdown timed out']],
  vms: [['build-vm', 'Primary · Local user', 'Running · 4 vCPU · 8 GB'], ['review-vm', 'Primary · Teammate', 'Running · 8 vCPU · 16 GB'], ['windows-test', 'Child · Shared host-wide', 'Off · Retained'], ['linux-test', 'Child · Private', 'Overdue shutdown']],
  users: [['local-user', 'Administrator', '1 / 4 primaries · 1 token'], ['teammate', 'User', '1 / 2 primaries · No tokens']],
  media: [['ubuntu', 'Ubuntu 24.04 LTS', 'Public ISO · Verified'], ['windows', 'Windows 11 Evaluation', 'Uploaded · 6.2 GB'], ['child-media', 'Child installation media', '2 items · 1 in use']],
  operations: [['shutdown-job', 'Graceful shutdown · linux-test', 'Failed · Retry available'], ['media-job', 'Ubuntu ISO verification', 'Complete'], ['audit', 'Audit log', 'Credential-safe events']],
  configuration: [['vault-config', 'Key vault', 'Approval page address'], ['capacity-config', 'Capacity policy', 'Observe / enforce budgets'], ['network-config', 'Networking', 'Per-VM public host names']],
  maintenance: [['current-service', 'constructd 1.8.0', 'Current and healthy'], ['available-update', 'constructd 1.8.2', 'Available to stage'], ['rollback', 'Rollback point', 'Database compatible']],
};

function hostDetail() {
  const item = state.hostItem;
  const tab = state.hostTab;
  if (tab === 'overview' && item === 'capacity') return `<div class="detail-inner">${detailHeader('Host / capacity', 'Runtime capacity', 'Budgets are enforced. Storage is observed and reported without blocking unrelated CPU or RAM changes.', 'cpu')}<section class="machine-cards"><div class="machine-stat"><small>MEMORY</small><strong>24 / 64 GB</strong><div class="progress-track"><i style="width:38%"></i></div></div><div class="machine-stat"><small>VCPU</small><strong>16 / 32</strong><div class="progress-track"><i style="width:50%"></i></div></div><div class="machine-stat"><small>STORAGE</small><strong>34%</strong><div class="progress-track"><i style="width:34%"></i></div></div></section></div>`;
  if (tab === 'vms') return `<div class="detail-inner">${detailHeader('Host inventory / VM', item, 'Owner, kind, sharing, resources, lease, and guest-reported Construct facts remain separate.', 'server', `<button class="primary-action" data-vm-settings="${item}">${icon('sliders')}VM SETTINGS</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Kind</small><strong>${item.includes('test') ? 'Child of build-vm' : 'Primary'}</strong></div><div class="data-cell"><small>Owner</small><strong>${item === 'review-vm' ? 'Teammate' : 'Local user'}</strong></div><div class="data-cell"><small>Resources</small><strong>4 vCPU · 8 GB fixed</strong></div><div class="data-cell"><small>Idle / lease</small><strong>${item === 'linux-test' ? 'OVERDUE · Still running' : 'Save after 45 min'}</strong></div></div></section><div class="notice-line">${icon('info')}Hardware edits wait for a full stop and start while the VM runs. Idle edits apply now. Guest update and reinstall stay in the instance panel.</div></div>`;
  if (tab === 'users') return `<div class="detail-inner">${detailHeader('Identity and allowance', item === 'local-user' ? 'Local user' : 'Teammate', 'Blank delegation fields inherit host defaults. Effective values are shown beside stored overrides.', 'users', `<button class="secondary-action" data-action="issue-token">${icon('key')}ISSUE TOKEN</button><button class="primary-action" data-action="edit-user">${icon('edit')}EDIT ALLOWANCE</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Role</small><strong>${item === 'local-user' ? 'Administrator' : 'User'}</strong></div><div class="data-cell"><small>Primary quota</small><strong>${item === 'local-user' ? '1 / 4' : '1 / 2'}</strong></div><div class="data-cell"><small>Child creation</small><strong>8 vCPU · 16 GB · 4 slots</strong></div><div class="data-cell"><small>Sharing</small><strong>Host-wide allowed</strong></div></div></section><div class="notice-line">${icon('key')}A new token is shown once in a native dialog with a Copy button. It is never stored or written to the audit log.</div></div>`;
  if (tab === 'configuration') return `<div class="detail-inner">${detailHeader('Validated JSON / independent section', item === 'vault-config' ? 'Key vault configuration' : item === 'capacity-config' ? 'Capacity policy' : 'Networking', 'Each section explains its keys and reports validation problems beside the editor.', 'file', `<button class="primary-action" data-action="save-config">${icon('check')}VALIDATE & SAVE</button>`)}<section class="data-panel"><div class="data-panel-head"><h2>Section JSON</h2><small>AGENTS AND VM TOKENS CANNOT EDIT</small></div><div class="data-panel-body code-block">${item === 'vault-config' ? '{\n  "webUrl": "https://vault.example.net"\n}' : item === 'capacity-config' ? '{\n  "mode": "enforce",\n  "maxCpu": 32,\n  "maxRamBytes": 68719476736\n}' : '{\n  "perVmPublicHostNames": true\n}'}</div></section><div class="notice-line good">${icon('check')}No validation problems. Saving one section does not overwrite another.</div></div>`;
  if (tab === 'maintenance') return `<div class="detail-inner">${detailHeader('Service maintenance', item === 'available-update' ? 'constructd 1.8.2 is ready to stage' : item === 'rollback' ? 'Database-compatible rollback point' : 'constructd 1.8.0 is healthy', 'Stage, apply, resume, cancel, or resolve service updates without mixing arbitrary versions.', 'refresh', item === 'available-update' ? `<button class="primary-action" data-action="stage-update">${icon('download')}STAGE UPDATE</button>` : '')}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Current service</small><strong>constructd 1.8.0</strong></div><div class="data-cell"><small>Health</small><strong>All required capabilities present</strong></div><div class="data-cell"><small>Maintenance state</small><strong>Off · Mutations available</strong></div><div class="data-cell"><small>Rollback</small><strong>Database compatible</strong></div></div></section><div class="notice-line">${icon('info')}During an update, the host banner shows its phase, mutations disable, and health is polled every five seconds.</div></div>`;
  if (item === 'attention' || item === 'linux-test' || item === 'shutdown-job') return `<div class="detail-inner">${detailHeader('Operations / action needed', 'Graceful shutdown timed out', 'linux-test remains running with an overdue lease. No forced fallback has run.', 'alert', `<button class="primary-action" data-action="retry-shutdown">${icon('refresh')}RETRY SHUTDOWN</button>`)}<div class="notice-line bad">${icon('alert')}Runtime resources remain allocated. Media and disk are retained.</div><section class="data-panel"><div class="data-panel-body code-block">13:45:00  lease expired
13:45:01  guest shutdown requested
13:48:00  timeout; terminal Off state not reached
13:48:00  lease marked overdue; explicit retry allowed</div></section></div>`;
  return `<div class="detail-inner">${detailHeader(`Host / ${tab}`, item === 'service' ? 'constructd is healthy' : item, 'The selected record opens in this inspector while the inventory remains visible.', 'network', `<button class="secondary-action" data-action="refresh-host">${icon('refresh')}REFRESH</button>`)}<section class="data-panel"><div class="data-grid"><div class="data-cell"><small>Service</small><strong>constructd 1.8.0</strong></div><div class="data-cell"><small>Health</small><strong>Healthy · API reachable</strong></div><div class="data-cell"><small>Machines</small><strong>5 primary · 3 children</strong></div><div class="data-cell"><small>Active jobs</small><strong>2 · 1 needs attention</strong></div></div></section><section class="data-panel"><div class="data-panel-head"><h2>Available actions</h2></div><div class="data-panel-body"><button class="secondary-action" data-action="new-vm">${icon('plus')}CREATE FIRST OR NEXT CONSTRUCT VM</button></div></section></div>`;
}

function hostPage() {
  const items = hostLists[state.hostTab];
  const list = `<div class="stream-head"><div class="stream-title-row"><h1>host.example</h1><small>ADMIN</small></div><p class="stream-description">The host owns these operations. Your own VM’s provision and reinstall stay under Machines.</p><div class="subtabs">${[['overview', 'HOME'], ['vms', 'VMS'], ['users', 'USERS'], ['media', 'MEDIA'], ['operations', 'OPS'], ['configuration', 'CONFIG'], ['maintenance', 'UPDATE']].map(([id, label]) => `<button class="${state.hostTab === id ? 'active' : ''}" data-host-tab="${id}">${label}</button>`).join('')}</div></div><div class="stream-list">${items.map(([id, line, meta]) => `<button class="machine-list-item ${state.hostItem === id ? 'selected' : ''}" data-host-item="${id}"><span class="list-item-top">${icon(state.hostTab === 'users' ? 'users' : state.hostTab === 'configuration' ? 'file' : 'server')}<strong>${id}</strong></span><p>${line}</p><span class="list-item-meta"><span>${meta}</span></span></button>`).join('')}</div>`;
  return split(list, hostDetail());
}

function setting(title, description, effect, control) {
  return `<div class="setting-line" data-search="${(title + ' ' + description).toLowerCase()}"><div class="setting-copy"><strong>${title}</strong><small>${description}</small><span class="effect-label">${effect}</span></div><div class="setting-input">${control}</div></div>`;
}

function settingsPage() {
  return `<div class="settings-layout"><div class="settings-inner">${detailHeader('Application / preferences', 'Settings', 'Dense, searchable controls with scope and effect attached to every row.', 'sliders')}<div class="settings-toolbar"><label class="list-search">${icon('search')}<input id="pulse-settings-search" placeholder="Filter settings"></label><div class="settings-scope"><button class="${state.settingsScope === 'vm' ? 'active' : ''}" data-settings-scope="vm">SELECTED VM</button><button class="${state.settingsScope === 'pc' ? 'active' : ''}" data-settings-scope="pc">THIS PC</button></div></div>${state.settingsScope === 'vm' ? `<section class="settings-group"><h2>Live access</h2>${setting('Microphone passthrough', 'Capture this PC only while Claude records on agent-vm.', 'LIVE / PER VM', '<input class="switch" type="checkbox" checked data-setting="mic">')}${setting('T3 Code web GUI', 'Serve the paired workspace over HTTPS.', 'LIVE / RESTARTS T3', '<input class="switch" type="checkbox" checked data-setting="t3">')}${setting('T3 channel', 'Stable releases or nightly CI builds.', 'REINSTALLS T3', '<select data-setting="channel"><option>Stable</option><option>Nightly</option></select>')}${setting('Trusted T3 proxy', 'Optional origin for pairing links and phones.', 'WRITE NOW', '<input type="text" value="https://t3.example.net" data-setting="proxy">')}</section><section class="settings-group"><h2>Resources and recovery</h2>${setting('vCPUs and fixed RAM', 'Current: 4 vCPUs and 8 GB.', 'RESTART REQUIRED', '<button class="secondary-action" data-vm-settings="agent-vm">EDIT</button>')}${setting('Idle policy', 'Save after verified guest and forward inactivity.', 'LIVE / HOST CAP 120M', '<input type="number" value="45" data-setting="idle">')}${setting('Chat history retention', 'Older transcripts are omitted from backups only.', 'NEXT BACKUP', '<input type="number" value="30" data-setting="retention">')}${setting('Automatic checkpoints', 'Manual checkpoints are never deleted automatically.', 'APPLY NOW OR NEXT REBUILD', '<input class="switch" type="checkbox" data-setting="checkpoints">')}${setting('OpenCode background watcher', 'Detached jobs can wake their OpenCode session.', 'NEXT REPROVISION', '<input class="switch" type="checkbox" checked data-setting="watcher">')}</section><section class="settings-group"><h2>Deliberate lifecycle</h2>${setting('Reprovision agent-vm', 'Apply installed Construct and selected profiles without deleting data.', 'REVIEW FIRST', '<button class="secondary-action" data-action="run-reprovision">REVIEW</button>')}${setting('Custom reinstall', 'Choose save-and-restore, existing backup, or clean wipe.', 'DELETES AND RECREATES VM', '<button class="secondary-action" data-action="custom-reinstall">CHOOSE</button>')}${setting('Remove instance', 'Clean client state without touching shared profiles.', 'FINAL CONFIRMATION', '<button class="danger-action" data-action="remove-machine">REMOVE</button>')}</section>` : `<section class="settings-group"><h2>Companion on this PC</h2>${setting('Start with Windows', 'Approvals and tunnels remain available without VS Code.', 'NEXT SIGN IN', '<input class="switch" type="checkbox" checked data-setting="autostart">')}${setting('Desktop notifications', 'Use the Windows notification center.', 'THIS PC / ALL VMS', '<input class="switch" type="checkbox" checked data-setting="notifications">')}${setting('Capture device', 'Microphone used by passthrough and host bridge.', 'THIS PC', '<select data-setting="capture"><option>Default microphone</option><option>USB headset</option></select>')}${setting('Forward links', 'Loopback only or advertised on this PC’s LAN address.', 'RESTARTS LIVE TUNNELS', '<select data-setting="forwards"><option>Loopback only</option><option>LAN host label</option><option>Disabled</option></select>')}${setting('Patch repair delay', 'Recheck enabled reversible patches after startup.', '0 DISABLES', '<input type="number" value="20" data-setting="repatch">')}${setting('Debug logging', 'Keep host consoles open. Credentials stay redacted.', 'THIS PC', '<input class="switch" type="checkbox" data-setting="debug">')}</section>`}<div id="staged">${state.dirty ? stagedBar() : ''}</div></div></div>`;
}

function stagedBar() {
  return `<div class="staged-bar"><div><strong>STAGED CHANGES</strong><small>Review exact effects before applying.</small></div><button class="secondary-action" data-action="discard-settings">DISCARD</button><button class="primary-action" data-action="apply-settings">REVIEW & APPLY</button></div>`;
}

function render() {
  const pages = { timeline: timelinePage, machines: machinesPage, workspaces: workspacesPage, vault: vaultPage, host: hostPage, settings: settingsPage };
  page.innerHTML = pages[state.page]();
  document.querySelectorAll('.pulse-tabs [data-page]').forEach((button) => button.classList.toggle('active', button.dataset.page === state.page));
  document.querySelector('.pulse-global-actions [data-page="settings"]').classList.toggle('active', state.page === 'settings');
  const badge = document.getElementById('timeline-badge');
  badge.textContent = state.requestsPending;
  badge.style.display = state.requestsPending ? 'inline' : 'none';
  hydrateIcons(page);
  page.focus({ preventScroll: true });
}

function goTo(nextPage) {
  state.page = nextPage;
  state.dirty = false;
  render();
}

function simpleDialog(title, eyebrow, body, confirm = 'APPLY') {
  dialog({ title, eyebrow, body: `${body}<p class="dialog-note" style="margin-top:16px">Prototype only. Sample values are not real credentials or live machine state.</p>`, actions: [{ label: 'CANCEL', className: 'secondary-action' }, { label: confirm, primary: true, onClick: () => toast(`${title} completed`) }] });
}

function handleAction(action) {
  const messages = {
    'copy-link': 'localhost:5173 copied', 'open-link': 'Web preview opened', 'close-forward': 'Forward closed. The VM request was acknowledged.',
    'open-config': 'Config repository opened in the merge editor', 'run-sync': 'Sync complete. No changes found.', 'export-usage': 'Usage JSON exported',
    'release-notes': 'Release notes opened', 'update-t3': 'Quick update started for T3 Code server and Desktop',
    'machine-console': 'Fresh console link created', 'machine-open': 'Opening the selected VM workspace',
    'machine-power': 'Power action reviewed for the selected VM', 'open-host-job': 'Job and audit records opened',
    'retry-shutdown': 'Graceful shutdown retried. No force-off fallback will run.', 'compare-release': 'Release comparison opened',
    'edit-workspace': 'Profile editor opened with schema validation', 'open-workspace': `Opening ${state.workspace} on agent-vm`,
    'share-profile': 'Sharing choices opened: one-liner or zip bundle', 'push-profile': 'Tracked profile changes are ready for review',
    'refresh-vault': 'Vault state refreshed', 'sync-hosts': 'Both hosts synced', 'copy-secret': 'Secret copied natively. Clipboard clears in 30 seconds.',
    'toggle-auto': 'Auto-approve change requires explicit confirmation', 'delete-secret': 'Deletion confirmation opened',
    'revoke-lease': 'Lease revoked. Cleanup started.', 'host-mode': 'Mode review opened with host trust consequences',
    'pair-phone': 'Native QR pairing dialog opened with separate origins', 'show-key': 'Vault key requested in a native dialog',
    'import-key': 'Native vault-key import opened', 'issue-token': 'One-time token issued in a native dialog',
    'edit-user': 'Allowance editor opened with inherited and effective values', 'save-config': 'JSON section validated and saved',
    'stage-update': 'Update staging started. Existing VMs keep their state.', 'refresh-host': 'Host state refreshed',
  };
  if (messages[action]) return toast(messages[action]);
  if (action === 'adjust-request') return simpleDialog('Limit this approval', 'ONE VM / LISTED SECRETS', '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>Uses</label><input type="number" value="1"></div><div class="dialog-field"><label>Time</label><select><option>10 minutes</option><option>1 hour</option><option>2 hours</option></select></div></div>', 'USE LIMITS');
  if (action === 'run-reprovision') return dialog({ title: 'Reprovision agent-vm?', eyebrow: 'PRESERVE DATA / APPLY UPDATE', body: '<div class="code-block">TARGET       agent-vm\nINSTALLED    main@b72c91\nVM CURRENT   main@a81d2f\nPROFILES     4 selected\nDATA         Preserved\nSERVICES     Restart where required</div><p class="dialog-note">T3 Code resumes busy threads after its restart. Remote-SSH reconnects when the VM is ready.</p>', actions: [{ label: 'CANCEL', className: 'secondary-action' }, { label: 'START REPROVISION', primary: true, onClick: () => toast('Reprovision job added to the timeline') }] });
  if (action === 'custom-reinstall') return simpleDialog('Custom reinstall', 'DELETION GATE REMAINS IN ELEVATED CONSOLE', '<div class="dialog-field"><label>Configuration policy</label><select><option>Save and restore current config</option><option>Restore an existing backup</option><option>Clean wipe</option></select></div><p class="dialog-note" style="margin-top:16px">The VM and disk are recreated. The elevated console asks for typed confirmation before deletion.</p>', 'CONTINUE');
  if (action === 'remove-machine') return simpleDialog('Remove this instance?', 'CLIENT STATE CLEANUP', '<p>Remove the SSH alias and key, editor platform entry, OpenCode server, T3 certificate trust, and per-instance state.</p><p class="dialog-note">Remote VMs get a separate keep-or-delete choice. Shared profiles stay intact.</p>', 'REVIEW CLEANUP');
  if (action === 'add-workspace') return simpleDialog('Add project', 'CLONE + CREATE PROFILE', '<div class="dialog-field"><label>Git repository URL</label><input placeholder="https://git.example.com/team/project.git"></div><div class="dialog-field"><label>Profile name</label><input placeholder="project-name"></div>', 'CLONE & ADD');
  if (action === 'add-secret') return simpleDialog('Add secret', 'NATIVE VALUE ENTRY', '<div class="dialog-field"><label>Name</label><input placeholder="service-token"></div><div class="dialog-field"><label>Purpose shown to agents</label><input></div><div class="dialog-field"><label>Value</label><textarea rows="4"></textarea></div><div class="dialog-field"><label>Optional username</label><input></div>', 'SAVE SECRET');
  if (action === 'edit-secret') return simpleDialog(`Edit ${state.vaultItem}`, 'METADATA / VALUE / POLICY', '<div class="dialog-field"><label>Description shown to agents</label><input value="Credential for automated tasks"></div><div class="dialog-field"><label>Username</label><input value="service-account"></div><p class="dialog-note" style="margin-top:16px">A new value ends every lease on the old value. A metadata-only edit preserves access.</p>', 'SAVE');
  if (action === 'add-instance') return simpleDialog('Add instance', 'LOCAL OR REMOTE', '<div class="dialog-field"><label>Flow</label><select><option>Enroll a remote host</option><option>Create VM on enrolled host</option><option>Register this Remote-SSH VM</option></select></div><div class="dialog-field"><label>Host service URL</label><input placeholder="https://host.example:7462"></div>', 'CONTINUE');
  if (action === 'apply-settings') return dialog({ title: 'Apply staged settings?', eyebrow: 'GROUPED BY EFFECT', body: '<div class="code-block">NOW             Live service and VM config changes\nRESTART         CPU and fixed RAM, separately confirmed\nNEXT PROVISION  Plugins and provisioning preferences\nTHIS PC         Companion-only preferences</div>', actions: [{ label: 'KEEP EDITING', className: 'secondary-action' }, { label: 'APPLY SAFE CHANGES', primary: true, onClick: () => { state.dirty = false; render(); toast('Safe changes applied. Restart-only work remains pending.'); } }] });
  if (action === 'discard-settings') { state.dirty = false; render(); return toast('Staged edits discarded'); }
}

function decideRequest(decision) {
  const approved = decision === 'approve';
  const finish = () => {
    const resolvedId = state.page === 'vault' ? state.vaultItem : 'request-release';
    state.resolvedRequests.add(resolvedId);
    state.requestsPending = Math.max(0, state.requestsPending - 1);
    if (state.page === 'timeline') {
      state.selectedEvent = 'child-overdue';
    } else if (state.vaultTab === 'requests') {
      const next = vaultItems.requests.find(([id]) => !state.resolvedRequests.has(id));
      state.vaultItem = next?.[0] || resolvedId;
    }
    render();
    toast(approved ? 'Access approved with the shown limits' : 'Request denied. No value released.');
  };
  if (approved) return dialog({ title: 'Approve secret access?', eyebrow: 'FIRST ANSWER WINS', body: '<p>Only the named VM receives the listed secrets, with the displayed time and use limits.</p><p class="dialog-note">Cleanup begins when the lease ends. Other paired approval surfaces close automatically.</p>', actions: [{ label: 'BACK', className: 'secondary-action' }, { label: 'APPROVE', primary: true, onClick: finish }] });
  finish();
}

function vmSettings(name) {
  simpleDialog(`${name} settings`, 'ONLY EDITED FIELDS ARE SENT', '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>CPU count</label><input type="number" value="4"></div><div class="dialog-field"><label>Fixed RAM GB</label><input type="number" value="8"></div><div class="dialog-field"><label>Idle timeout</label><input type="number" value="45"></div><div class="dialog-field"><label>Idle action</label><select><option>Save</option><option>Shut down</option><option>Off</option></select></div></div><p class="dialog-note" style="margin-top:16px">Idle applies now. Hardware changes on a running VM wait for a confirmed full stop and cold start.</p>', 'APPLY CHANGES');
}

function openCommand() {
  const element = document.createElement('dialog');
  element.className = 'command-palette';
  element.innerHTML = `<div class="palette-input">${icon('command')}<input autofocus placeholder="Type a destination or action"><kbd>ESC</kbd></div><div class="palette-results"><p>Navigate</p><button data-command-page="timeline">${icon('activity')}Timeline<small>ALL INSTANCES</small></button><button data-command-page="machines">${icon('server')}Machines<small>4 REGISTERED</small></button><button data-command-page="vault">${icon('shield')}Vault requests<small>${state.requestsPending} WAITING</small></button><button data-command-page="settings">${icon('sliders')}Settings<small>SEARCHABLE</small></button><p>Run</p><button data-command-action="run-sync">${icon('git')}Sync configuration<small>agent-vm</small></button><button data-command-action="run-reprovision">${icon('refresh')}Reprovision selected VM<small>REVIEW FIRST</small></button><button data-command-action="add-workspace">${icon('plus')}Add project<small>CLONE + PROFILE</small></button></div>`;
  document.body.append(element);
  element.showModal();
  element.querySelector('input').focus();
  element.addEventListener('close', () => element.remove());
  element.querySelectorAll('[data-command-page]').forEach((button) => button.addEventListener('click', () => { goTo(button.dataset.commandPage); element.close(); }));
  element.querySelectorAll('[data-command-action]').forEach((button) => button.addEventListener('click', () => { element.close(); handleAction(button.dataset.commandAction); }));
}

function handleClick(event) {
  const nav = event.target.closest('[data-page]');
  if (nav) return goTo(nav.dataset.page);
  const filter = event.target.closest('[data-filter]');
  if (filter) { state.filter = filter.dataset.filter; const visible = events.find((item) => state.filter === 'all' || item.kind === state.filter); state.selectedEvent = visible?.id || null; return render(); }
  const eventButton = event.target.closest('[data-event]');
  if (eventButton) { state.selectedEvent = eventButton.dataset.event; return render(); }
  const machine = event.target.closest('[data-machine]');
  if (machine) { state.machine = machine.dataset.machine; return render(); }
  const workspace = event.target.closest('[data-workspace]');
  if (workspace) { state.workspace = workspace.dataset.workspace; return render(); }
  const vaultTab = event.target.closest('[data-vault-tab]');
  if (vaultTab) { state.vaultTab = vaultTab.dataset.vaultTab; state.vaultItem = vaultItems[state.vaultTab][0][0]; return render(); }
  const vaultItem = event.target.closest('[data-vault-item]');
  if (vaultItem) { state.vaultItem = vaultItem.dataset.vaultItem; return render(); }
  const hostTab = event.target.closest('[data-host-tab]');
  if (hostTab) { state.hostTab = hostTab.dataset.hostTab; state.hostItem = hostLists[state.hostTab][0][0]; return render(); }
  const hostItem = event.target.closest('[data-host-item]');
  if (hostItem) { state.hostItem = hostItem.dataset.hostItem; return render(); }
  const settingsScope = event.target.closest('[data-settings-scope]');
  if (settingsScope) { state.settingsScope = settingsScope.dataset.settingsScope; state.dirty = false; return render(); }
  const decision = event.target.closest('[data-request-decision]');
  if (decision) return decideRequest(decision.dataset.requestDecision);
  const vm = event.target.closest('[data-vm-settings]');
  if (vm) return vmSettings(vm.dataset.vmSettings);
  const action = event.target.closest('[data-action]');
  if (action) return handleAction(action.dataset.action);
}

document.addEventListener('click', handleClick);
document.addEventListener('change', (event) => {
  if (event.target.matches('[data-setting]')) {
    state.dirty = true;
    const staged = document.getElementById('staged');
    if (staged) { staged.innerHTML = stagedBar(); hydrateIcons(staged); }
  }
});
document.addEventListener('input', (event) => {
  if (event.target.id === 'pulse-settings-search') {
    const query = event.target.value.toLowerCase();
    document.querySelectorAll('[data-search]').forEach((row) => { row.hidden = !row.dataset.search.includes(query); });
    document.querySelectorAll('.settings-group').forEach((group) => { group.hidden = !group.querySelector('[data-search]:not([hidden])'); });
  }
});
document.querySelectorAll('[data-scope]').forEach((button) => button.addEventListener('click', () => {
  document.querySelectorAll('[data-scope]').forEach((item) => item.classList.toggle('active', item === button));
  document.getElementById('pulse-instance').textContent = button.dataset.scope;
  document.getElementById('pulse-location').textContent = button.dataset.scope === 'build-vm' ? 'HOST.EXAMPLE / REMOTE' : 'LOCAL / HYPER-V';
  toast(`Scope switched to ${button.dataset.scope}. Live caches retargeted.`);
}));
document.getElementById('mic-toggle').addEventListener('click', (event) => {
  const button = event.currentTarget;
  button.classList.toggle('active');
  button.querySelector('span:last-child').textContent = button.classList.contains('active') ? 'MIC ARMED' : 'MIC OFF';
  toast(button.classList.contains('active') ? 'Microphone passthrough armed for the selected VM' : 'Microphone tunnel closed and recorder released');
});
document.getElementById('command-open').addEventListener('click', openCommand);
document.addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); openCommand(); }
});

function updateClock() {
  document.getElementById('clock').textContent = new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date());
}
updateClock();
setInterval(updateClock, 1000);
render();
