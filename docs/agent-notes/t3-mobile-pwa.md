# Minimal mobile PWA experiment

Prepared on `experiment/t3-mobile-pwa`, based on Construct `25d480c`. Not
deployed. The existing main worktree has unrelated work and was left untouched.

## Change

Both current upstream tags (`v0.0.38` and
`v0.0.39-nightly.20260905.1288`) already link a manifest with `id`, `scope`,
`start_url` and `display: standalone`. They also include an Apple touch icon,
safe-area styling and `interactive-widget=resizes-content` in the viewport.

The experiment adds two narrow transforms to that manifest: an app name and
an SVG icon entry with `sizes: any`. The icon overlay is an unchanged copy of
upstream stable's `assets/prod/logo.svg` (upstream MIT license). Existing raster
icons and the Apple touch icon remain available. The inventories are maintained
separately, with identical overlays. Only six lines are inserted into upstream's
manifest. There are no React, CSS or build-system changes.

A second pair of narrow transforms imports `node:os`'s hostname function and
adds a branch to the existing static-file handler for `/manifest.webmanifest`.
The handler reads the original manifest and sets `name` to `T3 Code (<VM>)` and
`short_name` to `T3 <VM>` at request time. `<VM>` is the trimmed
`CONSTRUCT_INSTANCE_NAME`, falling back to the guest hostname. Construct's systemd
service already loads this setting from `/etc/construct/config.env`; local
installs without it use the hostname. This keeps shared prebuilt assets identical
across VMs. JSON serialization escapes names safely, and `Cache-Control: no-cache`
ensures revalidation rather than serving an old cached label.

The existing origin-relative `id`, `scope` and `start_url` are preserved. Separate
VM HTTPS origins give separate installations; a VM-name change does not change
app identity. Installed labels are managed by the browser/OS and may require a
later refresh, approval, or reinstall to reflect a rename. Neither install-time
metadata nor the home-screen label follows whichever remote thread is selected:
it identifies the VM serving the website.

`standalone` removes browser toolbars while retaining normal OS status/navigation
controls. A custom in-page Install button would need additional UI/event code;
this experiment uses the browser's installation UI. No service worker or offline
cache is needed for this scope.

## Verification

- Applied the PWA-only transforms and overlay to pristine files from each tag.
  Reapplying reports zero changes; all manifest and server hooks are already present.
- Used real, headed Chromium 153.0.8010.12 with a persistent, non-incognito profile
  against an isolated localhost page serving each manifest and its icon assets.
  `Page.getAppManifest` reports no parse errors; `Page.getInstallabilityErrors`
  reports no errors for either patched channel; `Page.getManifestIcons` returns
  a decoded icon. The stable baseline reports `manifest-missing-name-or-short-name`.
  The test page isolates metadata; it does not run T3's application.
- `apps/server/src/constructPwa.test.ts` exercises the actual static route with
  different VM names, whitespace/absent settings, quote escaping, conditional
  requests, preserved manifest fields and files, and an unchanged HTML response.
  It passes against stable's handler and nightly's handler plus its `MediaFile.ts`
  dependency using the cached stable toolchain. This is a focused route check,
  not a full nightly application build. Run after applying the inventory from
  `apps/server`: `../../node_modules/.bin/vitest run src/constructPwa.test.ts`.
- Existing source-transform suite: 10/10 passed. `git diff --check` passed.
- An isolated full server typecheck encountered workspace dependency resolution
  and type errors with borrowed cache dependencies; it is not a clean application
  typecheck.
- No full application build, live deployment, or real-device installation was
  performed. Desktop Chromium validation does not establish Android/iOS keyboard
  behavior or guarantee that a browser will immediately show an install prompt.

## Phone trial after deployment

Open the normal trusted HTTPS T3 address. On Android Chrome, use the browser's
Install app / Add to Home screen action. On iPhone, use Share → Add to Home Screen
(enable Open as Web App if offered). Launch the home-screen icon and pair again
if that app context requests it. Check that browser toolbars are absent, the
composer stays visible while typing, the layout recovers when dismissing the
keyboard, long chats scroll, rotation works, and controls clear the notch/home
indicator. Test relaunching too; the manifest starts at `/`, without pairing
tokens or a pinned thread URL.

Any reproducible keyboard/scrolling defect should be assessed separately before
adding layout patches. Installation alone does not fix viewport bugs. Activation
belongs to external provisioning/maintenance, not a restart from this T3 session.

References:

- [Installability requirements and platform install UI](https://developer.mozilla.org/en-US/docs/Web/Progressive_web_apps/Guides/Making_PWAs_installable)
- [SVG manifest icons and raster fallbacks](https://web.dev/articles/add-manifest)
- [Android keyboard viewport behavior](https://developer.chrome.com/blog/viewport-resize-behavior)
- [WebKit home-screen standalone behavior](https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/)

- [Installed manifest updates and timing](https://web.dev/articles/manifest-updates)
