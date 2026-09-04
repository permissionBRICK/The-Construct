import { describe, expect, it } from "@effect/vitest";

import {
  CONSTRUCT_OMNILOOP_PROXY_PREFIX,
  makeOmniloopTicketStore,
  omniloopGuiPath,
  parseOmniloopConfig,
  workflowStatusFromDetail,
} from "./constructOmniloop.ts";
import {
  rewriteOmniloopAsset,
  rewriteOmniloopLocation,
  splitOmniloopProxyPath,
} from "./constructOmniloopProxy.ts";

describe("parseOmniloopConfig", () => {
  it("reads port and token, ignoring comments and quoting", () => {
    const config = parseOmniloopConfig(
      [
        "port = 4711                        # env OMNILOOP_PORT overrides",
        'token = "ol_cfg_abcd1234"  # auth token',
        "server_hold_ms = 270000",
        "[harness.claude]",
        "max_concurrent = 5",
      ].join("\n"),
      {},
    );
    expect(config).toEqual({ port: 4711, token: "ol_cfg_abcd1234" });
  });

  it("falls back to the default port and no token, with OMNILOOP_PORT winning", () => {
    expect(parseOmniloopConfig("", {})).toEqual({ port: 4700, token: null });
    expect(parseOmniloopConfig("port = 4711", { OMNILOOP_PORT: "4800" }).port).toBe(4800);
  });
});

describe("tickets", () => {
  it("mints opaque tickets that expire and are capped", () => {
    const store = makeOmniloopTicketStore(1000, 2);
    const first = store.mint(0);
    expect(first.ticket).toMatch(/^[A-Za-z0-9_-]{32}$/);
    expect(first.expiresAt).toBe(1000);
    expect(store.isValid(first.ticket, 999)).toBe(true);
    expect(store.isValid(first.ticket, 1000)).toBe(false);
    expect(store.isValid("nope", 0)).toBe(false);
    const second = store.mint(10);
    const third = store.mint(20);
    expect(store.isValid(first.ticket, 30)).toBe(false); // evicted by the cap
    expect(store.isValid(second.ticket, 30)).toBe(true);
    expect(store.isValid(third.ticket, 30)).toBe(true);
  });

  it("builds the dashboard path under the ticket, carrying the daemon token", () => {
    expect(omniloopGuiPath("abc", "ol_cfg_x/y")).toBe(
      `${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/abc/gui/index.html?token=ol_cfg_x%2Fy`,
    );
    expect(omniloopGuiPath("abc", null)).toBe(`${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/abc/gui/index.html`);
  });
});

describe("workflowStatusFromDetail", () => {
  it("accepts the detail envelope and a bare summary", () => {
    expect(
      workflowStatusFromDetail({ workflow: { id: "wf_a", name: "review", status: "running" }, nodes: [] }),
    ).toEqual({ id: "wf_a", name: "review", status: "running" });
    expect(workflowStatusFromDetail({ id: "wf_b", status: "weird" })).toEqual({
      id: "wf_b",
      name: "wf_b",
      status: "unknown",
    });
    expect(workflowStatusFromDetail({ nope: true })).toBeNull();
  });
});

describe("proxy path handling", () => {
  it("splits the ticket from the upstream path", () => {
    expect(splitOmniloopProxyPath("/construct/omniloop/abcdefghijklmnop/gui/index.html")).toEqual({
      ticket: "abcdefghijklmnop",
      rest: "/gui/index.html",
    });
    expect(splitOmniloopProxyPath("/construct/omniloop/abcdefghijklmnop")).toEqual({
      ticket: "abcdefghijklmnop",
      rest: "/",
    });
    expect(splitOmniloopProxyPath("/construct/omniloop/short/gui/")).toBeNull();
    expect(splitOmniloopProxyPath("/construct/other/abcdefghijklmnop/gui/")).toBeNull();
  });

  it("rewrites the dashboard's absolute references and redirects under the ticket", () => {
    const base = "/construct/omniloop/abcdefghijklmnop";
    expect(
      rewriteOmniloopAsset(
        `<script src="/gui/app.js"></script> fetch('/api/workflows/' + id); new EventSource('/sse/workflows'); \`/api/x\``,
        base,
      ),
    ).toBe(
      `<script src="${base}/gui/app.js"></script> fetch('${base}/api/workflows/' + id); new EventSource('${base}/sse/workflows'); \`${base}/api/x\``,
    );
    expect(rewriteOmniloopAsset("window.location.hash = '#/w/' + wfId;", base)).toBe(
      "window.location.hash = '#/w/' + wfId;",
    );
    expect(rewriteOmniloopLocation("/gui/index.html?x=1", base)).toBe(`${base}/gui/index.html?x=1`);
    expect(rewriteOmniloopLocation("https://elsewhere.test/", base)).toBe("https://elsewhere.test/");
  });
});
