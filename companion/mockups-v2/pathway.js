const { icon, hydrateIcons, toast, dialog } = window.mockup;

const state = {
  page: 'home',
  libraryTab: 'projects',
  selectedRequest: 'release',
  requests: [
    { id: 'release', vm: 'build-vm', type: 'Access request', title: 'Release deployment needs 2 secrets', time: '9 min left', names: [['github-release', 'Release repository token'], ['staging-db', 'Staging database administrator']], access: '10 uses · 2 hours', source: 'T3 Code · Release thread', reason: 'Deploy the release candidate and run the database migration.', description: 'The agent can read both values from build-vm until the use count or time limit ends.' },
    { id: 'publish', vm: 'agent-vm', type: 'One-time read', title: 'Package publishing needs npm-publish', time: '6 min left', names: [['npm-publish', 'Test registry publishing']], access: '1 use · 10 minutes', source: 'Terminal · npm publish', reason: 'Publish the patched package to the test registry.', description: 'The agent can read this value once. Cleanup starts one minute after the last use.' },
  ],
  dirty: false,
};

const page = document.getElementById('page');

function title(kicker, heading, copy, actions = '') {
  return `<div class="page-title"><div><span class="title-kicker">${kicker}</span><h1>${heading}</h1><p>${copy}</p></div><div class="title-actions">${actions}</div></div>`;
}

function homePage() {
  return `<section class="welcome"><p class="welcome-kicker">Everything is connected</p><h1>What would you like to do?</h1><p>Start with an outcome. Pathway keeps the machine details close without making you navigate by implementation.</p></section>
    <section class="task-grid" aria-label="Common tasks">
      <button class="task-card" data-task="continue"><span class="task-icon">${icon('folder')}</span><strong>Continue work</strong><p>Open a recent project or connect to a running VM.</p>${icon('arrowUpRight')}</button>
      <button class="task-card" data-page="approvals"><span class="task-badge" id="home-request-count">${state.requests.length}</span><span class="task-icon">${icon('shield')}</span><strong>Respond to requests</strong><p>Review secret access and files waiting for a cleanup decision.</p>${icon('arrowUpRight')}</button>
      <button class="task-card" data-page="maintain"><span class="task-icon">${icon('server')}</span><strong>Maintain a VM</strong><p>Update agents, adjust resources, or repair configuration.</p>${icon('arrowUpRight')}</button>
      <button class="task-card" data-page="infrastructure"><span class="task-icon">${icon('network')}</span><strong>Set up infrastructure</strong><p>Add a remote host, create a VM, or manage capacity.</p>${icon('arrowUpRight')}</button>
    </section>
    <div class="home-layout">
      <section><div class="section-heading"><h2>Pick up where you left off</h2><button class="text-button" data-page="library" data-library-target="projects">All projects ${icon('arrowRight')}</button></div><div class="continue-card">
        <button class="continue-row" data-open-project="Construct"><span class="project-glyph">CO</span><span class="continue-copy"><strong>Construct</strong><small>agent-vm · /root/repos/construct</small></span><span class="continue-meta"><strong>Running</strong>2 active threads</span>${icon('arrowUpRight')}</button>
        <button class="continue-row" data-open-project="Shopfront"><span class="project-glyph">SH</span><span class="continue-copy"><strong>Shopfront</strong><small>agent-vm · Web preview on port 5173</small></span><span class="continue-meta"><strong>Ready</strong>Edited 28 min ago</span>${icon('arrowUpRight')}</button>
        <button class="continue-row" data-open-project="Mobile notes"><span class="project-glyph">MN</span><span class="continue-copy"><strong>Mobile notes</strong><small>build-vm · Android build profile</small></span><span class="continue-meta"><strong>Remote</strong>Opened yesterday</span>${icon('arrowUpRight')}</button>
      </div></section>
      <aside class="health-card"><div class="section-heading"><h2>Your Construct</h2><span class="access-badge auto">Live</span></div><div class="health-score"><div class="health-ring"><span>3</span></div><p><strong>Instances online</strong>All essential background services are available.</p></div><div class="health-item">${icon('alert')}<div><strong>One update is worth scheduling</strong><p>agent-vm is one Construct release behind this PC.</p><button class="text-button" data-guided="reprovision">Review the 2-minute update ${icon('arrowRight')}</button></div></div><div class="health-item">${icon('key')}<div><strong>Two agents are waiting</strong><p>Secret requests expire if nobody answers them.</p><button class="text-button" data-page="approvals">Review requests ${icon('arrowRight')}</button></div></div><p class="health-footer">Last full check just now</p></aside>
    </div>`;
}

function requestList() {
  return state.requests.map((request) => `<button class="request-list-item ${state.selectedRequest === request.id ? 'active' : ''}" data-request="${request.id}"><span class="request-list-top">${icon('key')}<strong>${request.vm}</strong><time>${request.time}</time></span><p>${request.title}</p><small>${request.access}</small></button>`).join('');
}

function requestDetail(request) {
  return `<div class="request-detail-head"><span class="request-type">${icon('shield')}${request.type}</span><h2>${request.title}</h2><p>${request.description}</p></div><div class="request-detail-body"><span class="detail-label">Requested secrets</span>${request.names.map(([name, description]) => `<div class="request-secret">${icon('key')}<div><strong>${name}</strong><small>${description}</small></div><span class="state">Value hidden</span></div>`).join('')}<div class="request-info-grid"><div class="request-info"><small>Requested access</small><strong>${request.access}</strong></div><div class="request-info"><small>Requesting VM</small><strong>${request.vm}</strong></div></div><div class="reason-box"><strong>Agent's reason</strong>“${request.reason}”</div><p class="safety-check">${icon('info')}Only approve the access this task needs. After the lease ends, Construct searches the VM for leftover copies.</p></div><div class="request-detail-actions"><button class="text-button" data-adjust="${request.id}">Change time or uses</button><button class="secondary-action" data-decision="deny" data-request-id="${request.id}">Deny</button><button class="primary-action" data-decision="approve" data-request-id="${request.id}">${icon('check')}Approve access</button></div>`;
}

