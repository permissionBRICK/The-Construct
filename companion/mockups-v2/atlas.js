const { icon, hydrateIcons, toast, dialog } = window.mockup;

const state = {
  page: 'overview',
  vaultTab: 'requests',
  settingsTab: 'general',
  settingsScope: 'vm',
  hostTab: 'overview',
  usagePeriod: 'daily',
  requests: 2,
  dirty: false,
};

const page = document.getElementById('page');
const pageMeta = {
  overview: ['Workspace', 'Overview', 'The selected VM, its live services, and the work that needs attention.'],
  projects: ['Configuration', 'Projects', 'Profiles define what each VM restores, installs, and opens.'],
  usage: ['Observability', 'Token usage', 'Exact token counts and estimated cost for the selected instance.'],
  vault: ['Local trust domain', 'Key vault', 'Approve access, inspect active leases, and manage synced hosts. Values stay in native dialogs.'],
  host: ['host.example', 'Host administration', 'Capacity, machines, people, media, and service operations in one place.'],
  settings: ['Preferences', 'Settings', 'Every setting names its scope and when the change takes effect.'],
};

function heading(section, title, subtitle, actions = '') {
  return `<div class="page-heading"><div><p class="eyebrow">${section}</p><h1>${title}</h1><p class="page-subtitle">${subtitle}</p></div><div class="heading-actions">${actions}</div></div>`;
}

function activityItems() {
  return `
    <li class="activity-item"><span class="activity-icon success">${icon('check')}</span><div class="activity-copy"><p><strong>Config sync completed</strong> across 6 selected profiles.</p><small>4 minutes ago</small></div></li>
    <li class="activity-item"><span class="activity-icon warning">${icon('key')}</span><div class="activity-copy"><p><strong>build-vm requested</strong> github-release and staging-db.</p><small>11 minutes ago · Waiting for you</small></div></li>
    <li class="activity-item"><span class="activity-icon">${icon('link')}</span><div class="activity-copy"><p><strong>Port 5173 opened</strong> as localhost:5173.</p><small>28 minutes ago · Web preview</small></div></li>
    <li class="activity-item"><span class="activity-icon success">${icon('refresh')}</span><div class="activity-copy"><p><strong>Codex updated</strong> to 0.31.0.</p><small>Today at 13:42</small></div></li>`;
}

function overviewPage() {
  return `${heading(...pageMeta.overview, `<button class="secondary-action" data-action="logs">${icon('file')}View logs</button><button class="primary-action" data-action="open-vm">${icon('arrowUpRight')}Open on VM</button>`)}
    <div class="notice">${icon('alert')}<span><strong>Reprovision recommended.</strong> agent-vm is one Construct release behind this PC. Your projects and data stay in place.</span><button class="primary-action" data-action="reprovision">Review update</button></div>
    <div class="overview-grid">
      <div class="overview-main">
        <section class="card hero-card">
          <div class="hero-top">
            <div><span class="live-label"><span class="status-dot"></span>Online and reachable</span><h2>agent-vm</h2><p>Ubuntu 24.04 LTS · Local Hyper-V · Connected 18 days ago</p></div>
            <div class="power-actions"><button class="secondary-action" data-action="console">${icon('monitor')}Console</button><button class="secondary-action" data-action="shutdown">${icon('power')}Shut down</button></div>
          </div>
          <div class="hero-actions"><button class="secondary-action" data-action="open-t3">${icon('globe')}Open T3 Code</button><button class="secondary-action" data-action="start-connect">${icon('terminal')}Open terminal</button><button class="secondary-action" data-action="more-lifecycle">${icon('more')}Lifecycle</button></div>
        </section>
        <section class="card metrics" aria-label="VM resources">
          <div class="metric"><span class="metric-label">${icon('cpu')}Compute</span><strong class="metric-value">4 <small>vCPUs</small></strong><span class="metric-note">Host capacity available</span></div>
          <div class="metric"><span class="metric-label">${icon('database')}Memory</span><strong class="metric-value">8 <small>GB</small></strong><span class="metric-note">Fixed allocation</span></div>
          <div class="metric"><span class="metric-label">${icon('drive')}Storage</span><strong class="metric-value">68 <small>/ 128 GB</small></strong><span class="metric-note">53% used · 60 GB free</span></div>
          <div class="metric"><span class="metric-label">${icon('mic')}Voice input</span><strong class="metric-value">Armed</strong><span class="metric-note">Mic opens only while recording</span></div>
        </section>
        <section class="card split-card">
          <section><div class="card-header"><h2>Coding agents</h2><button class="text-action" data-action="update-agents">Update all ${icon('refresh')}</button></div><ul class="agent-list">
            <li class="agent-row"><span class="agent-monogram">CC</span><div><strong>Claude Code</strong><small>Ready · authenticated</small></div><span class="agent-version">2.0.31</span></li>
            <li class="agent-row"><span class="agent-monogram">CX</span><div><strong>Codex</strong><small>Ready · authenticated</small></div><span class="agent-version">0.31.0</span></li>
            <li class="agent-row"><span class="agent-monogram">OC</span><div><strong>OpenCode</strong><small>Background watcher enabled</small></div><span class="agent-version">1.0.85</span></li>
            <li class="agent-row"><span class="agent-monogram">T3</span><div><strong>T3 Code <span class="update-pill">Update</span></strong><small>Stable · HTTPS ready</small></div><span class="agent-version">0.1.0</span></li>
          </ul></section>
          <section><div class="card-header"><h2>Open forwards</h2><button class="text-action" data-action="forward-help">How forwards work ${icon('help')}</button></div><ul class="forward-list">
            <li class="forward-row">${icon('globe')}<div><strong>Web preview</strong><small>vm:5173 → localhost:5173</small></div><button class="icon-button" aria-label="Open web preview" data-toast="Opened localhost:5173 in the preview browser">${icon('arrowUpRight')}</button></li>
            <li class="forward-row">${icon('database')}<div><strong>API server</strong><small>vm:3000 → localhost:3000</small></div><button class="icon-button" aria-label="Open API server" data-toast="Opened localhost:3000">${icon('arrowUpRight')}</button></li>
            <li class="forward-row">${icon('clock')}<div><strong>Storybook</strong><small>vm:6006 · Waiting for a free local port</small></div><button class="icon-button" aria-label="Inspect queued forward" data-toast="The forward will retry without changing its requested port">${icon('more')}</button></li>
          </ul></section>
        </section>
      </div>
      <aside class="card activity-card"><div class="card-header"><h2>Across your Construct</h2><span class="state-pill green">Live</span></div><ul class="activity-list">${activityItems()}</ul><div class="activity-footer"><button class="text-action" data-page="vault">Review 2 vault requests ${icon('arrowRight')}</button></div></aside>
    </div>`;
}

function projectRows() {
  const projects = [
    ['construct', 'The Construct', 'PowerShell · .NET 10 · Node', '2 repositories', true],
    ['shopfront', 'Shopfront', 'Node 22 · pnpm · Docker', '1 repository', true],
    ['mobile-notes', 'Mobile notes', 'Flutter · Android SDK', '2 repositories', true],
    ['docs-site', 'Documentation', 'Node 22 · Markdown', '1 repository', true],
    ['data-lab', 'Data lab', 'Python 3.12 · uv', '1 repository', false],
    ['legacy-tools', 'Legacy tools', '.NET 8 · PowerShell', '1 repository', false],
  ];
  return projects.map(([id, name, stack, repos, selected]) => `<div class="project-row"><label class="check-control" title="Select for next provision"><input type="checkbox" ${selected ? 'checked' : ''} data-project-select="${id}" aria-label="Select ${name}"></label><div class="project-name">${icon('folder')}<div><strong>${name}</strong><small>${repos}</small></div></div><span class="project-stack">${stack}</span><span class="project-sync">${icon('check')}Synced</span><div class="row-actions"><button class="icon-button" title="Open project" data-open-project="${name}">${icon('arrowUpRight')}</button><button class="icon-button" title="Edit profile" data-edit-project="${id}">${icon('edit')}</button></div></div>`).join('');
}

