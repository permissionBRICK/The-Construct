# Jarvis backup repairs — 2026-09-05

Scope: P1 from `/tmp/jarvis-bug-triage/construct/report.json`, based on Construct
`5f1247ff649b258bbd1b6770ae9fe89bfee9a152`. Work performed in the isolated
`fix/jarvis-backup-20260905` worktree. P2–P5 are separate work packages and were
not repaired or revalidated here.

## Per-task outcomes

- `construct-export-glab-config.md`: repaired. `bin/export-config.sh` now captures
  the complete `.config/glab-cli` directory only with `INCLUDE_AUTH=true`.
  `docs/backup-restore.md` describes inclusion and restore behavior. Existing
  unconditional `.config/gh` behavior is retained and explicitly documented.
  The generic home overlay restores glab files; no restore implementation change
  was needed.
- `construct-large-backup-restore.md`: existing fixes retained and regression
  fixtures passed. Streamed tarball overlay, saved Claude skill symlink versus
  fresh directory, recovery of both paused services after injected failure, and
  explicit error diagnostics remain covered. The affected machine's 605 MiB
  reinstall was not repeated: fixture success does not complete that field check.
  Retry its retained archive in an external maintenance context and capture the
  reported restore revision and diagnostic if it still fails.

## Tested result

- `bash test/export-config.test.sh`: **12/12 passed**. Real exporter archives
  contain glab credentials and additional config with auth on; both archive and
  manifest omit glab with auth off. Both settings retain GitHub CLI credentials.
  The real tarball restore reproduces dummy glab credentials/settings byte for
  byte, preserves credential mode `600`, and restores no glab directory from the
  auth-off archive. Before the fix, the fixture failed all six glab inclusion and
  positive restore checks (6/12 passed), demonstrating regression sensitivity.
- `bash test/restore-config.test.sh`: **25/25 passed**.
- `bash -n bin/export-config.sh bin/restore-config.sh test/export-config.test.sh
  test/restore-config.test.sh` and `git diff --check`: passed.

The new fixture uses temporary homes, dummy credentials, an empty repository and
profile store, isolated external-secret paths, a clean child environment, and a
stubbed `systemctl`. Existing restore fixtures also stub service operations and
installer execution. No production services were restarted and no live GitLab
authentication was attempted.

## Activation

After this commit is integrated through the normal Construct update flow, ensure
the exporting VM uses the updated `bin/export-config.sh`, then create a fresh
auth-enabled backup before reinstalling. Existing archives cannot acquire glab
credentials retroactively. The glab binary still needs its existing project
provisioning installation. This work does not require a service restart and does
not activate the change in the production checkout; no push or merge is part of
this work package.