function approvalsPage() {
  if (!state.requests.length) return `${title('Key vault', 'You’re all caught up', 'No access requests or file decisions are waiting. New work appears here and beside the Windows tray icon.')}<section class="empty-approvals"><span>${icon('check')}</span><h2>No decisions waiting</h2><p>Your active leases keep running. You can review or revoke them from Library → Vault access.</p><button class="secondary-action" style="margin-top:22px" data-page="library" data-library-target="access">Review active access</button></section>`;
  const selected = state.requests.find((request) => request.id === state.selectedRequest) || state.requests[0];
  state.selectedRequest = selected.id;
  return `${title('Key vault', 'Approval inbox', 'One queue for access, replacement, deletion, auto-approve, and cleanup decisions. The first answer from any paired device wins.', `<button class="secondary-action" data-action="vault-help">${icon('help')}How approvals work</button>`)}<div class="approval-layout"><aside class="inbox-panel"><div class="inbox-head"><strong>Waiting for you</strong><small>Oldest first</small></div><div id="request-list">${requestList()}</div></aside><section class="request-detail" id="request-detail">${requestDetail(selected)}</section></div>`;
}

function projectLibrary() {
  const profiles = [
    ['CO', 'Construct', '2 repos', 'Core Companion, service, and agent runtime development.', ['PowerShell', '.NET 10', 'Node'], true],
    ['SH', 'Shopfront', '1 repo', 'Storefront web app with persistent preview forwards.', ['Node 22', 'pnpm', 'Docker'], true],
    ['MN', 'Mobile notes', '2 repos', 'Flutter app and its local API development profile.', ['Flutter', 'Android', 'Python'], true],
    ['DO', 'Documentation', '1 repo', 'Static documentation with link and build checks.', ['Node 22', 'Markdown'], true],
    ['DL', 'Data lab', '1 repo', 'Python notebooks and reproducible data tooling.', ['Python', 'uv'], false],
    ['LT', 'Legacy tools', '1 repo', 'Older maintenance utilities kept out of routine provisions.', ['.NET 8', 'PowerShell'], false],
  ];
  return `<div class="sync-card">${icon('git')}<div><strong>Profiles are synced</strong><small>This PC and agent-vm match. VM edits return here automatically.</small></div><button class="secondary-action" data-action="sync">Sync now</button></div><div class="profile-grid">${profiles.map(([glyph, name, repos, description, stack, selected]) => `<article class="profile-card"><div class="profile-top"><span class="project-glyph">${glyph}</span><div><h3>${name}</h3><small>${repos}</small></div>${selected ? `<span class="selected-check" title="Selected for next provision">${icon('check')}</span>` : ''}</div><p class="profile-description">${description}</p><div class="stack-pills">${stack.map((item) => `<span>${item}</span>`).join('')}</div><div class="profile-footer"><small>${selected ? 'Next provision' : 'Not selected'}</small><div><button class="text-button" data-open-project="${name}">Open</button><button class="text-button" data-edit-profile="${name}">Edit</button></div></div></article>`).join('')}</div>`;
}

function secretsLibrary() {
  const secrets = [
    ['github-release', 'Release repository token', 'release-bot', '1 VM holds access', false],
    ['staging-db', 'Staging database administrator', 'deploy', '1 VM holds access', false],
    ['npm-read', 'Read-only package mirror', 'ci', 'Available to all VMs', true],
    ['npm-publish', 'Test registry publishing', 'builder', 'No active access', false],
    ['ssh-deploy', 'Deployment server private key', 'deploy', 'No active access', false],
  ];
  return `<div class="library-toolbar"><label class="search-field">${icon('search')}<input placeholder="Find by name, purpose, or username" aria-label="Find secrets"></label><button class="secondary-action" data-action="vault-hosts">${icon('network')}Hosts & devices</button></div><section class="secrets-list">${secrets.map(([name, description, username, access, auto]) => `<div class="secret-item"><div class="secret-item-name">${icon('key')}<div><strong>${name}</strong><small>${description}</small></div></div><div class="secret-access"><strong>${username}</strong><small>${access}</small></div><span class="access-badge ${auto ? 'auto' : 'ask'}">${auto ? 'Auto-approved' : 'Ask every time'}</span><div class="secret-actions"><button class="round-action" title="Copy secret" data-copy="${name}">${icon('copy')}</button><button class="round-action" title="Edit secret" data-edit-secret="${name}">${icon('edit')}</button></div></div>`).join('')}</section>`;
}

function accessLibrary() {
  return `<div class="notice-card">${icon('info')}<div><strong>Access and cleanup stay together</strong><p>Revoke a lease to start a scrub. Files outside agent logs always wait for your decision.</p></div></div><section class="panel"><div class="panel-head"><h2>Active leases</h2><small>2 leases on build-vm</small></div><div class="maintenance-list"><li>${icon('key')}<div><strong>github-release</strong><small>8 uses left · Ends today at 16:30 · Release deployment</small></div><button class="text-button" data-revoke="github-release">Revoke</button></li><li>${icon('key')}<div><strong>staging-db</strong><small>8 uses left · Ends today at 16:30 · Database migration</small></div><button class="text-button" data-revoke="staging-db">Revoke</button></li></div></section><section class="panel" style="margin-top:18px"><div class="panel-head"><h2>Pending cleanup</h2><small>Waits for offline VMs</small></div><div class="maintenance-list"><li>${icon('clock')}<div><strong>old-api-token on test-vm</strong><small>Scrub starts when this VM comes online. No action needed now.</small></div><button class="text-button" data-action="cleanup-details">Details</button></li></div></section><section class="panel" style="margin-top:18px"><div class="panel-head"><h2>Recent vault activity</h2><button class="text-button" data-action="export-activity">Export safe log</button></div><div class="maintenance-list"><li>${icon('check')}<div><strong>Agent logs redacted on agent-vm</strong><small>No other files contained the released value · 14:12</small></div></li><li>${icon('phone')}<div><strong>npm-read approved from a paired phone</strong><small>build-vm · 13:58</small></div></li><li>${icon('refresh')}<div><strong>24 secrets synced with host.example</strong><small>No conflicts · 13:30</small></div></li></div></section>`;
}

