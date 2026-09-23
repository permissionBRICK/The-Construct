# The Construct installer: current flow and GUI design proposal

## 1. Setup questions asked today, grouped into wizard pages

Almost all of these are asked up front. The exceptions are called out in section 2.

**Page A: Where the VM runs.** This is only asked on a fresh PC: no VM exists, no named instances, and no `-Action`, `-VmName` or `-InstanceName` was passed.
- Local Hyper-V install (default) or Remote host install. Local relaunches elevated. Remote stays non-admin.

**Page B: Feature set.** Fresh installs only. Existing instances replay their saved choices.
- Minimal (default), Full or Custom.
- Custom asks yes/no for each of 13 components, in this order. Defaults follow Minimal. Items marked "default on" are on by default.
  - Companion (default on)
  - Claude partial streaming (default on)
  - Git credential store (default on)
  - VS Code serve-web (the browser IDE on port 8000)
  - VS Code tunnel
  - SMB share
  - Map share to a drive letter
  - Microphone passthrough
  - Automatic checkpoints
  - OpenCode watcher
  - T3 Code
  - Patched T3 + Desktop
  - T3 HTTPS (default on)
- A summary follows, with no confirmation step. In the GUI this becomes one checklist page with presets.

**Page C: Existing VM.** Only when the VM already exists. Replaces pages A and B.
- Reprovision (default), Reinstall, Redownload, Export config, Add config (asks for a URL or path), Remove instance, Quit.
- Reinstall and Redownload add three more questions:
  - An unsaved-work warning: Abort (default) or Continue.
  - "Save & restore the agent config?"
  - Continue without a backup (default No).

**Page D: Restore.** Only if a backup exists at `extracted\backup-info.json`.
- "Auto-restore the saved config?" Yes (recommended) or No.

**Page E: VM resources.**
- VM RAM in GB. Default is a third of host RAM, clamped to 4–24 GB. It shows the system RAM.
- Disk size in GB. Default 50, minimum 10. The disk grows on demand.

**Page F: Projects.**
- A multi-select of profiles in the config `projects/` folder. All are selected by default. It has an "Open projects config folder" button.
- Git clone credentials for each private host that fails an anonymous check: username (empty skips the host) plus a token. Tokens are verified live.

**Page G: Identity.**
- Agent user password. Default `agent`, which is only a fallback credential.
- Git user name and email. Defaults are the saved value, then the host's git global identity.
- "Store git credentials on the VM?" This carries a plaintext and prompt-injection warning. The default comes from the feature set.

**Remote only.**
- Host service URL.
- Certificate pin confirmation (default No).
- Sign-in fallback: API token, Domain account or Cancel.
- Instance name (lowercase letters, digits, hyphens).
- If the instance already exists: its own action menu.

## 2. Install phases, in order (estimated weights)

| # | Phase | Weight | User-visible wait |
|---|---|---|---|
| 1 | Download and verify the source zip (install.ps1) | 2% | – |
| 2 | Per-user pre-step: VS Code Remote-SSH, panel extension, Companion | 4% | – |
| 3 | Wizard questions | not timed | – |
| 4 | UAC relaunch as admin | – | **UAC prompt**. The window can open behind others. |
| 5 | Enable Hyper-V and platform features | 2% | **Reboot required**. The user must re-run the script afterwards; nothing resumes it. |
| 6 | Resolve the .NET ISO builder | 3% | – |
| 7 | Download Ubuntu Server ISO (about 3 GB) and check SHA256 | 20% | **Network-bound**. Show MB/s. |
| 8 | Build the autoinstall ISO, then delete the source ISO | 5% | – |
| 9 | Create and start the Hyper-V VM | 3% | – |
| 10 | Unattended Ubuntu install until SSH answers | 25% | **About 5 min** ("takes about 5 minutes"; timeout 20 min) |
| 11 | Provision: upload the repo, then the `provision.sh` steps | 30% | Patched T3 build (Full) is much longer. |
| 12 | Host configuration: root key, `~\.ssh`, VS Code, T3 CA | 3% | **Windows certificate dialog**; also the VS Code tunnel sign-in (see below) |
| 13 | Final VM reboot and wait for a new boot ID | 3% | Up to 5 min |
| 14 | Open VS Code (Remote-SSH) | – | – |

The script tells users "about 10 minutes total" once the questions are done.

`provision.sh` already reports each step through `run_step critical|optional "<title>"`, which prints `==> <title>`, and it keeps track of which steps failed. That output can drive sub-progress within phase 11 directly.

The questions are not all asked up front today:
- **The VS Code tunnel device sign-in** happens in the middle of provisioning. It waits on "Press Enter once sign-in is complete".
- **Git clone credentials** can be asked again.

The GUI should show both as inline action cards, not hidden console prompts.

## 3. Update phases

**Update Construct** (`Update-Construct.ps1`) never touches the VM:
1. Fetch the manifest, download the source zip and verify its checksum (40%).
2. Extract, remove stale files, record the source manifest (15%).
3. Reinstall the panel extension (20%).
4. Advance the install marker (5%).
5. Companion swap (20%). This has its own sub-steps:
   - Check the manifest and SHA.
   - Ask the app to quit and wait up to 15 s. It never kills the process.
   - Move files one by one through `.previous`, retrying for 10 s per file.
   - Register, then start `--background`.
   - On failure it rolls back and names the step and the processes holding the file.
