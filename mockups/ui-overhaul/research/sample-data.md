# Shared sample data for all mockups

Use this data in every design, so the designs compare like for like. Invent plausible extra detail where needed, but keep these facts.

## Client PC: "PC-1", Windows 11, 64 GB RAM

### Instances (registry)
| name | backend | state | notes |
|---|---|---|---|
| agent-vm | hyperv-local | running, uptime 3h 12m | 8 vCPU, 16 GB RAM (11.2 GB in use), disk 146 GB (88% used, warn). Ubuntu 26.04 LTS. Provisioned at commit c21978b; installed Construct is b5c4348 → **behind host by 2 commits · reprovision**. |
| dev-2 | hyperv-remote on `buildbox.example.local:7462` | **saved** by idle policy 41 min ago (resume on start) | 4 vCPU, 12 GB. Idle policy: save after 60 min. |
| win-test | child VM of agent-vm (Windows 11 eval, lease expires in 5h 20m) | running | 4 GB |

A Construct update is available: installed b5c4348, latest 3a91f0e (4 commits: "Default VM CPU allocation to the host allowance", "Initialize vTPM identity before capturing reusable VM baselines", …).

### Coding agents on agent-vm
| agent | version | update | live activity |
|---|---|---|---|
| Claude Code | 2.4.1 | 2.4.3 available | **working**, repo `construct`, turn started 6 min ago, "Refactoring forwarder retry backoff" |
| Codex | 0.61.0 | up to date | idle, last turn 48 min ago |
| OpenCode | 1.9.2 | up to date | serve running :4096, idle |
| T3 Code | 0.9.14-construct.3 | — | web UI enabled, 2 threads active |

### Forwards (`construct expose`)
- `vm:5173` → `localhost:5173`, client, **open**, label "vite dev server", opened by Claude 12 min ago
- `vm:8080 → 18800` (remapped, 8080 busy on PC), client, **open**, label "api preview"
- `vm:3000`, host forward on buildbox LAN, **queued**

### Notifications inbox (`construct notify`)
- 14:02 info  "Test suite finished — 3 failures" (claude, construct)
- 13:40 error "Deploy failed, rolling back" (codex, gitgudlab)
- 12:15 info  "PR #31 opened" (claude, omniloop)

### Mic passthrough: armed (capture device "Shure MV7"), not currently streaming.

### Projects (profiles)
construct ✔, omniloop ✔, gitgudlab ✔, emili-simulator ☐ (deselected), jarvis ✔ (auto-discovered, "new"). Config sync: last synced 3 min ago; 1 new profile from VM.

### Token usage & cost (agent-vm)
Today $4.12 (claude $3.40, codex $0.58, opencode $0.14), this month $86.30, all time $412.77. Last 7 days: 9.8, 12.4, 6.1, 14.0, 11.3, 8.7, 4.1.

## Shared host: "buildbox" (Hyper-V `constructd` 1.14.2, latest 1.15.0 available)
- 32 cores, 128 GB RAM. Resident 96 GB, committed 142 GB (over-commit 111%), admission line 118 GB. Memory pressure: **elevated**, last pressure save 22 min ago (mara/scratch-3).
- Capacity mode: enforce. Disk D: 3.6 TB, 61% used.
- Health: ok except "inventory incomplete for 1 VM", 1 lease overdue (bob/win-qa, 2h over).
- Active jobs: "create child win11-eval for alice" (running, 62%), "reprovision dev-2" (queued). Failed: "delete child bob/win-qa" (failed 09:14, access denied).

### Users
| user | role | primaries | children | RAM used/allow | vCPU | month cost | flags |
|---|---|---|---|---|---|---|---|
| alice | dev | 2/3 | 1/4 | 28/32 GB | 12/16 | $212.40 | — |
| bob | dev | 1/2 | 2/2 | 20/24 GB | 8/8 | $54.10 | lease overdue |
| dana | admin | 2/∞ | 1/∞ | 28 GB | 12 | $86.30 | — |
| mara | contractor | 1/1 | 1/1 | 16/16 GB | 4/4 | $139.90 | legacy credential |
| jonas | dev | 0/2 | 0/2 | 0/24 GB | 0/8 | $0 | disabled |

### VMs (examples)
alice/work-vm (running, 16 GB, busy), alice/bench (idle 2h, 12 GB), alice/win11-eval (creating), bob/api-vm (running, 12 GB), bob/win-qa (child, lease overdue, 8 GB), dana/dev-2 (saved, 12 GB), dana/agent-vm-2 (running 16 GB), mara/scratch-3 (saved by memory pressure, 16 GB).

### Windows licensing
License key pool: 2 MAK keys (key A: 7/10 activations used; key B: 2/5), 1 retail key. Guests: alice/win11-eval (grace 29 days), bob/win-qa (activated, key A), 1 retained machine identity. One activation failure: "0xC004C008 key has exceeded its unlock limit" 2 days ago.

### ISO catalog: Ubuntu 26.04 LTS server (current), 24.04.3 LTS; Child media: Windows 11 24H2 eval, Windows Server 2025 eval.
### Audit examples: "dana rotated token for mara · 13:22", "alice created child win11-eval · 13:51", "memory-pressure saved mara/scratch-3 · 13:48".