function remotesLibrary() {
  return `<div class="remote-grid"><article class="remote-card">${icon('git')}<h3>git.example.com/team/agent-configs</h3><p>Three tracked profiles. Import new baselines, push local edits for review, or publish a profile created here.</p><div class="profile-footer"><small>Fetched 4 minutes ago</small><div><button class="text-button" data-action="import">Import</button><button class="text-button" data-action="push">Push</button></div></div></article><article class="remote-card">${icon('git')}<h3>git.example.com/lab/base-profiles</h3><p>Eight read-only profiles. Imported files merge with their tracked base and run through the same validator as local edits.</p><div class="profile-footer"><small>Fetched 12 minutes ago</small><div><button class="text-button" data-action="import">Import</button><button class="text-button" data-action="share">Share</button></div></div></article></div><section class="panel" style="margin-top:22px"><div class="panel-head"><h2>Sharing without leaking credentials</h2></div><div style="padding:0 20px 20px;color:var(--muted);font-size:11px;line-height:1.7">Linked profiles can become an install one-liner. Local-only profiles become a zip bundle with a deployment script. Plain repository URLs are required so Git Credential Manager handles tokens.</div></section>`;
}

function libraryPage() {
  const actions = state.libraryTab === 'projects' ? `<button class="secondary-action" data-action="export-config">${icon('download')}Export config</button><button class="primary-action" data-action="add-project">${icon('plus')}Add project</button>` : state.libraryTab === 'secrets' ? `<button class="primary-action" data-action="add-secret">${icon('plus')}Add secret</button>` : state.libraryTab === 'remotes' ? `<button class="primary-action" data-action="add-remote">${icon('plus')}Link repository</button>` : '';
  return `${title('Reusable setup', 'Library', 'Project profiles, secret metadata, active access, and shared configuration live here.', actions)}<nav class="library-tabs"><button class="${state.libraryTab === 'projects' ? 'active' : ''}" data-library-tab="projects">Projects</button><button class="${state.libraryTab === 'secrets' ? 'active' : ''}" data-library-tab="secrets">Secrets</button><button class="${state.libraryTab === 'access' ? 'active' : ''}" data-library-tab="access">Vault access</button><button class="${state.libraryTab === 'remotes' ? 'active' : ''}" data-library-tab="remotes">Config remotes</button></nav>${state.libraryTab === 'projects' ? projectLibrary() : state.libraryTab === 'secrets' ? secretsLibrary() : state.libraryTab === 'access' ? accessLibrary() : remotesLibrary()}`;
}

function maintainPage() {
  return `${title('Selected VM', 'Maintain agent-vm', 'Start with the recommended action. Every disruptive change gets a plan and a review before it runs.', `<button class="secondary-action" data-action="switch-vm">${icon('server')}Change VM</button>`)}<div class="notice-card">${icon('alert')}<div><strong>A reprovision is ready</strong><p>Apply the Construct installed on this PC. Your projects, repositories, and agent auth stay on the VM.</p></div><button class="secondary-action" data-guided="reprovision">Review update</button></div><section class="maintain-hero"><div class="machine-main"><span class="machine-status"><i></i>Online and ready</span><h2>agent-vm</h2><p>Ubuntu 24.04 LTS · Local Hyper-V · Last provisioned 18 days ago</p><div class="machine-actions"><button class="primary-action" data-open-project="agent-vm">${icon('arrowUpRight')}Open on VM</button><button class="secondary-action" data-action="console">${icon('monitor')}Console</button><button class="secondary-action" data-action="shutdown">${icon('power')}Shut down</button></div></div><div class="machine-resources"><div><strong>4</strong><small>vCPUs</small></div><div><strong>8 GB</strong><small>Memory</small></div><div><strong>53%</strong><small>Disk used</small></div></div></section><div class="maintain-grid"><section class="panel"><div class="panel-head"><h2>Maintenance</h2><small>Clear next steps</small></div><ul class="maintenance-list"><li>${icon('cpu')}<div><strong>Resources</strong><small>4 vCPUs · 8 GB fixed RAM · 128 GB disk</small></div><button class="text-button" data-guided="resources">Change</button></li><li>${icon('refresh')}<div><strong>Coding agents</strong><small>T3 Code has an update. Other agents are current.</small></div><button class="text-button" data-guided="agents">Update</button></li><li>${icon('clock')}<div><strong>Idle policy</strong><small>Save after 45 minutes of inactivity</small></div><button class="text-button" data-guided="idle">Edit</button></li><li>${icon('drive')}<div><strong>Reinstall or restore</strong><small>Choose what happens to config before recreating the VM</small></div><button class="text-button" data-guided="reinstall">Choose</button></li></ul></section><section class="panel"><div class="panel-head"><h2>Usage today</h2><button class="text-button" data-action="usage-details">Daily · Monthly · All time</button></div><div class="usage-mini"><div class="usage-total"><strong>18.4M</strong><span>$42.18 estimated</span></div><div class="usage-mini-bars"><i style="height:32%"></i><i style="height:51%"></i><i style="height:43%"></i><i style="height:74%"></i><i style="height:60%"></i><i style="height:88%"></i><i style="height:69%"></i><i style="height:48%"></i><i style="height:82%"></i><i style="height:57%"></i><i style="height:93%"></i><i style="height:76%"></i></div><div class="usage-caption"><span>Claude Code 68%</span><span>Codex 22%</span><span>OpenCode 10%</span></div></div></section><section class="panel"><div class="panel-head"><h2>Open forwards</h2><span class="access-badge auto">2 open</span></div><div class="forward-cards"><div class="forward-card">${icon('globe')}<div><strong>Web preview</strong><small>vm:5173 → localhost:5173</small></div><button class="round-action" data-action="open-forward">${icon('arrowUpRight')}</button></div><div class="forward-card">${icon('database')}<div><strong>API server</strong><small>vm:3000 → localhost:3000</small></div><button class="round-action" data-action="open-forward">${icon('arrowUpRight')}</button></div></div></section><section class="panel"><div class="panel-head"><h2>Connection health</h2><span class="access-badge auto">All ready</span></div><ul class="maintenance-list"><li>${icon('mic')}<div><strong>Microphone passthrough</strong><small>Armed · Captures only while recording</small></div></li><li>${icon('git')}<div><strong>Config sync</strong><small>Synced 4 minutes ago · No conflicts</small></div><button class="text-button" data-action="sync">Sync</button></li><li>${icon('bell')}<div><strong>Desktop notifications</strong><small>Windows notification center connected</small></div></li></ul></section></div>`;
}