function projectsPage() {
  return `${heading(...pageMeta.projects, `<button class="secondary-action" data-action="export-config">${icon('download')}Export config</button><button class="primary-action" data-action="add-project">${icon('plus')}Add project</button>`)}
    <div class="sync-strip">${icon('git')}<div><strong>Configuration is in sync</strong><small>Host and agent-vm match · Last checked 4 minutes ago · Next automatic scan in 1 minute</small></div><button class="secondary-action" data-action="sync">${icon('refresh')}Sync now</button></div>
    <div class="toolbar"><label class="input-wrap">${icon('search')}<input placeholder="Filter profiles" aria-label="Filter project profiles" id="project-filter"></label><div class="segmented"><button class="active" data-project-filter="all">All 6</button><button data-project-filter="selected">Selected 4</button><button data-project-filter="remote">Shared</button></div></div>
    <div class="project-layout"><section class="card project-list" id="project-list">${projectRows()}</section><aside class="side-stack">
      <section class="card"><div class="card-header"><h2>Config remotes</h2><button class="icon-button" data-action="add-remote" aria-label="Add config remote">${icon('plus')}</button></div><div class="remote-item"><strong>git.example.com/team/agent-configs</strong><small>3 tracked profiles · Fetched 4 min ago</small><div class="remote-actions"><button class="text-action" data-action="import-config">Import</button><button class="text-action" data-action="push-config">Push changes</button></div></div><div class="remote-item"><strong>git.example.com/lab/base-profiles</strong><small>Read only · 8 profiles available</small><div class="remote-actions"><button class="text-action" data-action="import-config">Import</button><button class="text-action" data-action="publish-config">Publish</button></div></div></section>
      <section class="card vault-side-card"><h3>Selection applies next time</h3><p class="mini-copy">Checkboxes change the profiles used by the next reprovision or reinstall. Profile edits still sync immediately.</p><div class="remote-actions"><button class="text-action" data-action="profile-selection">Review next provision ${icon('arrowRight')}</button></div></section>
      <section class="card vault-side-card"><h3>Share a setup</h3><p class="mini-copy">Package selected profiles as a one-liner or zip bundle. Credentials in repository URLs are refused.</p><div class="remote-actions"><button class="text-action" data-action="share-config">Choose profiles ${icon('arrowRight')}</button></div></section>
    </aside></div>`;
}

function usagePage() {
  const values = state.usagePeriod === 'daily' ? ['18.4M', '$42.18', '6h 24m'] : state.usagePeriod === 'monthly' ? ['284.7M', '$618.42', '91h 08m'] : ['1.92B', '$4,106.80', '612h 45m'];
  const bars = [42, 66, 38, 80, 57, 91, 68, 74, 48, 86, 62, 96].map((height, index) => `<div class="bar-group"><div class="bar-stack"><i class="bar" style="height:${height}%"></i><i class="bar codex" style="height:${Math.max(8, height * .45)}%"></i><i class="bar opencode" style="height:${Math.max(4, height * .18)}%"></i></div><small>${index + 1}</small></div>`).join('');
  return `${heading(...pageMeta.usage, `<button class="secondary-action" data-action="export-usage">${icon('download')}Export JSON</button>`)}
    <div class="toolbar"><div class="segmented"><button class="${state.usagePeriod === 'daily' ? 'active' : ''}" data-period="daily">Today</button><button class="${state.usagePeriod === 'monthly' ? 'active' : ''}" data-period="monthly">This month</button><button class="${state.usagePeriod === 'total' ? 'active' : ''}" data-period="total">All time</button></div><span class="state-pill">Updated 2 min ago</span></div>
    <div class="usage-summary"><section class="card summary-card"><small>Total tokens</small><strong>${values[0]}</strong><span>Across Claude Code, Codex, and OpenCode</span></section><section class="card summary-card"><small>Estimated cost</small><strong>${values[1]}</strong><span>Token counts are exact. Cost is an estimate.</span></section><section class="card summary-card"><small>Active agent time</small><strong>${values[2]}</strong><span>Time with at least one running turn</span></section></div>
    <section class="card" style="margin-bottom:18px"><div class="card-header"><h2>Usage over time</h2><small>Tokens in millions · UTC days</small></div><div class="usage-chart"><div class="chart-bars">${bars}</div><div class="chart-legend"><span><i></i>Claude Code</span><span><i class="codex"></i>Codex</span><span><i class="opencode"></i>OpenCode</span></div></div></section>
    <section class="card"><div class="card-header"><h2>By agent</h2><small>Current period</small></div><table class="usage-table"><thead><tr><th>Agent</th><th>Input</th><th>Output</th><th>Cached</th><th>Estimated cost</th><th>Share</th></tr></thead><tbody><tr><td><strong>Claude Code</strong></td><td>3.8M</td><td>1.2M</td><td>8.9M</td><td>$28.74</td><td>68%</td></tr><tr><td><strong>Codex</strong></td><td>1.9M</td><td>640K</td><td>1.5M</td><td>$9.12</td><td>22%</td></tr><tr><td><strong>OpenCode</strong></td><td>680K</td><td>270K</td><td>490K</td><td>$4.32</td><td>10%</td></tr></tbody></table></section>`;
}

function requestCard(id, vm, names, reason, access) {
  return `<article class="request-card" data-request-card="${id}"><div class="request-head">${icon('key')}<strong>Access request</strong><small>Expires in 09:42</small></div><div class="request-body"><h3>${vm} asks for access to ${names.length} secrets</h3><p>Review what the agent can read and how long the access lasts. Values are not shown here.</p><div class="secret-chips">${names.map((name) => `<span class="secret-chip">${icon('key')}${name}</span>`).join('')}</div><div class="request-facts"><div><small>Requested access</small><strong>${access}</strong></div><div><small>Source</small><strong>${vm} · T3 Code thread</strong></div></div><div class="request-reason">“${reason}”</div></div><div class="request-actions"><button class="secondary-action" data-adjust-request="${id}">Adjust access</button><button class="secondary-action" data-deny-request="${id}">Deny</button><button class="primary-action" data-approve-request="${id}">${icon('check')}Approve request</button></div></article>`;
}

