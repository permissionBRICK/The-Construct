# Remote reinstall seed-user lookup failure

Captured 2026-09-06 on haus-vm from the user's failed VS Code remote reinstall
and read-only SSH reproduction through main-pc's Jarvis Link relay.

The reinstall exports the existing VM before deletion. On the saved-root-key
path, Windows PowerShell 5.1 strips embedded double quotes from the seed-user
query passed to ssh.exe. The query exits 2, leaving the default `agent` user.
Pre-built remote media uses `construct`, so the subsequent repository unpack
fails at `chown -R agent:agent /opt/construct`. The installer reports a failed
config save and offers cancellation or a blank reinstall. No backup was made
by that attempt; VM deletion had not begun.

Encode the seed query as UTF-8 base64 and decode it into bash on the guest.
This preserves the original lookup and fallback behavior without double quotes
on the native command line. Other SSH commands are unchanged.

Validation: the original command failed with exit 2 from main-pc's Windows
PowerShell 5.1; the encoded command returned `construct` against the same guest.
The patched installed Windows script also returned `construct`. The new
`test/provision-seed-user.test.ps1` checks transport and six Linux account/config
fixtures; existing instance-identity and remote-install suites passed 242 and
206 checks respectively. Full export/reinstall was not run: export stops the
active T3 service briefly, and reinstall destroys the VM hosting this session.

The main-pc installer was patched in place, with its prior file retained as
`Provision-AgentVM.ps1.before-seed-quote-fix`. Start a new installer process to
pick up the change; a process already waiting at the failed-save prompt cannot
retry that export by choosing the blank-reinstall option.

## Adopted local VM reprovision (2026-09-10)

After host conversion, the remote backend defaults to seed account `construct`,
although the adopted local guest may still use `agent`. The lookup found the guest's
account but then called undefined `Write-Note` before assigning it. Its catch kept
the wrong default, and repository unpacking proceeded to `chown construct:construct`.
Use the existing `Write-Ok` helper and apply the detected account first. Missing,
invalid or unreadable seed detection now stops before replacing the guest repo.
The regression test executes the whole production root-key branch, not just the
query assignments; it fails against the old code and passes with this fix, alongside
the existing six shell fixtures. No live full reprovision was run from the agent.