function infrastructurePage() {
  return `${title('Remote capacity', 'Infrastructure', 'Enroll hosts, create VMs, and administer shared capacity. Host tools appear only for administrators.', `<button class="secondary-action" data-action="add-host">${icon('plus')}Add remote host</button><button class="primary-action" data-action="new-vm">${icon('plus')}Create VM</button>`)}<div class="infra-summary"><section class="infra-stat">${icon('network')}<strong>2</strong><small>Enrolled hosts · Both reachable</small></section><section class="infra-stat">${icon('server')}<strong>8</strong><small>5 primary VMs · 3 child VMs</small></section><section class="infra-stat">${icon('cpu')}<strong>16 / 32</strong><small>vCPUs in use on host.example</small></section><section class="infra-stat">${icon('database')}<strong>24 / 64</strong><small>GB runtime memory allocated</small></section></div><div class="host-workspace"><section class="panel"><div class="panel-head"><h2>Hosts and machines</h2><small>Current state, not cached guesses</small></div><div class="host-list"><div class="host-row"><span class="project-glyph">HV</span><div><strong>host.example</strong><small>Hyper-V · Healthy · constructd 1.8.0</small></div><button class="text-button" data-host="host.example">Administer ${icon('arrowRight')}</button></div><div class="host-row"><span class="project-glyph">PV</span><div><strong>secondary-host.example</strong><small>Proxmox · Healthy · constructd 1.8.0</small></div><button class="text-button" data-host="secondary-host.example">Administer ${icon('arrowRight')}</button></div><div class="host-row"><span class="project-glyph">VM</span><div><strong>build-vm</strong><small>Primary on host.example · Running · Save after 45 min</small></div><button class="text-button" data-vm-settings="build-vm">Settings</button></div><div class="host-row"><span class="project-glyph">WT</span><div><strong>windows-test</strong><small>Child of build-vm · Shared host-wide · Off</small></div><button class="text-button" data-vm-settings="windows-test">Inspect</button></div><div class="host-row"><span class="project-glyph">LT</span><div><strong>linux-test</strong><small>Child of build-vm · Private · Graceful shutdown overdue</small></div><button class="text-button" data-action="job-details">Inspect job</button></div></div></section><aside class="host-details"><h2>Host operations in one place</h2><p>The admin workspace keeps service work separate from the lifecycle of your own VM.</p><div class="host-facts"><div><small>VMs and users</small><strong>Quotas & allowances</strong></div><div><small>Media</small><strong>Catalog & child ISOs</strong></div><div><small>Operations</small><strong>Jobs & audit</strong></div><div><small>Maintenance</small><strong>Stage & apply</strong></div><div><small>Configuration</small><strong>Validated JSON</strong></div></div><button class="secondary-action" data-host="host.example">Open host workspace ${icon('arrowRight')}</button></aside></div>`;
}

function preference(titleText, description, effect, control) {
  return `<div class="preference" data-searchable="${titleText.toLowerCase()} ${description.toLowerCase()}"><div class="preference-copy"><strong>${titleText}</strong><small>${description}</small>${effect ? `<span class="effect-tag">${effect}</span>` : ''}</div><div class="preference-control">${control}</div></div>`;
}

