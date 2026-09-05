# T3 prebuilt pair publishing (2026-09-05)

The publisher is https://github.com/permissionBRICK/construct-t3-builds. It owns
its Action and release assets; Construct itself still follows main and does not
publish these binaries in its release tab. Both repositories are public. No
cross-repository secret or dispatch credential was added. The workflow polls
npm stable and Construct main at :17/:47, and also supports manual dispatch.

Stable patched provisioning (`T3CODE_LIMIT_RESUME=true`) defaults to downloading
the last validated pair. `-T3CodeBuildSource local` / `T3CODE_BUILD_SOURCE=local`
selects local compilation; omitted values keep the VM preference. Nightly still
builds locally. A source build can pin `T3CODE_SOURCE_VERSION` and independently
select `T3CODE_INVENTORY`. The publisher tries the complete release inventory,
then the complete nightly inventory, against the exact stable tag. It never
mixes inventory operations or publishes a failed build.

The exact hash and manifest contract is in the publisher README and
`patches/README.md`. Neither Construct's commit nor the provisioning driver is a
binary cache key. The selected patch inventory, transformer, runtime patchers,
and artifact-producing recipe are. Publisher packaging/runtime inputs also
invalidate binaries. Version is part of the service restart/build key.

## Verification evidence

- First GitHub build and publication:
  https://github.com/permissionBRICK/construct-t3-builds/actions/runs/33972118571
- Repeat run skipped both build and publish:
  https://github.com/permissionBRICK/construct-t3-builds/actions/runs/33972478823
- First release: T3 0.0.38, identity
  `a27a5d088190b8da03fb293eab119aab76a5754e76edcf0528abaeecce2d77fb`.
  Windows installer 176,169,629 bytes; Linux runtime 90,741,368 bytes.
- Downloaded the published Linux asset and tested an extracted runtime through
  a symlink: CLI starts, native PTY works, native FileFinder loads, HTTP serves
  the built web UI. The real prebuilt installer downloaded it once; a second
  invocation skipped download/extraction. Isolated launcher/cache/status paths
  were used throughout; the session's live T3 service was not restarted.
- Windows handoff tests execute the provisioner's real handoff block with
  simulated SSH/download/NSIS boundaries. Two VM instances use one host record;
  second VM skips packaging, download, and installation. New hashes, missing app,
  corrupt bytes, invalid URLs, packaging failures, and installer failures covered.
  CI validates the real Windows PE/NSIS archive, but no Windows UI install was run.
- Publisher tests cover unchanged identity reuse, independent version/patch
  changes, irrelevant-file exclusion, whole-nightly fallback, incompatible
  inventories, incomplete releases, and failed upload/verification staying draft.

## Incremental-build experiment

Before choosing prebuilt publishing, three successive pristine upstream stable
releases were benchmarked on this VM. Both cases reused the pnpm package store.
The second case retained node_modules plus correctly configured task caches.
Pristine upstream was necessary because the current patch inventory deliberately
does not support historical 0.0.36. Production received no JS task-cache changes.

| Release | Fresh checkout total | Persistent cache total | Build-only fresh / cached |
| --- | ---: | ---: | ---: |
| 0.0.36 | 62.626 s | 62.571 s | 46.922 / 46.990 s |
| 0.0.37 | 61.124 s | 59.629 s | 45.659 / 46.673 s |
| 0.0.38 | 62.420 s | 58.008 s | 46.684 / 47.396 s |

Cross-release task hits: 0/4 for every release. Same-release control: 4/4,
1.061 s. Output comparison: all 817/817/815 non-sourcemap files identical.
Retained node_modules saved about 2.5–5.1 s on dependency installation, with no
build-phase gain. Cargo compiler targets and package/Electron downloads remain
cached because those are reusable. The detailed disposable benchmark report was
`/var/tmp/t3-release-benchmark.gH37yN/REPORT.md`.

## Voice input in pending questions

The 2026-09-05 follow-up fixes dictation into an Ask User Question custom answer.
Voice input used the normal chat draft for its edit guard and the shared prompt
ref for incoming transcript checks, although the editor displays the separate
pending answer. Both inventories now guard the displayed value and bind a session
to the thread/request/question identity. Transcript insertion reads the current
editor snapshot and synchronizes the replacement ref before writing. Changing
questions or manually editing cancels the session; late results cannot write to
the wrong question. No changes to audio capture or speech recognition were needed.

`test/t3-voice-question.browser.test.mjs` runs the inventory's actual voice callbacks
and the upstream replacement callback in React/Chromium, with synthetic streamed
transcripts at the transport boundary. It covers cumulative custom-answer text,
a stale shared ref, manual edits, switching questions with identical answer text,
and ordinary chat. It failed against `fe6d3a2` and passed for the release inventory
on 0.0.38 and nightly inventory on 0.0.39-nightly.20260905.1287. Both inventories
also applied successfully to their respective clean tags.

Run with `T3_TEST_SOURCE` pointing at the matching upstream checkout (with React
installed) and `T3_TEST_TOOLS` pointing at a package directory containing `esbuild`
and `playwright`. `T3_TEST_CHANNEL=nightly` selects the nightly inventory;
`T3_TEST_CHROMIUM` optionally selects an installed Chromium executable.
`T3_TEST_BASELINE=fe6d3a2` runs the old inventory for regression reproduction.
