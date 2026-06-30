# Webview UI smoke test

A headless-Chromium (Playwright) smoke test for the control-panel webview. It
serves the real `media/panel.{html,css,js}` with a mocked `acquireVsCodeApi` and
checks that the panel renders, that view switching and the controls work, and that
the webview ↔ extension message protocol holds in both directions. This covers the
UI logic without needing a running VS Code instance.

```bash
cd extension/test
npm install
npx playwright install chromium   # one-time browser download
npm test
```

Exit code is non-zero if any assertion fails, so it drops straight into CI.
