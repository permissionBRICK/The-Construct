# Host update UX and STANDPC hang — 2026-09-10

The admin panel now has one **Update host** button and an always-visible release
status. Status refreshes every minute (five seconds while updating); release
metadata checks are limited to once per fifteen minutes per open panel. Clicking
Update checks again, stages the selected release, and applies it after verification.
The old stage/apply and recovery controls remain under collapsed update details.
Recovery actions remain accessible while the service is in maintenance.

New hosts accept `autoApply` on the existing stage endpoint and advertise
`supportsAutoApply` in update status. Intent is persisted before the staged state;
the recovery worker submits apply without depending on the browser or VS Code.
Older hosts use the same stage/apply endpoints with idempotency keys. The extension
persists the requested update per host in global state, polls while its panel is
hidden, and resumes when the panel is reopened after closure/reload. It never
automatically applies an unrelated package staged by another action.

Storage usage percentage was separately changed in `680dad5`: physical used bytes
plus OS headroom, excluding potential disk growth. Admission accounting is unchanged.

## Live Windows diagnosis

STANDPC's active service remains `cf5c77f9226492916b834850309145a9f68335a2`, at
`C:\Construct\service\publish\Constructd.Api.exe`; data is under
`C:\ProgramData\Construct\service`. Update `1162fb94271342fb8c4b8c146cf13130`
downloaded `14e7bff4f8793a1eeb3c8406feda7101270c8b59`, reached handoff at
22:45:14 UTC, and was fenced closed at 22:45:45. It never wrote its first updater
progress entry or began replacement. The scheduled task still appeared running;
the API reported `interrupted`, while the old panel retained `draining`.

Windows Wait Chain Traversal showed multiple PowerShell threads waiting on the
same RPC endpoint owned by `MsMpEng.exe` (PID 11744 during diagnosis). Defender's
Operational event **5008 at 20:10:09 UTC** explicitly reported a hang, platform
`4.18.26080.3`, engine code `16508`. A PowerShell command containing only
`[Console]::WriteLine('ready')` timed out. The normal supported
`MpCmdRun.exe -SignatureUpdate -MMPC` command also timed out after 120 seconds.
No exclusions, protection changes, process-killing bypasses, or reboot were applied.

RDP authenticated at 22:46:30 UTC (RemoteConnectionManager 1149), but Christoph
reported login hanging. A shared Defender problem is plausible, not proven by an
RDP wait chain. Windows reported approximately 6.6 GiB physical RAM free, 2.35 GiB
commit free, and 3 GiB free on C:. Do not equate this event with the earlier
September 5–6 memory exhaustion without additional evidence.

The Construct recovery bug is independent: after a failed launch in the *same*
service process, recovery reran `Bootstrap`, including PowerShell-backed forward
reconciliation and marking live jobs interrupted. It now performs startup work
only when this process actually started under an update maintenance gate. A
missing updater startup now records `updater-did-not-start`; draining status
reports live blocking operations.

## Pending activation and checks

Host deployment is blocked by Windows' Defender hang. A reboot needs an explicit
maintenance window because it stops haus-vm and this coding session. After recovery,
verify PowerShell and RDP first, inspect the fenced update, then retry/apply or
cancel that pre-replacement attempt and install the new release normally. Do not
delete the handoff/fence/rollback evidence or blindly restart the updater task.

The company viewer at `http://localhost:6080/` reportedly stayed at “Connecting to
guest…”. Its browser WebSocket uses that same origin and port. The Linux gateway
separately connects to the configured Windows host on VMConnect port 2179. No live
WS009 access was available to establish its actual failure. The gateway now exposes
cookie-authenticated connection stages and credential-free errors; the browser
keeps errors visible and has a 70-second connection deadline. Deploy/reprovision
the company gateway and retry to identify the failing stage; this is not yet a
confirmed repair of its underlying connection problem.

Validation: 50 update/recovery service tests; 293 host-admin model and 95 adapter
checks; 13 update/conversion Node tests; 11 Python gateway/CLI tests. Chromium
smoke checks exercised the update button, badge, collapsed details, live blockers,
maintenance recovery controls, same-port viewer WebSocket, and retained timeout.
