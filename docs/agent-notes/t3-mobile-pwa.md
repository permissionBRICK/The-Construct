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
separately, with identical icon overlays. Each inventory adds eleven lines of
transform configuration plus a four-line SVG; only six lines are inserted into
upstream's manifest. There are no React, CSS, server or build-system changes.

`standalone` removes browser toolbars while retaining normal OS status/navigation
controls. A custom in-page Install button would need additional UI/event code;
this experiment uses the browser's installation UI. No service worker or offline
cache is needed for this scope.

## Verification

- Applied the PWA-only transforms and overlay to pristine files from each tag.
  Reapplying reports zero changes and all three operations already present.
- Used real, headed Chromium 153.0.8010.12 with a persistent, non-incognito profile
  against an isolated localhost page serving each manifest and its icon assets.
  `Page.getAppManifest` reports no parse errors; `Page.getInstallabilityErrors`
  reports no errors for either patched channel; `Page.getManifestIcons` returns
  a decoded icon. The stable baseline reports `manifest-missing-name-or-short-name`.
  The test page isolates metadata; it does not run T3's application.
- Existing source-transform suite: 10/10 passed. `git diff --check` passed.
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