function vaultContent() {
  if (state.vaultTab === 'requests') {
    return `<div class="vault-layout"><div><div id="request-list">${state.requests > 0 ? requestCard('release', 'build-vm', ['github-release', 'staging-db'], 'Deploy the release candidate and run the database migration.', '10 uses · 2 hours') : ''}${state.requests > 1 ? requestCard('read', 'agent-vm', ['npm-publish'], 'Publish the patched package to the test registry.', '1 use · 10 minutes') : ''}</div>${state.requests === 0 ? `<section class="card empty-state">${icon('check')}<h3>No requests waiting</h3><p>New agent requests will appear here and beside the tray icon.</p></section>` : ''}</div><aside class="side-stack"><section class="card vault-side-card"><h3>Approval checklist</h3><p>Requests come from an untrusted VM. Read the reason and limit access to the task.</p><ul class="safety-list"><li>${icon('check')}Prefer a single use for one command.</li><li>${icon('check')}Use a short lease for a sequence of commands.</li><li>${icon('check')}Revoke anything you did not expect.</li></ul></section><section class="card vault-side-card"><h3>Approval destinations</h3><p class="mini-copy">This PC and one paired phone can answer. The first answer wins; the other request closes automatically.</p><span class="state-pill green">${icon('phone')}Phone paired</span></section></aside></div>`;
  }
  if (state.vaultTab === 'secrets') {
    const rows = [
      ['github-release', 'Release repository token', 'release-bot', '1 active lease', false],
      ['staging-db', 'Staging database administrator', 'deploy', '1 active lease', false],
      ['npm-read', 'Read-only package mirror', 'ci', 'Auto-approved', true],
      ['npm-publish', 'Test registry publishing', 'builder', 'No active leases', false],
      ['ssh-deploy', 'Deployment server private key', 'deploy', 'No active leases', false],
    ];
    return `<div class="toolbar"><label class="input-wrap">${icon('search')}<input placeholder="Find a secret" aria-label="Find a secret"></label><button class="secondary-action" data-action="vault-sync">${icon('refresh')}Sync hosts</button><button class="primary-action" data-action="add-secret">${icon('plus')}Add secret</button></div><section class="card secrets-table">${rows.map(([name, description, username, access, auto]) => `<div class="secret-row"><div class="secret-name">${icon('key')}<div><strong>${name}</strong><small>${description}</small></div></div><span class="secret-user">${username}</span><span class="state-pill ${access.includes('active') ? 'amber' : auto ? 'green' : ''}">${access}</span><label title="Auto-approve on every VM"><input class="switch" type="checkbox" ${auto ? 'checked' : ''} data-auto-secret="${name}" aria-label="Auto-approve ${name}"></label><div class="row-actions"><button class="icon-button" title="Copy secret" data-copy-secret="${name}">${icon('copy')}</button><button class="icon-button" title="Edit secret" data-edit-secret="${name}">${icon('edit')}</button></div></div>`).join('')}</section>`;
  }
  if (state.vaultTab === 'access') {
    return `<div class="page-heading"><div><h1 style="font-size:18px">Active leases</h1><p class="page-subtitle">Access ends when its time or uses run out. Revoking starts a VM scrub.</p></div></div><div class="lease-grid"><section class="card lease-card"><div class="lease-card-head"><div><h3>github-release</h3><p>build-vm · Requested for a release deployment</p></div><span class="state-pill amber">8 uses left</span></div><div class="lease-meta"><div><small>Expires</small><strong>Today at 16:30</strong></div><div><small>Approved from</small><small>This PC</small></div></div><button class="secondary-action" data-revoke="github-release on build-vm">Revoke access</button></section><section class="card lease-card"><div class="lease-card-head"><div><h3>staging-db</h3><p>build-vm · Requested for a database migration</p></div><span class="state-pill amber">8 uses left</span></div><div class="lease-meta"><div><small>Expires</small><strong>Today at 16:30</strong></div><div><small>Approved from</small><strong>This PC</strong></div></div><button class="secondary-action" data-revoke="staging-db on build-vm">Revoke access</button></section></div><section class="card" style="margin-top:18px"><div class="card-header"><h2>Pending cleanup</h2><span class="state-pill">1 VM offline</span></div><div class="request-body"><p style="color:var(--muted);font-size:10px">test-vm will be scrubbed for <strong style="color:var(--text)">old-api-token</strong> when it comes online. Agent logs are redacted automatically. Other files wait for your decision.</p><button class="text-action" style="margin-top:14px" data-action="discard-scrub">Discard pending scrub for removed VM</button></div></section>`;
  }
  if (state.vaultTab === 'activity') {
    const events = [
      ['14:28', 'key', 'Access requested', 'build-vm asked for github-release and staging-db.', 'Waiting'],
      ['14:12', 'check', 'Cleanup completed', 'Agent logs on agent-vm were redacted. No other files contained the value.', 'Complete'],
      ['13:58', 'phone', 'Request answered on phone', 'npm-read was approved for build-vm.', 'Approved'],
      ['13:30', 'refresh', 'Host sync completed', '24 secrets synced with host.example. No conflicts.', 'Synced'],
      ['12:46', 'alert', 'File decision needed', 'A deployment note on test-vm contains old-api-token.', 'Review'],
    ];
    return `<section class="card"><div class="card-header"><h2>Vault activity</h2><button class="secondary-action" data-action="export-activity">${icon('download')}Export safe log</button></div>${events.map(([time, glyph, title, copy, pill]) => `<div class="timeline-event"><time>${time}</time><span class="activity-icon">${icon(glyph)}</span><div><p><strong>${title}</strong></p><small>${copy}</small></div><span class="state-pill">${pill}</span></div>`).join('')}</section>`;
  }
  return `<div class="notice info">${icon('info')}<span>Names and descriptions remain readable on a locked host. Values and usernames are encrypted under your vault key.</span></div><section class="card"><div class="host-card"><div class="host-card-head"><div><h3>host.example</h3><p>Hyper-V host · Synced 4 minutes ago · 24 secrets</p></div><span class="state-pill green">Always available</span></div><div class="host-details"><div><small>Vault mode</small><strong>Host can answer while this PC is off</strong></div><div><small>Paired devices</small><strong>1 phone · Last active 13:58</strong></div><div><small>Approval page</small><strong>vault.example.net</strong></div></div><div class="host-card-actions"><button class="secondary-action" data-action="host-mode">Change mode</button><button class="secondary-action" data-action="pair-phone">${icon('phone')}Pair a phone</button><button class="secondary-action" data-action="show-vault-key">${icon('key')}Show vault key</button><button class="secondary-action" data-action="import-vault-key">Import key</button></div></div><div class="host-card"><div class="host-card-head"><div><h3>secondary-host.example</h3><p>Proxmox host · Synced 12 minutes ago · 24 secrets</p></div><span class="state-pill amber">Locked to my PC</span></div><div class="host-details"><div><small>Vault mode</small><strong>Unlocked only while this PC connects</strong></div><div><small>Paired devices</small><strong>No paired phones</strong></div><div><small>Approval page</small><strong>Not configured</strong></div></div><div class="host-card-actions"><button class="secondary-action" data-action="host-mode">Change mode</button><button class="secondary-action" data-action="pair-phone">${icon('phone')}Pair a phone</button><button class="secondary-action" data-action="show-vault-key">${icon('key')}Show vault key</button></div></div></section>`;
}

function vaultPage() {
  const tabs = [['requests', 'Requests', state.requests], ['secrets', 'Secrets'], ['access', 'Access'], ['activity', 'Activity'], ['hosts', 'Hosts & devices']];
  return `${heading('Local trust domain', `<span class="vault-heading"><span class="vault-mark">${icon('shield')}</span><span>Key vault</span></span>`, pageMeta.vault[2], `<span class="state-pill green">${icon('lock')}DPAPI protected</span>`)}<nav class="vault-tabs" aria-label="Key vault sections">${tabs.map(([id, label, count]) => `<button class="${state.vaultTab === id ? 'active' : ''}" data-vault-tab="${id}">${label}${count ? `<span class="tab-count">${count}</span>` : ''}</button>`).join('')}</nav>${vaultContent()}`;
}

