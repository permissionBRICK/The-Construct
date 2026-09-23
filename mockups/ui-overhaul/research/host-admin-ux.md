# Host admin panel: UX research from the admin and team lead's point of view

One correction to the brief first: the panel isn't one long page. It already has 8 tabs: Overview, VMs, Users, Usage, Media, Operations, Configuration and Maintenance (`TABS` in `extension/src/hostadmin.js:49`). The Companion uses the same webview. The problem is that each tab is a long stack of sections, and the tabs can't link to each other. Nothing in the repo was edited.

## 1. Top 8 admin jobs, ranked by frequency and urgency

| # | Job | What the admin needs |
|---|---|---|
| 1 | **"Why is the host out of RAM / why was my VM saved?"** | Resident vs committed vs admission line, swap, memory-pressure state and last save. Also the RAM hogs by owner, which are idle, and which carry the "saved (memory pressure)" badge. |
| 2 | **"Something's stuck or failed"** | Failed or long-running jobs, overdue leases, inventory incomplete, health not ok, Windows activation failures. Each needs one-click retry, cancel or open. |
| 3 | **"Who is burning tokens or money?"** | Cost by user, then by VM, then by tool, compared with last period. |
| 4 | **"Can Alice have another VM / more RAM?"** | Her effective allowance vs what she uses (primaries, children, vCPU, RAM, storage), host headroom, per-VM overrides. |
| 5 | **Onboard or offboard a developer** | Register the user, role, quota, then issue the token (shown once). For offboarding: disable, see their VMs, children and shared children, then do the cascade delete. |
| 6 | **Update the service safely** | Installed vs latest, jobs that block the update, a warning that "VMs keep running", phase progress, and resolve/rollback when it goes wrong. |
| 7 | **Windows guests and licences** | Key pool budget (MAK attempts), which VM holds which key, activation or grace state, evaluation expiry. |
| 8 | **Change host policy** | Idle defaults and caps, memory-pressure thresholds, capacity observe/enforce, user defaults and caps. Each change needs a preview of its effect. |

## 2. Pain points in the current panel

- **Overview gives counts you can't click.** "Leases overdue" and "Unmanaged VMs" are bare numbers. Health is one joined string. Memory pressure is a single text line under the RAM bar. Active jobs don't link to their VM or owner.
- **VM rows are overloaded.** Each row has 5 columns, and "live usage" stacks CPU %, "demand / assigned", disk, sample time and tokens as text. A row can carry up to **8 buttons**: VM settings, Restart/Start, Change lifetime, Make public, Shut down, Delete, Overrides, Rotate token. Reservations only appear in a hover `title`. The table is sorted by name only, with no filter by owner, state or idle.
- **Detail editors open below long tables.** The Overrides card sits at the bottom of the VMs tab, and the user edit card at the bottom of Users, so you scroll away from the row you clicked.
- **The user editor has three separate saves**: Save user, Save allowance, Issue token. Allowances are free text ("12h / 3d", `"true"/"false"`). "Effective allowance" is one long sentence in a single cell.
- **Cost lives only in Usage.** Users shows tokens only ("tokens this month"), and the per-tool split is hover-only. The period picker offers today, month or all time, so there is no trend and no comparison.
- **Windows licensing is hard to find.** It's nested inside the "Child media" section of the Media tab. Key rows and guest rows are one `·`-joined sentence each, with codes like `reason 0x…`. Retained machines are appended to the guest list.
- **Configuration is 10 raw JSON textareas** ("a section is replaced whole"). The memory-pressure thresholds are edited there, while their live state shows on Overview.
- **Maintenance hides the controls.** Check / Stage / Apply / Resume / Cancel and the three Resolve buttons sit inside a collapsed `<details>`. The `blockingJobs` data exists but isn't shown before Apply.
- **Nothing links to anything.** A VM can't jump to its owner, jobs, audit entries, usage or licence. There's no global search.

## 3. Three layout concepts