function settingsPage() {
  return `${title('Simple on the surface', 'Settings', 'Search by outcome. Each option says which VM it changes and whether it applies now, after restart, or during the next provision.')}<label class="search-field settings-search">${icon('search')}<input id="settings-filter" placeholder="Try “microphone”, “restart”, or “backup”" aria-label="Search settings"></label><div class="settings-groups">
    <section class="settings-group"><div class="settings-group-title"><span>${icon('globe')}</span><div><h2>Work from anywhere</h2><p>T3 Code, forwards, and remote access for agent-vm.</p></div></div><div class="panel">${preference('T3 Code web GUI', 'Open the paired workspace in a browser or the matching Desktop app.', 'Applies immediately · Restarts T3', '<input class="switch" type="checkbox" checked data-setting="T3 Code">')}${preference('Release channel', 'Stable follows releases. Nightly follows the latest CI build.', 'Reinstalls T3 Code', '<select data-setting="Channel"><option>Stable</option><option>Nightly</option></select>')}${preference('Trusted proxy address', 'Use your own HTTPS origin for pairing links and phone access.', 'Writes to the VM now', '<input type="text" value="https://t3.example.net" data-setting="Proxy"><button class="secondary-action" data-action="apply-proxy">Apply</button>')}${preference('Port forwards', 'Keep agent-requested web previews available while Companion runs.', 'This PC · All VMs', '<select data-setting="Forwards"><option>Loopback only</option><option>Share on LAN</option><option>Disabled</option></select>')}</div></section>
    <section class="settings-group"><div class="settings-group-title"><span>${icon('mic')}</span><div><h2>Voice and notifications</h2><p>Local devices and Windows integration.</p></div></div><div class="panel">${preference('Microphone passthrough', 'Capture this PC only while Claude Code records on agent-vm.', 'Live · Per VM', '<input class="switch" type="checkbox" checked data-setting="Microphone">')}${preference('Capture device', 'Used by passthrough and the Construct host bridge.', 'This PC', '<select data-setting="Capture"><option>Default microphone</option><option>USB headset</option><option>Studio microphone</option></select>')}${preference('Desktop notifications', 'Deliver agent messages to the Windows notification center.', 'This PC · All VMs', '<input class="switch" type="checkbox" checked data-setting="Notifications">')}</div></section>
    <section class="settings-group"><div class="settings-group-title"><span>${icon('cpu')}</span><div><h2>Resources and idle</h2><p>Hardware changes are reviewed with their restart effect.</p></div></div><div class="panel">${preference('VM resources', 'Currently 4 vCPUs, 8 GB fixed RAM, and a 128 GB disk.', 'Restart or reinstall depending on field', '<button class="secondary-action" data-guided="resources">Review resources</button>')}${preference('Idle timeout', 'Save agent-vm after both guest activity and forwards stay idle.', 'Remote VMs · Applies now', '<input type="number" value="45" data-setting="Idle"><span>minutes</span>')}${preference('Automatic checkpoints', 'Keep Hyper-V automatic checkpoints off for this disposable VM.', 'Apply now or next rebuild', '<input class="switch" type="checkbox" data-setting="Checkpoints">')}</div></section>
    <section class="settings-group"><div class="settings-group-title"><span>${icon('shield')}</span><div><h2>Safety and recovery</h2><p>Backups, cleanup, and deliberate lifecycle actions.</p></div></div><div class="panel">${preference('Chat history in backups', 'Keep 30 days of transcripts. Agent memory is always included. Use 0 for all history.', 'Next backup or export', '<input type="number" value="30" data-setting="Retention"><span>days</span>')}${preference('Reinstall data policy', 'Choose save-and-restore, an existing backup, or a clean wipe.', 'Confirmed before deletion', '<button class="secondary-action" data-guided="reinstall">Choose policy</button>')}${preference('Remove this instance', 'Clean up client state. Shared project profiles stay intact.', 'Remote deletion has a separate choice', '<button class="secondary-action" data-action="remove">Review removal</button>')}</div></section>
    <section class="settings-group"><div class="settings-group-title"><span>${icon('settings')}</span><div><h2>Companion on this PC</h2><p>Application behavior that does not sync to a VM.</p></div></div><div class="panel">${preference('Start with Windows', 'Keep approvals, forwards, and notifications available without VS Code.', 'Next sign-in', '<input class="switch" type="checkbox" checked data-setting="Autostart">')}${preference('Repair agent patches after startup', 'Wait 20 seconds, then reapply only features that are enabled and have reverted.', 'Set 0 to disable', '<input type="number" value="20" data-setting="Repatch"><span>seconds</span>')}${preference('Debug logging', 'Keep host consoles open and write detailed, credential-safe diagnostics.', 'This PC', '<input class="switch" type="checkbox" data-setting="Debug">')}</div></section>
    </div><div id="change-summary">${state.dirty ? changeSummary() : ''}</div>`;
}

function changeSummary() {
  return `<div class="change-summary"><div><strong>Review before anything changes</strong><small>Your edits are staged on this screen.</small></div><button class="secondary-action" data-action="discard">Discard</button><button class="primary-action" data-action="review-settings">Review changes ${icon('arrowRight')}</button></div>`;
}

function render() {
  const pages = { home: homePage, approvals: approvalsPage, library: libraryPage, maintain: maintainPage, infrastructure: infrastructurePage, settings: settingsPage };
  page.innerHTML = pages[state.page]();
  document.querySelectorAll('.path-nav [data-page]').forEach((button) => button.classList.toggle('active', button.dataset.page === state.page || (state.page === 'maintain' && button.dataset.page === 'home')));
  document.querySelector('.settings-button').classList.toggle('active', state.page === 'settings');
  const count = document.getElementById('approval-count');
  count.textContent = state.requests.length;
  count.style.display = state.requests.length ? 'inline-flex' : 'none';
  hydrateIcons(page);
  page.focus({ preventScroll: true });
  window.scrollTo({ top: 0, behavior: 'instant' });
}

function goTo(nextPage, libraryTarget) {
  state.page = nextPage;
  if (libraryTarget) state.libraryTab = libraryTarget;
  state.dirty = false;
  render();
}

function simpleDialog(titleText, eyebrow, body, confirm = 'Save') {
  dialog({ title: titleText, eyebrow, body: `${body}<p class="dialog-note" style="margin-top:16px">Sample data only. This prototype does not change a VM or expose a secret.</p>`, actions: [{ label: 'Cancel', className: 'secondary-action' }, { label: confirm, primary: true, onClick: () => toast(`${titleText} completed`) }] });
}