function hostContent() {
  if (state.hostTab === 'vms') {
    return `<section class="card data-table-wrap"><table class="data-table"><thead><tr><th>Machine</th><th>Owner</th><th>State</th><th>Resources</th><th>Idle / lease</th><th>Construct</th><th></th></tr></thead><tbody><tr><td><strong>build-vm</strong><small>Primary · Private</small></td><td>Local user</td><td><span class="state-pill green">Running</span></td><td>4 vCPU · 8 GB</td><td>Save after 45 min</td><td>main@a81d2f</td><td><button class="secondary-action" data-vm-settings="build-vm">VM settings</button></td></tr><tr><td><strong>review-vm</strong><small>Primary · Private</small></td><td>Teammate</td><td><span class="state-pill green">Running</span></td><td>8 vCPU · 16 GB</td><td>Save after 30 min</td><td>main@a81d2f</td><td><button class="secondary-action" data-vm-settings="review-vm">VM settings</button></td></tr><tr><td><strong>windows-test</strong><small>Child of build-vm · Shared host-wide</small></td><td>Local user</td><td><span class="state-pill">Off</span></td><td>4 vCPU · 8 GB</td><td>Retained · expires on start</td><td>Not provisioned</td><td><button class="secondary-action" data-vm-settings="windows-test">Inspect</button></td></tr><tr><td><strong>linux-test</strong><small>Child of build-vm · Private</small></td><td>Local user</td><td><span class="state-pill red">Overdue</span></td><td>2 vCPU · 4 GB</td><td>Graceful shutdown timed out</td><td>Not provisioned</td><td><button class="secondary-action" data-action="inspect-job">Inspect job</button></td></tr></tbody></table></section>`;
  }
  if (state.hostTab === 'users') {
    return `<div class="page-heading"><div><h1 style="font-size:18px">Users and allowances</h1><p class="page-subtitle">Blank delegation limits inherit the host default. New tokens are shown once in a native dialog.</p></div><button class="primary-action" data-action="register-user">${icon('plus')}Register user</button></div><section class="card data-table-wrap"><table class="data-table"><thead><tr><th>Identity</th><th>Role</th><th>Primary VMs</th><th>Child allowance</th><th>Host forwards</th><th>Credentials</th><th></th></tr></thead><tbody><tr><td><strong>Local user</strong><small>Windows sign-in</small></td><td>Administrator</td><td>1 / 4</td><td>8 vCPU · 16 GB · 4 slots</td><td>Allowed</td><td>1 active token</td><td><button class="secondary-action" data-action="edit-allowance">Edit</button></td></tr><tr><td><strong>Teammate</strong><small>Windows sign-in</small></td><td>User</td><td>1 / 2</td><td>Inherited</td><td>Allowed</td><td>No tokens</td><td><button class="secondary-action" data-action="edit-allowance">Edit</button></td></tr></tbody></table></section>`;
  }
  const generic = {
    media: ['Media inventory', 'Primary ISO catalog and child installation media. Source URLs are redacted.', ['Ubuntu 24.04 LTS', 'Public ISO · Verified SHA-256', 'Ready'], ['Windows 11 Evaluation', 'Owner uploaded · 6.2 GB', 'Ready'], ['windows-test install media', 'Dedicated child media · 6.2 GB', 'In use']],
    operations: ['Jobs and audit', 'Active work, failures that can be retried, and a credential-safe audit trail.', ['Child shutdown · linux-test', 'Graceful shutdown timed out', 'Retry'], ['Config sync', 'No service operation required', 'Complete'], ['Media verification', 'Ubuntu 24.04 LTS checksum verified', 'Complete']],
    configuration: ['Host configuration', 'Each section validates independently and explains its keys before saving.', ['Key vault', 'Approval page: https://vault.example.net', 'Edit JSON'], ['Capacity policy', 'Enforce runtime CPU and RAM budgets', 'Edit JSON'], ['Networking', 'Per-VM public host names enabled', 'Edit JSON']],
    maintenance: ['Service maintenance', 'Stage, apply, resume, cancel, or resolve a host service update.', ['Current service', 'constructd 1.8.0 · Healthy', 'Current'], ['Available update', 'constructd 1.8.2 · Published 2 days ago', 'Stage update'], ['Rollback point', 'constructd 1.8.0 · Database compatible', 'Available']],
  };
  if (generic[state.hostTab]) {
    const [title, copy, ...rows] = generic[state.hostTab];
    return `<div class="page-heading"><div><h1 style="font-size:18px">${title}</h1><p class="page-subtitle">${copy}</p></div></div><section class="card">${rows.map(([name, detail, action]) => `<div class="remote-item" style="display:flex;align-items:center;gap:18px"><div style="flex:1"><strong>${name}</strong><small>${detail}</small></div><button class="secondary-action" data-toast="${action} opened for ${name}">${action}</button></div>`).join('')}</section>`;
  }
  return `<div class="capacity-grid"><section class="card capacity-card"><div class="capacity-head"><span>Runtime memory</span><strong>24 / 64 GB</strong></div><div class="progress"><i style="width:38%"></i></div><small>40 GB available · Enforced</small></section><section class="card capacity-card"><div class="capacity-head"><span>Virtual processors</span><strong>16 / 32</strong></div><div class="progress"><i style="width:50%"></i></div><small>16 available · Enforced</small></section><section class="card capacity-card"><div class="capacity-head"><span>Storage</span><strong>612 GB / 1.8 TB</strong></div><div class="progress"><i style="width:34%"></i></div><small>1.2 TB free · Observed</small></section></div><div class="overview-grid"><div class="overview-main"><section class="card"><div class="card-header"><h2>Service health</h2><span class="state-pill green">Healthy</span></div><div class="metrics"><div class="metric"><span class="metric-label">Version</span><strong class="metric-value">1.8.0</strong><span class="metric-note">Update available</span></div><div class="metric"><span class="metric-label">Machines</span><strong class="metric-value">8</strong><span class="metric-note">5 primary · 3 children</span></div><div class="metric"><span class="metric-label">Active jobs</span><strong class="metric-value">2</strong><span class="metric-note">Media and cleanup</span></div><div class="metric"><span class="metric-label">Maintenance</span><strong class="metric-value">Off</strong><span class="metric-note">Mutations available</span></div></div></section><section class="card"><div class="card-header"><h2>Needs attention</h2></div><div class="notice" style="margin:0 16px 16px">${icon('alert')}<span><strong>linux-test is overdue.</strong> The host asked for a graceful shutdown, but the guest did not stop. It has not been forced off.</span><button class="primary-action" data-action="inspect-job">Inspect</button></div><div class="notice info" style="margin:0 16px 16px">${icon('info')}<span>One unmanaged Hyper-V VM was found. It is left alone until you register it.</span><button class="text-action" data-action="register-vm">Review</button></div></section></div><aside class="card activity-card"><div class="card-header"><h2>Recent operations</h2></div><ul class="activity-list"><li class="activity-item"><span class="activity-icon success">${icon('check')}</span><div class="activity-copy"><p><strong>VM settings applied</strong> to build-vm.</p><small>18 minutes ago</small></div></li><li class="activity-item"><span class="activity-icon">${icon('download')}</span><div class="activity-copy"><p><strong>Ubuntu ISO verified</strong> and cached.</p><small>42 minutes ago</small></div></li><li class="activity-item"><span class="activity-icon warning">${icon('alert')}</span><div class="activity-copy"><p><strong>Child lease overdue</strong> for linux-test.</p><small>1 hour ago</small></div></li></ul></aside></div>`;
}

