"use strict";
// The webview documents and the message bridge (src/page.js). The frame document's script runs here in
// a node vm with a fake window, frame and webview API. Run: node --test extension/test/
const test = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const page = require("../src/page");

const TOKEN = "abc_DEF-123".repeat(4);

test("the frame's address carries the token in the fragment only", () => {
  const url = page.frameUrl(5123, TOKEN, "pop");
  assert.equal(url, `http://127.0.0.1:5123/?surf=pop#agent-token=${TOKEN}`);
  assert.equal(new URL(url).search.includes(TOKEN), false);
  assert.throws(() => page.frameUrl(5123, TOKEN, "phone"));
  assert.throws(() => page.frameUrl(0, TOKEN, "win"));
  assert.equal(page.routeUrl(5123, "win", "#/host/orchard/users"), "http://127.0.0.1:5123/?surf=win#/host/orchard/users");
  for (const bad of ["#/a?b", "/hosts", "#/x\"y", "#/" + "a".repeat(300), "javascript:alert(1)"]) assert.throws(() => page.routeUrl(1, "win", bad), bad);
});

test("the frame document: frames only from the agent, sandboxed, no token in it", () => {
  const html = page.frameHtml({ nonce: "N0nce", port: 5123 });
  const csp = /content="([^"]+)"/.exec(html)[1];
  assert.match(csp, /default-src 'none'/);
  assert.match(csp, /frame-src http:\/\/127\.0\.0\.1:5123;/);
  assert.match(csp, /script-src 'nonce-N0nce'/);
  assert.doesNotMatch(csp, /unsafe|\*/);
  const frame = /<iframe[^>]*>/.exec(html)[0];
  assert.match(frame, /sandbox="allow-scripts allow-same-origin allow-forms"/);
  assert.doesNotMatch(frame, /\ssrc=|allow-top-navigation|allow-popups/);
  assert.doesNotMatch(html, /agent-token|setState/);
});

test("the status document escapes what it shows", () => {
  const html = page.statusHtml({ nonce: "n", title: "<b>x</b>", text: "a & b", detail: "<script>alert(1)</script>", button: { id: "install", label: "Install" } });
  assert.doesNotMatch(html, /<script>alert/);
  assert.match(html, /&lt;b&gt;x&lt;\/b&gt;/);
  assert.match(html, /data-id="install"/);
  assert.match(html, /default-src 'none'/);
});

test("the bridge accepts only the page's known messages", () => {
  assert.deepEqual(page.pageMessage({ type: "openWindow", route: "#/hosts" }), { type: "openWindow", route: "#/hosts" });
  assert.deepEqual(page.pageMessage({ type: "openWindow" }), { type: "openWindow", route: "#/" });
  assert.deepEqual(page.pageMessage({ type: "close" }), { type: "close" });
  assert.deepEqual(page.pageMessage({ type: "openVsCode", engine: "a1b2c3d4e5f6", vm: "work", extra: 1 }), { type: "openVsCode", engine: "a1b2c3d4e5f6", vm: "work" });
  assert.deepEqual(page.pageMessage({ type: "openExternal", url: "http://localhost:3000/app?x=1" }), { type: "openExternal", url: "http://localhost:3000/app?x=1" });
  for (const url of ["https://evil.example/", "http://localhost.evil.example:80/", "file:///etc/passwd", "http://localhost:3000/#x", "vscode://x", 7])
    assert.equal(page.pageMessage({ type: "openExternal", url }), null, String(url));
  for (const bad of [null, "openWindow", [], { type: "openWindow", route: "https://evil/" }, { type: "openVsCode", engine: "../x", vm: "w" },
    { type: "openVsCode", engine: "e", vm: "w;rm -rf" }, { type: "run", command: "x" }]) {
    assert.equal(page.pageMessage(bad), null, JSON.stringify(bad));
  }
});

/** Runs the frame document's script against fakes; returns what it did. */
function runFrameScript(port) {
  const html = page.frameHtml({ nonce: "n", port });
  const script = /<script nonce="n">([\s\S]*?)<\/script>/.exec(html)[1];
  const posted = [];
  const navigations = [];
  const listeners = {};
  const frameListeners = [];
  let crossOrigin = false;
  const contentWindow = {
    location: {
      replace(u) { navigations.push(u); },
      get href() { if (crossOrigin) throw new Error("SecurityError"); return "about:blank"; },
    },
  };
  const frame = { contentWindow, addEventListener(t, f) { frameListeners.push(f); }, removeEventListener(t, f) { frameListeners.splice(frameListeners.indexOf(f), 1); } };
  const sandbox = {
    acquireVsCodeApi: () => ({ postMessage: m => posted.push(JSON.parse(JSON.stringify(m))), setState: () => assert.fail("no state") }),
    document: { getElementById: id => (id === "agent" ? frame : null) },
    window: { addEventListener: (t, f) => { listeners[t] = f; } },
  };
  vm.runInNewContext(script, sandbox);
  return {
    posted, navigations, contentWindow,
    send: (data, source = null, origin = "vscode-webview://x") => listeners.message({ data, source, origin }),
    frameLoad: (agentPage) => { crossOrigin = agentPage; for (const f of [...frameListeners]) f(); },
  };
}

test("the frame script: navigates the frame to the agent only, then the route after the page loaded", () => {
  const w = runFrameScript(5123);
  assert.deepEqual(w.posted, [{ type: "ready" }]);
  w.send({ type: "load", url: "https://evil.example/" });
  w.send({ type: "load", url: "http://127.0.0.1:51234/" });
  w.send({ type: "load", url: "http://127.0.0.1:5123.evil/" });
  assert.deepEqual(w.navigations, [], "nothing but the agent's origin");
  w.send({ type: "load", url: page.frameUrl(5123, TOKEN, "win"), then: page.routeUrl(5123, "win", "#/hosts") });
  assert.deepEqual(w.navigations, [page.frameUrl(5123, TOKEN, "win")]);
  w.frameLoad(false);
  assert.equal(w.navigations.length, 1, "the about:blank load is not the page");
  w.frameLoad(true);
  assert.deepEqual(w.navigations.slice(1), ["http://127.0.0.1:5123/?surf=win#/hosts"]);
  w.frameLoad(true);
  assert.equal(w.navigations.length, 2, "the route is sent once");
});

test("the frame script: passes on the page's messages, from that frame and origin only", () => {
  const w = runFrameScript(5123);
  w.send({ type: "openWindow", route: "#/vault" }, w.contentWindow, "http://127.0.0.1:5123");
  w.send({ type: "openWindow", route: "#/x" }, w.contentWindow, "http://127.0.0.1:9999");
  w.send({ type: "openWindow", route: "#/y" }, {}, "http://127.0.0.1:5123");
  assert.deepEqual(w.posted.slice(1), [{ type: "page", message: { type: "openWindow", route: "#/vault" } }]);
  w.send({ type: "load", url: "http://127.0.0.1:5123/?surf=win" }, w.contentWindow, "http://127.0.0.1:5123");
  assert.deepEqual(w.navigations, [], "the page cannot steer its own frame through the bridge");
});