function guidedChange(kind) {
  const content = {
    resources: { title: 'Change VM resources', first: '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>vCPUs</label><input type="number" value="4"></div><div class="dialog-field"><label>Fixed RAM GB</label><input type="number" value="8"></div></div><div class="dialog-field"><label>Disk GB</label><input type="number" value="128"></div>', review: '<div class="review-list"><div><small>Machine</small><strong>agent-vm</strong></div><div><small>CPU and RAM</small><strong>Graceful stop and cold start</strong></div><div><small>Disk</small><strong>Only changed during reinstall</strong></div><div><small>Data</small><strong>Preserved</strong></div></div><p class="dialog-note">A connected Remote-SSH window disconnects and reconnects when the VM is ready. Nothing is force-powered off.</p>', action: 'Apply and restart', done: 'Resource change started. The VM will reconnect after its cold start.' },
    reprovision: { title: 'Reprovision agent-vm', first: '<p>Apply the Construct installed on this PC with the four selected project profiles.</p><div class="review-list"><div><small>VM data</small><strong>Preserved</strong></div><div><small>Agent auth</small><strong>Preserved</strong></div><div><small>T3 Code</small><strong>Updated and restarted</strong></div></div>', review: '<div class="review-list"><div><small>Installed Construct</small><strong>main@b72c91</strong></div><div><small>VM currently</small><strong>main@a81d2f</strong></div><div><small>Selected profiles</small><strong>Construct, Shopfront, Mobile notes, Docs</strong></div></div><p class="dialog-note">Open work stays on disk. Some agent services restart, and T3 Code resumes busy threads where supported.</p>', action: 'Start reprovision', done: 'Reprovision started. You can follow it from Home.' },
    agents: { title: 'Update coding agents', first: '<p>T3 Code 0.1.1 is available. Claude Code, Codex, and OpenCode are current.</p><div class="dialog-note">The quick update changes T3 Code on this VM and the matching Desktop app on this PC. A full reprovision is not needed.</div>', review: '<div class="review-list"><div><small>T3 Code server</small><strong>0.1.0 → 0.1.1</strong></div><div><small>T3 Code Desktop</small><strong>Install matching build</strong></div><div><small>Other agents</small><strong>No change</strong></div></div>', action: 'Update T3 Code', done: 'T3 Code update started. Busy threads will be resumed after the restart.' },
    idle: { title: 'Change idle policy', first: '<div class="dialog-field"><label>After this much verified inactivity</label><input type="number" value="45"></div><div class="dialog-field"><label>Action</label><select><option>Save and free host RAM</option><option>Shut down</option><option>Never idle out</option></select></div>', review: '<div class="review-list"><div><small>VM</small><strong>agent-vm</strong></div><div><small>Idle detection</small><strong>No agent activity and no active forwards</strong></div><div><small>Effect</small><strong>Immediate, no restart</strong></div></div>', action: 'Apply idle policy', done: 'Idle policy applied without restarting the VM.' },
    reinstall: { title: 'Choose a reinstall policy', first: '<div class="dialog-field"><label>Configuration and chat history</label><select><option>Save current config and restore it</option><option>Restore an existing backup</option><option>Clean wipe, restore nothing</option></select></div><p class="dialog-note">A reinstall recreates the VM and its disk. The elevated console still asks you to type yes before deletion.</p>', review: '<div class="review-list"><div><small>Target</small><strong>agent-vm</strong></div><div><small>VM and disk</small><strong>Deleted and recreated</strong></div><div><small>Shared project profiles</small><strong>Preserved</strong></div></div><p class="dialog-note">The final elevated console is the deletion gate. No action runs from this review alone.</p>', action: 'Continue to elevated review', done: 'Elevated reinstall review opened.' },
  }[kind];
  let step = 1;
  const steps = `<ol class="wizard-steps"><li class="wizard-step active" data-step="1">Choose</li><li class="wizard-step" data-step="2">Review</li><li class="wizard-step" data-step="3">Apply</li></ol>`;
  dialog({ title: content.title, eyebrow: 'Guided change', wide: false, body: `${steps}<div id="wizard-body">${content.first}</div>`, actions: [{ label: 'Cancel', className: 'secondary-action' }, { label: 'Review plan', primary: true, close: false, onClick: (element) => {
    if (step === 1) {
      step = 2;
      element.querySelector('#wizard-body').innerHTML = content.review;
      element.querySelectorAll('.wizard-step').forEach((item, index) => item.classList.toggle('active', index === 1));
      element.querySelectorAll('.wizard-step')[0].classList.add('done');
      element.querySelector('.dialog-actions .primary-action').textContent = content.action;
    } else {
      element.close();
      toast(content.done);
    }
  } }] });
}

function hostWorkspace(host) {
  dialog({ title: host, eyebrow: 'Host administration', wide: true, body: `<div class="review-list"><div><small>Service</small><strong>Healthy · constructd 1.8.0</strong></div><div><small>Capacity</small><strong>16 / 32 vCPUs · 24 / 64 GB RAM</strong></div><div><small>Machines</small><strong>5 primary · 3 children</strong></div><div><small>Attention</small><strong>1 overdue child shutdown</strong></div></div><div class="dialog-field"><label>Workspace section</label><select id="host-section"><option>Overview</option><option>VMs and settings</option><option>Users and allowances</option><option>Media inventory</option><option>Jobs and audit</option><option>Validated JSON configuration</option><option>Service maintenance</option></select></div><p class="dialog-note">In this direction, the selected section replaces the host list with a full workspace. Mutations disable during service maintenance, and unsupported host features explain themselves instead of disappearing silently.</p>`, actions: [{ label: 'Close', className: 'secondary-action' }, { label: 'Open section', primary: true, onClick: () => toast('Host section opened with its own overview and action review') }] });
}