function hostPage() {
  const tabs = [['overview', 'Overview'], ['vms', 'VMs'], ['users', 'Users'], ['media', 'Media'], ['operations', 'Operations'], ['configuration', 'Configuration'], ['maintenance', 'Maintenance']];
  return `${heading(...pageMeta.host, `<button class="secondary-action" data-action="host-refresh">${icon('refresh')}Refresh</button><button class="primary-action" data-action="new-remote-vm">${icon('plus')}New Construct VM</button>`)}<nav class="vault-tabs host-tabs">${tabs.map(([id, label]) => `<button class="${state.hostTab === id ? 'active' : ''}" data-host-tab="${id}">${label}</button>`).join('')}</nav>${hostContent()}`;
}

function generalSettings() {
  return `<section class="card settings-section"><div class="settings-section-head"><h2>Agent experience</h2><p>Live services for this instance. Changes apply without a rebuild unless noted.</p></div><div class="setting-row"><div class="setting-copy"><strong>Microphone passthrough</strong><small>Stream this PC's microphone only while Claude records.</small><span class="setting-effect">Applies immediately · Per VM</span></div><label class="setting-control"><input class="switch" type="checkbox" checked data-setting="Microphone passthrough"><span class="setting-unit">Armed</span></label></div><div class="setting-row"><div class="setting-copy"><strong>T3 Code web GUI</strong><small>Serve the paired T3 workspace over HTTPS and install matching Desktop builds.</small><span class="setting-effect">Applies immediately · Service restarts</span></div><label class="setting-control"><input class="switch" type="checkbox" checked data-setting="T3 Code web GUI"></label></div><div class="setting-row"><div class="setting-copy"><strong>T3 Code channel</strong><small>Stable follows releases. Nightly follows the latest CI build.</small><span class="setting-effect">Changing channel reinstalls T3 Code</span></div><label class="setting-control"><select data-setting="T3 Code channel"><option>Stable</option><option>Nightly</option></select></label></div><div class="setting-row"><div class="setting-copy"><strong>T3 Code proxy address</strong><small>Optional trusted HTTPS origin used for pairing links and phone access.</small><span class="setting-effect">Writes directly to this VM's config.env</span></div><label class="setting-control"><input type="text" value="https://t3.example.net" data-setting="T3 proxy"><button class="secondary-action" data-action="apply-proxy">Apply</button></label></div></section><section class="card settings-section"><div class="settings-section-head"><h2>Connection</h2><p>Local integration behavior for agents and this PC.</p></div><div class="setting-row"><div class="setting-copy"><strong>OpenCode background watcher</strong><small>Let detached jobs wake the originating OpenCode session.</small><span class="setting-effect">Requires reprovision</span></div><label class="setting-control"><input class="switch" type="checkbox" checked data-setting="OpenCode background watcher"></label></div><div class="setting-row"><div class="setting-copy"><strong>Windows file share</strong><small>Expose the VM repository share for mapped drives.</small><span class="setting-effect">Applies next provision</span></div><label class="setting-control"><input class="switch" type="checkbox" data-setting="Windows file share"></label></div></section>`;
}

function resourceSettings() {
  return `<section class="card settings-section"><div class="settings-section-head"><h2>VM resources</h2><p>CPU and RAM changes need a graceful stop and cold start. Disk changes need a reinstall.</p></div><div class="setting-row"><div class="setting-copy"><strong>Virtual processors</strong><small>Current running VM: 4 vCPUs. Host maximum: 32.</small><span class="setting-effect">Restart to apply</span></div><label class="setting-control"><input type="number" value="4" min="1" max="32" data-setting="vCPUs"><span class="setting-unit">vCPUs</span></label></div><div class="setting-row"><div class="setting-copy"><strong>Fixed memory</strong><small>Current running VM: 8 GB. Dynamic memory is not used.</small><span class="setting-effect">Restart to apply</span></div><label class="setting-control"><input type="number" value="8" min="2" max="48" data-setting="Memory"><span class="setting-unit">GB</span></label></div><div class="setting-row"><div class="setting-copy"><strong>Virtual disk</strong><small>68 GB used. Partition and filesystem grow during a rebuild.</small><span class="setting-effect">Reinstall or redownload to apply</span></div><label class="setting-control"><input type="number" value="128" min="68" data-setting="Disk"><span class="setting-unit">GB</span></label></div><div class="setting-row"><div class="setting-copy"><strong>Automatic checkpoints</strong><small>Recommended off for disposable VMs. Manual checkpoints are never removed automatically.</small><span class="setting-effect">Can apply now with one UAC prompt</span></div><label class="setting-control"><input class="switch" type="checkbox" data-setting="Automatic checkpoints"></label></div></section><section class="card settings-section"><div class="settings-section-head"><h2>Idle policy</h2><p>Remote VMs only. Long-running agents and active forwards count as activity.</p></div><div class="setting-row"><div class="setting-copy"><strong>After inactivity</strong><small>Save frees host RAM and resumes transparently on the next start.</small><span class="setting-effect">Applies immediately · Host cap 120 minutes</span></div><label class="setting-control"><input type="number" value="45" data-setting="Idle timeout"><span class="setting-unit">minutes</span><select data-setting="Idle action"><option>Save</option><option>Shut down</option><option>Off</option></select></label></div></section>`;
}

function backupSettings() {
  return `<section class="card settings-section"><div class="settings-section-head"><h2>Reinstall and backup</h2><p>Choose what survives a rebuild before choosing how aggressively to recreate the VM.</p></div><div class="setting-row"><div class="setting-copy"><strong>Chat history kept in backups</strong><small>Older transcripts are omitted from the backup only. Agent memory is always kept. Enter 0 to keep all history.</small><span class="setting-effect">Used by reinstall, redownload, and export</span></div><label class="setting-control"><input type="number" value="30" data-setting="Chat retention"><span class="setting-unit">days</span></label></div><div class="setting-row"><div class="setting-copy"><strong>Default reinstall mode</strong><small>Save and restore configuration, restore an existing backup, or start clean.</small><span class="setting-effect">Can still be overridden for each reinstall</span></div><label class="setting-control"><select data-setting="Reinstall mode"><option>Save and restore</option><option>Restore existing backup</option><option>Clean wipe</option></select></label></div></section><section class="card settings-section"><div class="settings-section-head"><h2>Lifecycle actions</h2><p>Destructive actions always open a review with the exact target and data consequences.</p></div><div class="setting-row"><div class="setting-copy"><strong>Reprovision agent-vm</strong><small>Apply the installed Construct and saved profiles without deleting VM data.</small></div><button class="secondary-action" data-action="reprovision">Review reprovision</button></div><div class="setting-row"><div class="setting-copy"><strong>Custom reinstall</strong><small>Choose a one-time backup and restore policy before recreating this VM.</small></div><button class="secondary-action" data-action="custom-reinstall">Choose reinstall</button></div><div class="setting-row"><div class="setting-copy"><strong>Remove this instance</strong><small>Remove client state and, for a remote VM, optionally delete the machine. Shared config is not touched.</small></div><button class="danger-action" data-action="remove-instance">Remove</button></div></section>`;
}