### A. "Watchtower": ops console built around what needs attention
- **Order of information:** problems first, then capacity, then everything else.
- **First screen:** a strip across the top with health, version, capacity mode and RAM pressure. Below it, an **attention feed** of typed cards: failed job, overdue lease, pressure save, activation failed, update available, inventory incomplete. Each card has its fix inline (Retry, Shut down, Activate again, Update).
- **Navigation:** a left sidebar with Attention, Machines, People, Spend, Licences, Policy, Service and Log. Every entity opens in a **right-side drawer**, so you never lose your place in the list.
- **Signature feature:** a feed card turns into a resolved audit entry once fixed ("Retried child-delete · you · 14:02"). The feed is also your work log.

### B. "Floorplan": map of host capacity
- **Order of information:** physical RAM is the canvas, and VMs are tenants on it.
- **First screen:** a **treemap of committed RAM**. Area is assigned RAM, grouped by owner, then primary, then children. Colour is state (running, idle, saved, off, overdue), with a hatch on saved-by-pressure VMs. The admission line and host/unmanaged RAM are drawn as reserved blocks.
- **Navigation:** click a block to zoom into the owner, then the primary. The inspector shows usage, lease, guest commit and actions. Tabs for Media, Service and Policy are secondary.
- **Signature feature:** a **"what-if" slider**. Drag it to "save all VMs idle > 30 min" and the treemap reflows, showing RAM freed and who is affected. Previewing a policy change before you make it covers job #1 and job #8.

### C. "Roster": people first, for team leads
- **Order of information:** a person, then their allowance, then their VMs, spend and tokens.
- **First screen:** one card per developer. Each card shows allowance-vs-used bars (primaries, children, vCPU, RAM, storage), this month's cost with a sparkline, their VMs as chips coloured by state, and flags (disabled, legacy credential, no host forwards).
- **Navigation:** open a card into one page per person, with a **single form and one save** for role, quota, allowance, per-VM overrides and tokens. Host-wide views sit behind a "Host" toggle.
- **Signature feature:** an **Onboard / Offboard wizard**. Onboarding is register, then allowance from a template ("contractor", "senior"), then the token shown once with Copy. Offboarding is disable, then a cascade preview including shared children and Windows keys to release, then a typed confirm.

A hybrid is likely best: Watchtower's sidebar and drawers as the frame, Floorplan as the Machines view, Roster as the People view.

## 4. Charts that would help, and charts that would be noise

**Worth building:**
- **RAM stacked bar.** VMs resident, host, free, the admission-line tick, and a separate "committed X% (over-commit)" marker. This already exists; make it bigger and drillable.
- **Allowance bullet bars per user** (used vs budget vs host default). This answers job #4 at a glance.
- **Cost per user over time**, as a 30-day sparkline plus the change vs last month. This is a data gap: the API only returns today, month or all time, so the service would need daily buckets.
- **Idle-VM list sorted by time until the idle action**, showing last busy heartbeat and reason. These are exactly the VMs the memory-pressure policy would save next.
- **Lease timeline** for children (bars ending at expiry, overdue in red).
- **MAK budget meter** per key (attempts used vs budget).

**Would be noise:**
- Per-VM CPU % sparklines: samples go stale after 30 s, and the idle policy ignores CPU on purpose.
- Gauges or donuts for disk on a single volume.
- Pie charts of cost by tool: a sorted table is clearer.
- Charts of audit or job volume.
- Any visualisation of the backend-capabilities table (it's reference data, so it belongs as a collapsible list under Service).
- Real-time animated meters: they would hide the "stale / last sampled" honesty the current panel gets right.

**Key files:** `/root/repos/construct/extension/media/hostadmin.html`, `/root/repos/construct/extension/media/hostadmin.js` (the render functions), `/root/repos/construct/extension/src/hostadmin.js` (tabs and view models), `/root/repos/construct/companion/src/Construct.Companion.Core/HostAdmin/HostAdminViews*.cs`, `/root/repos/construct/docs/remote-host.md` §6 and §8.