function handleAction(action) {
  const messages = {
    sync: 'Profiles synchronized. No conflicts found.', console: 'Fresh browser console link created', shutdown: 'Graceful shutdown review opened',
    'usage-details': 'Usage can switch between today, this month, and all time', 'open-forward': 'Web preview opened on localhost',
    'job-details': 'Opened the overdue graceful-shutdown job', 'cleanup-details': 'The scrub will run when test-vm comes online',
    'export-activity': 'Credential-safe activity exported', 'export-config': 'Configuration export prepared',
    'apply-proxy': 'Trusted proxy address written to agent-vm', import: 'Found 11 valid profiles. None are selected by default.',
    push: 'Tracked profile changes are ready to push for review', share: 'Choose a one-liner or zip bundle',
    'open-logs': 'Companion logs opened',
  };
  if (messages[action]) return toast(messages[action]);
  if (action === 'vault-help') return dialog({ title: 'How approvals work', eyebrow: 'Key vault', body: '<p>Agents can list secret names and purposes. They can read a value only after approval unless it is auto-approved.</p><p>A lease belongs to one VM and one secret. When it ends, Construct redacts agent logs automatically and asks about other files.</p><p class="dialog-note">Auto-approved secrets have no lease or scrub. Keep that option off for critical credentials.</p>', actions: [{ label: 'Got it', primary: true }] });
  if (action === 'add-project') return simpleDialog('Add a project', 'Clone and configure', '<div class="dialog-field"><label>Git repository URL</label><input placeholder="https://git.example.com/team/project.git"></div><div class="dialog-field"><label>Profile name</label><input placeholder="project-name"></div><label style="display:flex;gap:8px;margin-top:16px;font-size:12px"><input type="checkbox" checked> Open the project after cloning</label>', 'Clone and add');
  if (action === 'add-secret') return simpleDialog('Add a secret', 'Native value entry', '<div class="dialog-field"><label>Name</label><input placeholder="service-token"></div><div class="dialog-field"><label>Purpose shown to agents</label><input placeholder="Read-only access to the test service"></div><div class="dialog-field"><label>Value</label><textarea rows="4"></textarea></div><div class="dialog-field"><label>Optional username</label><input></div>', 'Save secret');
  if (action === 'add-remote') return simpleDialog('Link a config repository', 'Shared profiles', '<div class="dialog-field"><label>Plain repository URL</label><input placeholder="https://git.example.com/team/configs.git"></div><p class="dialog-note" style="margin-top:16px">Do not embed a token in the URL. Git Credential Manager supplies authentication.</p>', 'Link repository');
  if (action === 'add-host') return simpleDialog('Add a remote host', 'Enroll before creating VMs', '<div class="dialog-field"><label>Host service URL</label><input placeholder="https://host.example:7462"></div><div class="dialog-field"><label>Authentication</label><select><option>Windows sign-in</option><option>Access token</option><option>Domain credentials</option></select></div><p class="dialog-note" style="margin-top:16px">Next, compare the certificate fingerprint once. The confirmed pin is shared with the PowerShell client.</p>', 'Check host');
  if (action === 'new-vm') return simpleDialog('Create a Construct VM', 'Review capacity before creation', '<div class="dialog-field"><label>Host</label><select><option>host.example</option><option>secondary-host.example</option></select></div><div class="dialog-field"><label>Name</label><input value="new-agent-vm"></div><div style="display:grid;grid-template-columns:repeat(3,1fr);gap:10px"><div class="dialog-field"><label>vCPUs</label><input type="number" value="4"></div><div class="dialog-field"><label>RAM GB</label><input type="number" value="8"></div><div class="dialog-field"><label>Disk GB</label><input type="number" value="128"></div></div>', 'Review and create');
  if (action === 'switch-vm') return toast('Use the instance picker in the header to retarget this flow');
  if (action === 'vault-hosts') return dialog({ title: 'Vault hosts and devices', eyebrow: 'One vault across your hosts', wide: true, body: '<div class="review-list"><div><small>host.example</small><strong>Always available · 1 paired phone</strong></div><div><small>secondary-host.example</small><strong>Locked to my PC · No phones</strong></div><div><small>Last sync</small><strong>24 secrets · No conflicts</strong></div></div><p>Always-available mode trusts host administrators with the values. Locked mode keeps the key off the host and unlocks only while this PC connects.</p><p class="dialog-note">Pairing uses separate T3 Code and approval-page origins. Vault keys are shown and imported only in native dialogs.</p>', actions: [{ label: 'Close', className: 'secondary-action' }, { label: 'Sync hosts', primary: true, onClick: () => toast('Both hosts synchronized') }] });
  if (action === 'review-settings') return dialog({ title: 'Apply your settings?', eyebrow: 'Review by effect', body: '<div class="review-list"><div><small>Apply now</small><strong>Live services and VM config</strong></div><div><small>After restart</small><strong>CPU and fixed RAM</strong></div><div><small>Next provision</small><strong>Agent plugins and setup options</strong></div><div><small>This PC only</small><strong>Companion preferences</strong></div></div><p class="dialog-note">The final implementation lists exact edits here. A restart always needs a separate confirmation.</p>', actions: [{ label: 'Keep editing', className: 'secondary-action' }, { label: 'Apply safe changes', primary: true, onClick: () => { state.dirty = false; render(); toast('Settings saved. Restart-only changes remain pending review.'); } }] });
  if (action === 'discard') { state.dirty = false; render(); return toast('Staged edits discarded'); }
  if (action === 'remove') return simpleDialog('Review instance removal', 'Client cleanup', '<p>Construct removes the SSH alias and key, editor platform entry, OpenCode server, T3 certificate trust, and per-instance state.</p><p class="dialog-note">A remote instance gets a separate “Keep the remote VM” choice. Shared profiles are never removed.</p>', 'Continue to final confirmation');
}