function companionSettings() {
  return `<section class="card settings-section"><div class="settings-section-head"><h2>Companion on this PC</h2><p>These settings affect every instance and stay outside config sync.</p></div><div class="setting-row"><div class="setting-copy"><strong>Start with Windows</strong><small>Keep forwards, notifications, and approvals available without VS Code.</small></div><label class="setting-control"><input class="switch" type="checkbox" checked data-setting="Start with Windows"></label></div><div class="setting-row"><div class="setting-copy"><strong>Desktop notifications</strong><small>Show agent notifications in the Windows notification center.</small></div><label class="setting-control"><input class="switch" type="checkbox" checked data-setting="Notifications"></label></div><div class="setting-row"><div class="setting-copy"><strong>Capture device</strong><small>Used for microphone passthrough and the Construct host bridge.</small></div><label class="setting-control"><select data-setting="Capture device"><option>Default microphone</option><option>USB headset</option><option>Studio microphone</option></select></label></div><div class="setting-row"><div class="setting-copy"><strong>Forward links</strong><small>Loopback keeps links private. A host label advertises this PC on the LAN and binds all interfaces.</small></div><label class="setting-control"><select data-setting="Forward links"><option>Loopback only</option><option>This PC on the LAN</option><option>Disabled</option></select></label></div><div class="setting-row"><div class="setting-copy"><strong>Patch repair delay</strong><small>Recheck reversible agent patches after startup. Set 0 to disable automatic repair.</small></div><label class="setting-control"><input type="number" value="20" data-setting="Repatch delay"><span class="setting-unit">seconds</span></label></div><div class="setting-row"><div class="setting-copy"><strong>Debug logging</strong><small>Keep lifecycle consoles open and write detailed diagnostics. Credentials are always redacted.</small></div><label class="setting-control"><input class="switch" type="checkbox" data-setting="Debug logging"></label></div></section>`;
}

function settingsContent() {
  if (state.settingsScope === 'companion') return companionSettings();
  if (state.settingsTab === 'resources') return resourceSettings();
  if (state.settingsTab === 'backup') return backupSettings();
  return generalSettings();
}

function settingsPage() {
  const tabs = [['general', 'Access & services'], ['resources', 'VM resources'], ['backup', 'Backup & lifecycle']];
  return `${heading(...pageMeta.settings, `<button class="secondary-action" data-action="open-logs">${icon('file')}Open logs</button>`)}<div class="settings-scope">${icon(state.settingsScope === 'vm' ? 'server' : 'monitor')}<div><strong>${state.settingsScope === 'vm' ? 'agent-vm settings' : 'This Companion'}</strong><small>${state.settingsScope === 'vm' ? 'Only the selected instance changes' : 'Shared application preferences on this PC'}</small></div><div class="segmented"><button class="${state.settingsScope === 'vm' ? 'active' : ''}" data-settings-scope="vm">Selected VM</button><button class="${state.settingsScope === 'companion' ? 'active' : ''}" data-settings-scope="companion">This PC</button></div></div><div class="settings-layout"><nav class="settings-nav">${state.settingsScope === 'vm' ? tabs.map(([id, label]) => `<button class="${state.settingsTab === id ? 'active' : ''}" data-settings-tab="${id}">${label}</button>`).join('') : '<button class="active">Companion preferences</button>'}</nav><div><div id="settings-content">${settingsContent()}</div><div id="save-bar">${state.dirty ? saveBar() : ''}</div></div></div>`;
}

function saveBar() {
  return `<div class="save-bar">${icon('info')}<div><strong>Changes ready to review</strong><small>Nothing has been applied yet.</small></div><button class="secondary-action" data-action="discard-settings">Discard</button><button class="primary-action" data-action="review-settings">Review changes ${icon('arrowRight')}</button></div>`;
}

function render() {
  const renderers = { overview: overviewPage, projects: projectsPage, usage: usagePage, vault: vaultPage, host: hostPage, settings: settingsPage };
  page.innerHTML = renderers[state.page]();
  document.querySelectorAll('.nav-item').forEach((item) => item.classList.toggle('active', item.dataset.page === state.page));
  hydrateIcons(page);
  page.focus({ preventScroll: true });
  window.scrollTo({ top: 0, behavior: 'instant' });
}

function goTo(nextPage) {
  state.page = nextPage;
  state.dirty = false;
  document.getElementById('sidebar').classList.remove('open');
  render();
}

function simpleForm(title, eyebrow, fields, confirmation = 'Save') {
  dialog({ title, eyebrow, body: `${fields}<p class="dialog-note" style="margin-top:16px">This prototype uses sample data. No real machine or secret will be changed.</p>`, actions: [{ label: 'Cancel', className: 'secondary-action' }, { label: confirmation, primary: true, onClick: () => toast(`${title} completed`) }] });
}

function openSearch() {
  const element = document.createElement('dialog');
  element.className = 'search-dialog';
  element.innerHTML = `<div class="command-input">${icon('search')}<input autofocus placeholder="Search pages, projects, secrets, or commands" aria-label="Command search"><kbd>Esc</kbd></div><div class="command-results"><p>Go to</p><button data-command-page="overview">${icon('home')}Overview<small>Selected VM</small></button><button data-command-page="projects">${icon('folder')}Projects<small>6 profiles</small></button><button data-command-page="vault">${icon('shield')}Key vault requests<small>${state.requests} waiting</small></button><p>Actions</p><button data-command-action="add-project">${icon('plus')}Add project<small>Clone and create profile</small></button><button data-command-action="sync">${icon('refresh')}Sync configuration<small>All selected profiles</small></button><button data-command-action="reprovision">${icon('refresh')}Reprovision agent-vm<small>Keep VM data</small></button></div>`;
  document.body.append(element);
  element.showModal();
  element.querySelector('input').focus();
  element.addEventListener('close', () => element.remove());
  element.querySelectorAll('[data-command-page]').forEach((button) => button.addEventListener('click', () => { goTo(button.dataset.commandPage); element.close(); }));
  element.querySelectorAll('[data-command-action]').forEach((button) => button.addEventListener('click', () => { element.close(); handleAction(button.dataset.commandAction); }));
}

