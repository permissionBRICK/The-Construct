# Final delivery review — ha/s5-final

This closes the Linux implementation review of the integrated host-administration
candidate. It does not establish Windows/Hyper-V readiness. All host-side execution
still requires the [field checklist](../field-test-host-admin.md). The frozen
contract's final-review **Deviations** section records compatibility decisions.

## Round 1 dispositions

| Finding | Resolution and evidence |
| --- | --- |
| B1 / Opus 1 | Added Admin-only ISO catalog endpoint, safe source projection, authorization tests and independent client catalog failure handling. |
| B2 | Child deletion uses the media job cleanup protocol for both partial and completed files and releases the actual path-keyed reservation. Inventory resolves path keys, including orphan rows. Other-VM references retain media and its charge. Upload/ready/shared-reference regressions cover this. |
| B3 | Scheduled task XML replaces `/TR`; recording tests cover resume arguments beyond 261 characters and exclude the health credential. Actual Task Scheduler execution remains a field check. |
| B4 | SQLite reconciliation identifies interrupted or launch-failed, never-started `child-create` jobs (null phase). Complete VM/disk/partial-media absence evidence releases the abandoned VM row and reservations with `vm.create.abandoned`; uncertain evidence fences deletion and holds liabilities. Six evidence variants cover recovery. |
| B5 | Production manifest key, repository and required signature are pinned by host-local options; stale API database settings cannot override them. Remote Admin edits to those trust fields are refused. This deliberately tightens §11.3; see Deviations. |
| B6 | Primary mutations wait for the background `capacity-reconcile` VM gate; real operation conflicts still return 409. Power/delete API regressions cover the wait. |
| B7 | Observe admissions use background inventory; missing primary placement records unknown-volume liabilities without preventing the legacy operation. Primary start returns its observed state. The broad same-owner retry claim was already covered for equal disk sizes; the fix extends Observe retries to smaller requests while preserving the larger disk liability, in SQLite and memory. Foreign disk ownership remains a conflict. |
| B8 | The first ownership marker is fully written/flushed before exclusive atomic publication. A host-default lookup failure leaves no empty marker. Cmdlet-double regression covers that window. |
| B9 | Null-id recovery accepts `New-VM -Path` itself or its name subdirectory as the reported VM path. The double models the nested form; actual Windows layout remains a field check. |
| B10 | PowerShell emits only allowlisted constant child error codes; the driver preserves them as structured validation failures. Unknown/dependency messages remain redacted. |
| B11 / Opus 2 | All upgrade/recovery hints name `Provision-AgentVM.ps1 -InstanceName <primary> -RotateVmToken`. The alleged pre-SSH rotation is not present: `Ensure-VmReachable`, `Enter-RootKeyFastPath`/`Test-KeyAuth`, and guest preparation precede `RotateVmCredentialIfRequested`. Delivery failure needs a new explicit rotation; ordinary reprovision does not redeliver it. The menu entry remains absent. |
| B12 | User registration rejects the reserved `vm:` credential namespace, case-insensitively, with audited validation tests. |
| B13 | Synchronous starts register their operation, settle failure keys, and release runtime/save liabilities on observed Off. Failed-primary and failed-child regressions cover it. |
| B14 | CertPath-only production hosts refuse apply before drain with `update-health-pin-required`; a configured certificate-store thumbprint is required. |
| B15 | Settled recovery outcomes stop repeated file hashing; a failed initial handoff read alone cannot create artificial maintenance or suppress bootstrap. Recovery regressions cover both. |
| B16 | Added PowerShell authorized-rollback/commitOnly cases and cross-user last-RAM SQLite API race. Fixed capacity-refusal audit rollback found by the race. Health-result control flow is tested; `Test-UpdateHealth` transport is fully mocked. Raw exception-message persistence was declined because filesystem/cmdlet exceptions can contain secrets; allowlisted diagnostic codes give safe specificity. |
| B17 | Recorded remaining primary API compatibility differences, cached inventory behavior and protocol framing below/in remote-host docs. The claimed ungated child polling is not present: `queryChildren` first calls TTL-cached `queryFeatures` and refuses without the child feature before issuing the children request. |
| B18 | Startup rejects a newer breaking migration before legacy schema writes; newer additive schemas remain readable. Tests cover both. Existing replay-safe migration DDL and previously documented M400/nullable cascade representation are unchanged. |
| Opus 3 | Shared-forward requester filtering uses canonical principal conversion and case-insensitive ownership comparison. Regression covers mixed-case requester visibility. |
| Opus 4 | Removed the dead optional-service guard from the required shared-exposure revocation path. |

## Nonblocking review items

