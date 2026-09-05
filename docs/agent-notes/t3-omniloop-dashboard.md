# Omniloop dashboard in T3 (2026-09-05)

Two independent issues caused a blank embedded dashboard:

- Desktop's `frame-src 'self'` policy rejected the selected environment's HTTP(S)
  origin, including the local HTTP backend behind the desktop's custom scheme.
  Both inventories now permit remote frames only under `/construct/omniloop/`;
  other cross-origin frame paths remain blocked.
- `useEnvironmentHttpBaseUrl` can return a trailing slash. Appending the absolute
  ticket path produced `//construct/omniloop/...`, which misses the proxy route.
  `OmniloopPanel` removes trailing slashes before joining and encodes workflow IDs.

The browser regression test uses the actual inventory's desktop policy, panel URL
expression, proxy asset rewrite, and Omniloop GUI sources. Workflow data alone is
synthetic. It reproduces both old failures, then checks HTTP and HTTPS frames,
workflow deep links, sidebar navigation, live SSE rendering, a 400px panel, and
continued blocking of unrelated remote frame paths. It never starts or changes a
production daemon. Run with `T3_TEST_SOURCE`, `T3_TEST_TOOLS`, `OMNILOOP_GUI_SOURCE`,
and optionally `T3_TEST_CHROMIUM`:

```
node test/t3-omniloop-frame.browser.test.mjs
```

Set `T3_TEST_CHANNEL=nightly` for the separate nightly inventory. Validated against
stable v0.0.38 and nightly v0.0.39-nightly.20260905.1288.