function handleAction(action) {
  const simple = {
    logs: 'Opened the Companion log viewer', 'open-logs': 'Opened the Companion log viewer',
    'open-vm': 'Opening /root/repos on agent-vm through Remote-SSH', 'start-connect': 'Opening a terminal on agent-vm',
    console: 'Creating a fresh, time-limited console link', 'open-t3': 'Opening the paired T3 Code workspace',
    sync: 'Configuration synchronized. No conflicts found.', 'update-agents': 'Agent update check started',
    'export-config': 'Configuration export prepared without secret values', 'export-usage': 'Usage JSON exported for the current period',
    'export-activity': 'Credential-safe vault activity exported', 'vault-sync': 'Both hosts are synchronized',
    'forward-help': 'Forwards retry on the same port until you close them', 'apply-proxy': 'Proxy address applied to agent-vm',
    'host-refresh': 'Host state refreshed', 'discard-scrub': 'Pending scrub discarded for the removed VM',
    'inspect-job': 'Opened the graceful shutdown job and audit details', 'register-vm': 'Reviewing the unmanaged VM without changing it',
    'profile-selection': 'Four profiles are selected for the next provision', 'import-config': 'Found 11 valid profiles. None are selected by default.',
    'push-config': 'Three tracked profiles are ready to push for review', 'publish-config': 'Two local profiles can be published',
    'share-config': 'Choose linked profiles for a one-liner or local profiles for a zip bundle',
  };
  if (simple[action]) return toast(simple[action]);
  if (action === 'add-project') return simpleForm('Add project', 'Clone and configure', `<div class="dialog-field"><label for="repo-url">Git repository URL</label><input id="repo-url" placeholder="https://git.example.com/team/project.git"></div><div class="dialog-field"><label for="profile-name">Profile name</label><input id="profile-name" placeholder="project-name"></div><label style="display:flex;gap:8px;align-items:center;margin-top:16px;font-size:12px"><input type="checkbox" checked> Open in a new Remote-SSH window after cloning</label>`, 'Clone and add');
  if (action === 'add-remote') return simpleForm('Link a config repository', 'Shared profiles', `<div class="dialog-field"><label for="config-url">Repository URL</label><input id="config-url" placeholder="https://git.example.com/team/configs.git"></div><p class="dialog-note" style="margin-top:16px">Use a plain URL without embedded credentials. Git Credential Manager will supply authentication.</p>`, 'Link repository');
  if (action === 'add-secret' || action === 'edit-secret') return simpleForm(action === 'add-secret' ? 'Add a secret' : 'Edit secret', 'Native secret dialog', `<div class="dialog-field"><label for="secret-name">Name</label><input id="secret-name" placeholder="service-token"></div><div class="dialog-field"><label for="secret-description">Description shown to agents</label><input id="secret-description" placeholder="What this credential is for"></div><div class="dialog-field"><label for="secret-value">Value</label><textarea id="secret-value" rows="4" placeholder="The value never enters the webview"></textarea></div><div class="dialog-field"><label for="secret-username">Optional username</label><input id="secret-username"></div><label style="display:flex;gap:8px;align-items:center;margin-top:16px;font-size:12px"><input type="checkbox"> Load value from a file</label>`, 'Save secret');
  if (action === 'reprovision') return dialog({ title: 'Reprovision agent-vm?', eyebrow: 'Keep data · Apply updates', body: `<p>This applies the Construct installed on this PC and the four selected project profiles. Existing repositories, agent auth, and VM data stay in place.</p><div class="dialog-note"><strong>Expected interruption:</strong> T3 Code and some agent services restart. Active jobs are allowed to finish where supported.</div>`, actions: [{ label: 'Cancel', className: 'secondary-action' }, { label: 'Start reprovision', primary: true, onClick: () => toast('Reprovision started. Progress will appear in the activity feed.') }] });
  if (action === 'shutdown') return dialog({ title: 'Shut down agent-vm?', eyebrow: 'Graceful power off', body: '<p>Ubuntu will shut down normally. Open tunnels stop and their requested ports are retained for the next start.</p>', actions: [{ label: 'Keep running', className: 'secondary-action' }, { label: 'Shut down', className: 'danger-action', onClick: () => toast('Graceful shutdown requested') }] });
  if (action === 'more-lifecycle') return dialog({ title: 'Lifecycle actions', eyebrow: 'agent-vm', body: '<p>Reprovision keeps data. Reinstall and redownload recreate the VM and require a typed confirmation in the elevated console.</p>', wide: false, actions: [{ label: 'Reprovision', className: 'secondary-action', onClick: () => setTimeout(() => handleAction('reprovision')) }, { label: 'Custom reinstall', className: 'secondary-action', onClick: () => toast('Choose save-and-restore, restore-existing, or clean wipe') }, { label: 'Redownload', className: 'danger-action', onClick: () => toast('Redownload requires a fresh ISO and typed confirmation') }] });
  if (action === 'custom-reinstall') return dialog({ title: 'Custom reinstall', eyebrow: 'One-time data policy', body: '<div class="dialog-field"><label for="reinstall-mode">What should happen to agent configuration?</label><select id="reinstall-mode"><option>Save current config and restore it</option><option>Restore an existing backup</option><option>Clean wipe, restore nothing</option></select></div><p class="dialog-note" style="margin-top:16px">The elevated console still asks for typed confirmation before deleting the VM.</p>', actions: [{ label: 'Cancel', className: 'secondary-action' }, { label: 'Continue to review', className: 'danger-action', onClick: () => toast('Reinstall review opened') }] });
  if (action === 'remove-instance') return dialog({ title: 'Remove agent-vm?', eyebrow: 'Client cleanup', body: '<p>This removes the SSH alias and key, VS Code platform entry, OpenCode server, T3 certificate trust, and per-instance state. Shared project profiles stay intact.</p><p class="dialog-note">A remote instance also offers “Keep the remote VM” before any deletion.</p>', actions: [{ label: 'Cancel', className: 'secondary-action' }, { label: 'Continue', className: 'danger-action', onClick: () => toast('The final review lists every cleanup step') }] });
  if (action === 'new-remote-vm') return simpleForm('Create a Construct VM', 'host.example', `<div class="dialog-field"><label>Name</label><input value="new-agent-vm"></div><div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:12px"><div class="dialog-field"><label>vCPUs</label><input type="number" value="4"></div><div class="dialog-field"><label>RAM GB</label><input type="number" value="8"></div><div class="dialog-field"><label>Disk GB</label><input type="number" value="128"></div></div><p class="dialog-note" style="margin-top:16px">Capacity is checked before creation. Provisioning also configures this PC.</p>`, 'Create and provision');
  if (action === 'register-user') return simpleForm('Register host user', 'Host administration', `<div class="dialog-field"><label>Windows or domain identity</label><input placeholder="EXAMPLE\\teammate"></div><div class="dialog-field"><label>Role</label><select><option>User</option><option>Administrator</option></select></div>`, 'Register');
  if (action === 'edit-allowance') return simpleForm('Edit delegation allowance', 'Blank fields inherit host defaults', `<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>CPU budget</label><input type="number" placeholder="Inherited"></div><div class="dialog-field"><label>RAM budget GB</label><input type="number" placeholder="Inherited"></div><div class="dialog-field"><label>Retained children</label><input type="number" placeholder="Inherited"></div><div class="dialog-field"><label>Maximum lifetime hours</label><input type="number" placeholder="Inherited"></div></div><label style="display:flex;gap:8px;align-items:center;margin-top:16px;font-size:12px"><input type="checkbox"> Allow host-wide sharing</label>`, 'Save allowance');
  if (action === 'pair-phone') return dialog({ title: 'Pair a phone', eyebrow: 'Approval device', body: `<div style="background:white;border-radius:12px;display:grid;grid-template-columns:repeat(9,16px);gap:3px;height:170px;margin:0 auto 18px;padding:12px;width:170px">${Array.from({ length: 81 }, (_, i) => `<i style="background:${((i * 7 + i % 4) % 3) ? '#111' : '#fff'}"></i>`).join('')}</div><p style="text-align:center">Scan with the phone camera. This sample QR is decorative.</p><p class="dialog-note">T3 Code and the approval page use separate origins so a VM-served page cannot read the phone's device token.</p>`, actions: [{ label: 'Done', primary: true }] });
  if (action === 'show-vault-key') return dialog({ title: 'Vault key stays native', eyebrow: 'Second PC setup', body: '<p>In the real app, a Windows native dialog reveals the key once for copying. It never enters this page, its DOM, logs, or the local HTTP API.</p>', actions: [{ label: 'Close', primary: true }] });
  if (action === 'import-vault-key') return simpleForm('Import vault key', 'Second PC setup', '<div class="dialog-field"><label>Vault key</label><textarea rows="3" placeholder="Paste the key from your other PC"></textarea></div><p class="dialog-note" style="margin-top:16px">Existing host entries will be merged per secret. Newer edits win; deletions count as edits.</p>', 'Import and sync');
  if (action === 'host-mode') return simpleForm('Change host vault mode', 'Availability versus host trust', '<div class="dialog-field"><label>Mode</label><select><option>Always available on this host</option><option>Locked to my PC</option></select></div><p class="dialog-note" style="margin-top:16px">Locked mode prevents secret reads while this PC is disconnected. Always-available mode trusts the host administrators with the values.</p>', 'Change mode');
  if (action === 'review-settings') return dialog({ title: 'Apply these changes?', eyebrow: 'Review scope and effect', body: '<p>The staged settings apply to <strong>agent-vm</strong>. Live services change immediately. Resource changes wait for a confirmed restart. Reprovision-only options are listed before anything runs.</p><div class="dialog-note">The implementation would list exact old and new values here. This prototype keeps sample edits local.</div>', actions: [{ label: 'Keep editing', className: 'secondary-action' }, { label: 'Apply changes', primary: true, onClick: () => { state.dirty = false; render(); toast('Settings applied to agent-vm'); } }] });
  if (action === 'discard-settings') { state.dirty = false; render(); return toast('Staged changes discarded', 'info'); }
}