| Finding | Final disposition / remaining limit |
| --- | --- |
| N1 | Added transition/documentation/special-use address rejection tests. The downloader does not enumerate the host's own public interface addresses; network policy must restrict any such address. |
| N2 | `DedicatedTo` may name a future VM because CLI uploads precede creation. It consumes the uploader's media budget and keeps media from automatic cleanup; it does not reserve the VM name. Explicit abort/delete remains available. |
| N3 | A chunk is buffered in memory (default 8 MiB, maximum configured 64 MiB). Concurrent uploads can multiply that memory; no aggregate buffer budget is implemented. |
| N4 | Storage safety rejects all reparse ancestors, including legitimate junction/mount layouts. Use ordinary directories for this candidate. |
| N5 | Graceful shutdown is advertised as conditional when the WMI component exists; disabled/missing guest integration can still return unavailable or timeout. Validate each guest on Hyper-V. |
| N6 | Restart of Saved/Absent no longer reports the unrelated `lease-due` code; it reports `vm-state-unknown`. |
| N7 | Console transport honors `Constructd:PowerShellPath`; recording regression covers a custom path. |
| N8 | Console/forward caps are per target VM; authorized shared consumers contend for those caps and the owner's resource allowance. No per-consumer fairness is implemented. |
| N9 | Handoff files inherit the protected data-root ACL; no explicit per-file SYSTEM-only DACL is installed. The health credential is accepted only in the loopback maintenance window, so reopening the gate ends its authority even while the file exists. Real ACL verification remains mandatory. |
| N10 | Unenrolled Negotiate identities receive reduced health output. Full output requires an enrolled user, VM credential or restricted update-handoff identity. |
| N11 | Added anonymous coverage for every protected mapped route and explicit legacy-token delegated-route denials. |
| N12 | Revocation clears the hash while preserving the recorded credential kind, atomically in both stores. Persistence regression covers it. |
| N13 | Local packaging does not prove content matches the claimed commit; `minInstalledCommitDate` is not enforced; preserved-name conflicts fail at apply rather than stage; a drain race can surface as a failed operation. Use the guarded release workflow. See host-release docs; these remain follow-up work. |
| N14 | Capacity-refusal audits survive declined admission transactions. Successful primary create releases runtime holds when actually stopped. Primary starts use the primary placement resolver. |
| N15 | Observe mode avoids synchronous inventory in admissions. Enforce mode can still refresh inventory while holding the ledger gate, including generic mutation scopes; a slow provider delays concurrent mutations. No performance claim is made. |
| N16 | Delete confirmation says sharing is unknown when the failed-job projection has no sharing evidence, instead of calling the VM private. |
| N17 | Generic guest system instructions still mention `construct vm` on local/legacy guests. The CLI reports that delegation is unavailable there; the command advertisement is not capability-specific. |
| N18 | Public PowerShell hardware/media/shutdown/capabilities entry points validate names before invoking wildcard-aware cmdlets. Four double checks cover this. |
| N19 | Remote e2e chooses an ephemeral port by default and fails if startup fails. It no longer treats an occupied default port as a passing skip. |

Previously recorded delivery gaps remain: no credential-upgrade menu, no Auto-Install
cascade confirmation, no child host forwards or packet isolation, unsupported disk
growth, unverified guest addresses, and no supplied production signing key. First
rollout is manual. No SCM, LocalSystem, real TLS health pin, Windows PowerShell 5.1,
real guest installation, update/rollback, or active-VM continuity was run on a host.

## Validation

Baseline before fixes: build 0 warnings/0 errors; 1183 .NET tests; 24 Node suites
(5152 checks), 22 PowerShell suites (3318 checks/groups), 22 Bash suites (1141 checks),
and browser smoke (311 checks). The one host-admin .NET scenario invoked by its Bash
wrapper is already included in the .NET count. Expected root/DPAPI platform skips
remain.

Round-2 full matrix: **all 70 suite invocations passed**, plus the explicit solution build with
**0 warnings / 0 errors**. .NET **1227 passed, 0 failed, 0 skipped**; Node **24 suites,
5156 checks**; PowerShell **22 suites, 3330 checks/groups**; Bash **22 suites, 1141
checks**; browser smoke **311 checks**. The two expected root/DPAPI skips remain.
Remote e2e passed **45 checks with no skips**, host-admin e2e passed **70 named checks**
plus log/persistence assertions, and the updater fake suite passed **56 assertions**.
Logs are under `/tmp/ha-s5-final-rerun` on the disposable development VM. No Windows
host was used. That .NET count is 44 above the integrated 1183-test baseline.


## Round 2 follow-up

Both reviewers independently passed the full 1227-test .NET and script/browser matrix.
The second reviewer approved; the first requested two recovery follow-ons and correction
of current primary-start wording. The rest of round 1 was accepted, including the
recorded nonblocking limits.

- Unknown primary placement now resolves under the existing VM/incarnation/generation,
  live-operation and captured-reservation fences. A readable uniquely identified boot
  disk replaces the placeholder and any duplicate proven hold with the maximum liability;
  the saved-state hold gains the observed config volume. Ambiguous identity/placement
  remains conservative. Tests create through the actual Observe API with failed placement,
  recover inventory, repeat reconciliation, restart the service and admit in Enforce;
  separate cases retain liability for stale or ambiguous identity evidence.
- Null-phase `child-create` jobs marked by `MarkStartFailedAsync` use the same evidence
  recovery as restart interruptions. Tests atomically admit rows/references/reservations,
  mark launch failure, then verify absence cleanup/audit or Deleting/held retention.
- Current README and field-checklist wording now requires primary start to return the
  observed state promptly, rather than wait 30 seconds for Running.

Two additional nonblocking notes remain recorded: the background-gate fallback can wait
for a later long operation if that operation wins the gate race (request cancellation
still applies), and optional ISO catalog `lastBuild` is omitted because no persisted
build-history projection is implemented.

Round-3 validation after these C#/documentation follow-ons: build **0 warnings / 0
errors**, focused recovery suite **37 passed**, full .NET suite **1235 passed, 0 failed,
0 skipped** (52 above the integrated baseline). Logs: `round3-build.log`,
`round3-regressions.log`, `round3-dotnet.log` in `/tmp/ha-s5-final-rerun`.
The unchanged Node/PowerShell/Bash/browser sources retain the round-2 full-matrix
results above; both reviewers independently reproduced that matrix. No Windows or
Hyper-V execution occurred.