function handleClick(event) {
  const pageButton = event.target.closest('[data-page]');
  if (pageButton) return goTo(pageButton.dataset.page, pageButton.dataset.libraryTarget);
  const libraryTab = event.target.closest('[data-library-tab]');
  if (libraryTab) { state.libraryTab = libraryTab.dataset.libraryTab; return render(); }
  const task = event.target.closest('[data-task="continue"]');
  if (task) return document.querySelector('.continue-card')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  const guided = event.target.closest('[data-guided]');
  if (guided) return guidedChange(guided.dataset.guided);
  const openProject = event.target.closest('[data-open-project]');
  if (openProject) return toast(`Opening ${openProject.dataset.openProject} in its Remote-SSH workspace`);
  const request = event.target.closest('[data-request]');
  if (request) { state.selectedRequest = request.dataset.request; return render(); }
  const decision = event.target.closest('[data-decision]');
  if (decision) {
    const approved = decision.dataset.decision === 'approve';
    const id = decision.dataset.requestId;
    const done = () => { state.requests = state.requests.filter((item) => item.id !== id); state.selectedRequest = state.requests[0]?.id || null; render(); toast(approved ? 'Access approved with the shown limits' : 'Request denied. No value was released.'); };
    if (approved) return dialog({ title: 'Confirm this access', eyebrow: 'One VM · Only the listed secrets', body: '<p>The agent receives only the requested secrets on the named VM, with the shown use and time limits.</p><p class="dialog-note">Approving replaces an existing lease on the same VM and secret.</p>', actions: [{ label: 'Back', className: 'secondary-action' }, { label: 'Approve', primary: true, onClick: done }] });
    return done();
  }
  const adjust = event.target.closest('[data-adjust]');
  if (adjust) return simpleDialog('Limit this approval', 'The agent’s upper bound, your choice', '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>Uses</label><input type="number" value="1"></div><div class="dialog-field"><label>Time</label><select><option>10 minutes</option><option>1 hour</option><option>2 hours</option></select></div></div>', 'Use these limits');
  const editProfile = event.target.closest('[data-edit-profile]');
  if (editProfile) return simpleDialog(`Edit ${editProfile.dataset.editProfile}`, 'Project profile', '<div class="dialog-field"><label>SDKs</label><input value="node 22, dotnet 10"></div><div class="dialog-field"><label>Provision commands</label><textarea rows="4">npm install</textarea></div><div class="dialog-field"><label>MCP servers</label><textarea rows="3">[{ "name": "context7", "type": "stdio" }]</textarea></div>', 'Validate and save');
  const copy = event.target.closest('[data-copy]');
  if (copy) return toast(`${copy.dataset.copy} copied in a native dialog. Clipboard clears in 30 seconds.`);
  const editSecret = event.target.closest('[data-edit-secret]');
  if (editSecret) return simpleDialog(`Edit ${editSecret.dataset.editSecret}`, 'Secret metadata and value', '<div class="dialog-field"><label>Purpose shown to agents</label><input value="Credential used by automated tasks"></div><div class="dialog-field"><label>Username</label><input value="service-account"></div><p class="dialog-note" style="margin-top:16px">Changing the value revokes existing leases and scrubs VMs that saw the old value. Metadata edits keep access.</p>', 'Save secret');
  const revoke = event.target.closest('[data-revoke]');
  if (revoke) return dialog({ title: `Revoke ${revoke.dataset.revoke}?`, eyebrow: 'End lease and scrub', body: '<p>The VM loses access at once. Construct redacts agent logs automatically. Other files containing the value wait for your decision.</p>', actions: [{ label: 'Keep access', className: 'secondary-action' }, { label: 'Revoke and scrub', className: 'danger-action', onClick: () => toast('Lease revoked. Cleanup has started.') }] });
  const vmSettings = event.target.closest('[data-vm-settings]');
  if (vmSettings) return simpleDialog(`${vmSettings.dataset.vmSettings} settings`, 'Host administration', '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>vCPUs</label><input type="number" value="4"></div><div class="dialog-field"><label>Fixed RAM GB</label><input type="number" value="8"></div><div class="dialog-field"><label>Idle timeout</label><input type="number" value="45"></div><div class="dialog-field"><label>Idle action</label><select><option>Save</option><option>Shut down</option></select></div></div><p class="dialog-note" style="margin-top:16px">Only edited fields are sent. Idle applies now. Hardware waits for a confirmed cold start.</p>', 'Apply changed fields');
  const host = event.target.closest('[data-host]');
  if (host) return hostWorkspace(host.dataset.host);
  const action = event.target.closest('[data-action]');
  if (action) return handleAction(action.dataset.action);
}

document.addEventListener('click', handleClick);
document.addEventListener('change', (event) => {
  if (event.target.matches('[data-setting]')) {
    state.dirty = true;
    const summary = document.getElementById('change-summary');
    if (summary) { summary.innerHTML = changeSummary(); hydrateIcons(summary); }
  }
});
document.addEventListener('input', (event) => {
  if (event.target.id === 'settings-filter') {
    const query = event.target.value.toLowerCase();
    document.querySelectorAll('[data-searchable]').forEach((item) => { item.hidden = !item.dataset.searchable.includes(query); });
    document.querySelectorAll('.settings-group').forEach((group) => { group.hidden = !group.querySelector('[data-searchable]:not([hidden])'); });
  }
});
document.getElementById('path-instance').addEventListener('change', (event) => {
  toast(`Switched to ${event.target.value}. The current flow now targets that VM.`);
});

render();