function handleClick(event) {
  const nav = event.target.closest('[data-page]');
  if (nav) return goTo(nav.dataset.page);
  const toastButton = event.target.closest('[data-toast]');
  if (toastButton) return toast(toastButton.dataset.toast);
  const action = event.target.closest('[data-action]');
  if (action) return handleAction(action.dataset.action);
  const vaultTab = event.target.closest('[data-vault-tab]');
  if (vaultTab) { state.vaultTab = vaultTab.dataset.vaultTab; return render(); }
  const hostTab = event.target.closest('[data-host-tab]');
  if (hostTab) { state.hostTab = hostTab.dataset.hostTab; return render(); }
  const period = event.target.closest('[data-period]');
  if (period) { state.usagePeriod = period.dataset.period; return render(); }
  const settingsTab = event.target.closest('[data-settings-tab]');
  if (settingsTab) { state.settingsTab = settingsTab.dataset.settingsTab; return render(); }
  const scope = event.target.closest('[data-settings-scope]');
  if (scope) { state.settingsScope = scope.dataset.settingsScope; state.dirty = false; return render(); }
  const project = event.target.closest('[data-open-project]');
  if (project) return toast(`Opening ${project.dataset.openProject} in a new Remote-SSH window`);
  const editProject = event.target.closest('[data-edit-project]');
  if (editProject) return simpleForm(`Edit ${editProject.dataset.editProject}`, 'Project profile', `<div class="dialog-field"><label>SDKs</label><input value="node 22, dotnet 10"></div><div class="dialog-field"><label>Provision commands</label><textarea rows="4">npm install
if [ ! -e .env ]; then cp .env.example .env; fi</textarea></div><div class="dialog-field"><label>MCP servers</label><textarea rows="3">[{ "name": "context7", "type": "stdio" }]</textarea></div>`, 'Validate and save');
  const approve = event.target.closest('[data-approve-request]');
  if (approve) { document.querySelector(`[data-request-card="${approve.dataset.approveRequest}"]`)?.remove(); state.requests = Math.max(0, state.requests - 1); render(); return toast('Access approved with the requested limits'); }
  const deny = event.target.closest('[data-deny-request]');
  if (deny) { document.querySelector(`[data-request-card="${deny.dataset.denyRequest}"]`)?.remove(); state.requests = Math.max(0, state.requests - 1); render(); return toast('Request denied. No secret was released.', 'info'); }
  const adjust = event.target.closest('[data-adjust-request]');
  if (adjust) return simpleForm('Limit this approval', 'One VM · Selected secrets only', '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>Uses</label><input type="number" value="1"></div><div class="dialog-field"><label>Time limit</label><select><option>10 minutes</option><option>1 hour</option><option>2 hours</option></select></div></div><p class="dialog-note" style="margin-top:16px">Approving a new lease replaces this VM’s existing lease on the same secret.</p>', 'Approve with these limits');
  const copy = event.target.closest('[data-copy-secret]');
  if (copy) return toast(`${copy.dataset.copySecret} copied in a native dialog. Clipboard clears in 30 seconds.`);
  const editSecret = event.target.closest('[data-edit-secret]');
  if (editSecret) return handleAction('edit-secret');
  const revoke = event.target.closest('[data-revoke]');
  if (revoke) return dialog({ title: 'Revoke this access?', eyebrow: 'Lease and cleanup', body: `<p>${revoke.dataset.revoke} will lose access immediately. The Companion will redact agent logs and ask before changing any other file that contains the value.</p>`, actions: [{ label: 'Keep lease', className: 'secondary-action' }, { label: 'Revoke and scrub', className: 'danger-action', onClick: () => toast('Lease revoked. VM cleanup has started.') }] });
  const vmSettings = event.target.closest('[data-vm-settings]');
  if (vmSettings) return simpleForm(`${vmSettings.dataset.vmSettings} settings`, 'Current and pending hardware', `<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px"><div class="dialog-field"><label>CPU count</label><input type="number" value="4"></div><div class="dialog-field"><label>Fixed RAM GB</label><input type="number" value="8"></div><div class="dialog-field"><label>Idle timeout minutes</label><input type="number" value="45"></div><div class="dialog-field"><label>Idle action</label><select><option>Save</option><option>Shut down</option><option>Off</option></select></div></div><p class="dialog-note" style="margin-top:16px">Idle changes apply now. Hardware changes while running stay pending until a confirmed full stop and start.</p>`, 'Apply changed fields');
}

document.addEventListener('click', handleClick);
document.addEventListener('change', (event) => {
  if (event.target.matches('[data-setting]')) {
    state.dirty = true;
    const bar = document.getElementById('save-bar');
    if (bar) { bar.innerHTML = saveBar(); hydrateIcons(bar); }
  }
  if (event.target.matches('[data-project-select]')) toast('Profile selection staged for the next provision', 'info');
  if (event.target.matches('[data-auto-secret]')) toast(event.target.checked ? 'Auto-approve requires one more confirmation in the native dialog' : 'Auto-approve disabled. Future requests need approval.', 'info');
});
document.addEventListener('input', (event) => {
  if (event.target.id === 'project-filter') {
    const query = event.target.value.toLowerCase();
    document.querySelectorAll('.project-row').forEach((row) => { row.hidden = !row.textContent.toLowerCase().includes(query); });
  }
});

document.getElementById('scope-button').addEventListener('click', () => {
  const menu = document.getElementById('scope-menu');
  menu.hidden = !menu.hidden;
  document.getElementById('scope-button').setAttribute('aria-expanded', String(!menu.hidden));
});
document.querySelectorAll('[data-instance]').forEach((button) => button.addEventListener('click', () => {
  document.getElementById('scope-name').textContent = button.dataset.instance;
  document.getElementById('scope-kind').textContent = button.dataset.kind;
  document.getElementById('scope-menu').hidden = true;
  toast(`Context switched to ${button.dataset.instance}. Caches and live connections were retargeted.`);
}));
document.getElementById('add-instance').addEventListener('click', () => { document.getElementById('scope-menu').hidden = true; simpleForm('Add an instance', 'Local or remote', '<div class="dialog-field"><label>How do you want to add it?</label><select><option>Enroll a remote host</option><option>Create a VM on an enrolled host</option><option>Register this Remote-SSH VM</option></select></div><div class="dialog-field"><label>Host service URL</label><input placeholder="https://host.example:7462"></div>', 'Continue'); });
document.getElementById('open-search').addEventListener('click', openSearch);
document.addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); openSearch(); }
});
document.getElementById('open-menu').addEventListener('click', () => document.getElementById('sidebar').classList.add('open'));
document.getElementById('close-menu').addEventListener('click', () => document.getElementById('sidebar').classList.remove('open'));

render();