6. Write `ok` or `fail` to the result file. Open VS Code windows then reload.

An elevated run skips the Companion step with a warning. Tell the user to run it non-elevated.

**Reprovision** (`Provision-AgentVM.ps1`, or Reprovision from the menu):
1. Pick projects and resolve the git identity.
2. Check SSH reachability and the saved root key.
3. Upload and unpack the repo.
4. Run `provision.sh` (the bulk of the time).
5. Host configuration and T3 Desktop update.
6. Usually no reboot.

Reprovision is fast ("usually only takes a few seconds"). A determinate bar is fine here.

## 4. UX recommendations

**During the install:**
- A left-hand checklist of phases, each marked done, active, pending, skipped or warned. "ISO already present" shows phases 6–8 as skipped.
- One plain-language line for the current step, such as "Ubuntu is installing itself inside the VM. Nothing to do." Put secondary detail under it, such as "1.2 of 3.1 GB · 18 MB/s" or "Provision step 14/31: Installing AI tool: claude".
- An ETA based on phase weights, recalibrated from measured download speed. Show a range ("about 8–12 min left"), not a countdown.
- Rotating "while you wait" cards explaining what The Construct is: the VM is disposable, agents run as root in it, config sync, the Companion tray. Make them match the selected features.
- Use a taskbar progress overlay. Send a Windows toast when the install finishes or needs the user.

**Blocking waits.** A full-width amber banner with one action button covers:
- UAC: "Approve the Windows prompt".
- The certificate dialog.
- The tunnel device code: a copy-code button plus a button that opens github.com/login/device.
- Credentials.

**Reboot for Hyper-V.** A dedicated screen explains why. It offers "Restart now" and "Later". Add a RunOnce entry that relaunches the GUI after the restart and restores the saved answers. That would be new behaviour; today the user has to re-run the script by hand.

**Failures:**
- Stop the bar and turn it red. Show the failed step name and the exception text.
- Offer Retry from the failed phase (every phase must be idempotent), Copy log, Open log folder, Open docs/troubleshooting and Close.
- Distinguish optional-step warnings from critical failures. `provision.sh` already tracks both, so the install can finish "with 2 warnings", each with an expandable entry.
- Show Home-edition and BIOS virtualization guidance as formatted text with links.

**Details log (drop-down):**
- Collapsed by default, with a chevron and a one-line tail preview.
- Once open, show a monospace view that renders ANSI colours, because the VM output streams through SSH.
- Autoscroll while the view is at the bottom. Scrolling up pauses it and shows a "Jump to live" chip.
- Filters: All / Steps (`==>`) / Warnings / Errors. Add a search box.
- Copy selection, Copy all and Save. Mask secrets before they reach the buffer.
- On failure, open the drawer automatically at the failing step.
- Write the log to disk from the start so it survives a crash or reboot.

**Architecture note.** Keep the one-liner. It downloads the source, then launches the GUI (WPF/WinForms, or the Companion executable). The GUI:
- collects the wizard answers;
- runs Auto-Install non-interactively with explicit parameters (`-FeatureSet`, `-VmMemoryGB`, `-VmDiskGB`, `-Projects`, `-GitCloneCredentialsB64`, `-Backend`, and so on);
- parses the `==> ` step lines, or better, a structured `##construct-progress{json}` line the scripts would emit.

The Companion is the primary UI, so hosting this as a Companion window would reuse its styling.

## 5. Two visual concepts

**Concept 1: compact updater (about 460×200, borderless and rounded)**
- Logo and title, one progress bar, a status line and a percentage/ETA.
- A "Details ▾" link expands the window downward to about 460×420 to show the log drawer.
- Closes itself on success after about 3 s. Stays open on failure with Retry and Copy log.
- Suits Update Construct, Reprovision and the Companion swap, where no questions are asked.

**Concept 2: full wizard (about 900×620)**
- A left step rail with two sections:
  - Setup: Location → Features → Resources → Projects → Identity → Review.
  - Install: the phases from section 2.
- The right pane holds the page content. Wizard pages use cards: preset tiles for Minimal/Full/Custom and a RAM slider that shows the host total.
- A Review page lists every answer with Edit links. Nothing is confirmed today, so this is new.
- After "Install", the rail switches to the phase checklist with live states. The main pane shows a large progress ring, the current-step sentence and tip cards.
- The log is a bottom split pane that can be resized or popped out into its own window.
- Blocking actions appear as a sticky banner above the pane.
- The success page shows buttons for Open in VS Code, Open T3 Code and the Companion tray tip.

These are the files I drew on:
- `/root/repos/construct/install.ps1`
- `/root/repos/construct/Auto-Install.ps1` (lines 641–1016 and 2885–3991)
- `/root/repos/construct/lib/AgentVm.FeatureSet.ps1`
- `/root/repos/construct/lib/AgentVm.Common.ps1` (Ensure-HyperV about line 300; Resolve-GitIdentity at 909; the git credential session at 1315)
- `/root/repos/construct/Create-AgentVM.ps1`
- `/root/repos/construct/Provision-AgentVM.ps1`
- `/root/repos/construct/bin/provision.sh` (`run_step` titles)
- `/root/repos/construct/Update-Construct.ps1`
- `/root/repos/construct/docs/companion.md`