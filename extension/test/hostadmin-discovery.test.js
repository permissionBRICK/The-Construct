"use strict";
const assert = require("assert/strict");
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const root = path.join(__dirname, "..");
const source = fs.readFileSync(path.join(root, "extension.js"), "utf8");
function section(start, end) { return source.slice(source.indexOf(start), source.indexOf(end, source.indexOf(start))); }
const offerCode = section("async function refreshHostAdminOffer(", "/** Child inventory stays");
const childrenCode = section("async function readHostAdminExtras(", "/** The panel\'s ");
const refreshCode = section("async function refreshState(", "/** Probe once and broadcast");
const remote = { name: "haus-vm", backend: "hyperv-remote" };
const offer = { host: "standpc", url: "https://standpc:7462" };
function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; }
const flush = () => new Promise(resolve => setImmediate(resolve));
(async () => {
  const messages = [], probe = deferred();
  let generation = 0, offerCalls = 0;
  const context = vm.createContext({
    companionDeferred: () => false,
    activeInstance: () => remote,
    instanceGate: { token: () => generation, valid: token => token === generation },
    instances: { captureTarget: () => ({ token: generation }) },
    probeOnce: () => probe.promise,
    hostAdminFeature: () => ({ hostAdminOfferFor: async () => { offerCalls++; return offer; },
      childrenStateFor: async () => ({ visible: true, items: [{ name: "shared-guest" }] }) }),
    liveWebviews: new Set(["sidebar", "panel"]), safePost: (view, message) => messages.push({ view, ...message }),
    cachedChildren: null, cachedHostAdminOffer: null, cachedHostAdminInstance: null,
    logLine: message => { throw Error(message); },
  });
  vm.runInContext(offerCode + childrenCode + refreshCode, context);
  const pendingRefresh = context.refreshState("panel");
  await flush();
  assert.equal(offerCalls, 1, "initial refresh discovers the host without waiting for VM status");
  assert.equal(messages.length, 4, "both surfaces receive the early offer and guest inventory");
  assert.ok(messages.filter(m => m.type === "hostAdminOffer").every(m => m.instance === "haus-vm" && m.offer === offer));
  assert.equal(messages.filter(m => m.type === "children" && m.children.items[0].name === "shared-guest").length, 2, "inventory loads while the VM probe is still pending");
  generation++; probe.resolve({}); await pendingRefresh;

  const delayed = deferred();
  context.hostAdminFeature = () => ({ hostAdminOfferFor: () => delayed.promise });
  const pendingOffer = context.refreshHostAdminOffer(remote);
  generation++; delayed.resolve(offer); await pendingOffer;
  assert.equal(messages.length, 4, "an answer after switching instances is discarded");
  const delayedChildren = deferred();
  context.hostAdminFeature = () => ({ childrenStateFor: () => delayedChildren.promise });
  const pendingChildren = context.readHostAdminExtras(remote);
  generation++; delayedChildren.resolve({ visible: true, items: [] }); await pendingChildren;
  assert.equal(messages.length, 4, "inventory from a previous instance is discarded too");
  await context.refreshHostAdminOffer({ name: "local", backend: "hyperv-local" });
  assert.equal(messages.at(-1).offer, null, "local instances clear the entry");

  // Execute the real sidebar controller: an early offer must work before a VM
  // status exists, survive partial updates, and reject messages for another VM.
  const elements = new Map(), posted = [];
  const element = id => {
    if (!elements.has(id)) elements.set(id, { hidden: true, style: {}, classList: { toggle() {} },
      setAttribute() {}, removeAttribute() {}, addEventListener() {}, appendChild() {} });
    return elements.get(id);
  };
  let receive;
  vm.runInNewContext(fs.readFileSync(path.join(root, "media/launcher.js"), "utf8"), {
    acquireVsCodeApi: () => ({ postMessage: message => posted.push(message) }),
    document: { getElementById: element, querySelectorAll: () => [], querySelector: () => null },
    window: { addEventListener: (_, callback) => { receive = callback; } },
  });
  receive({ data: { type: "hostAdminOffer", instance: "haus-vm", offer } });
  assert.equal(element("lHostAdmin").hidden, false);
  assert.equal(element("lHostAdmin").title, "Administer standpc");
  receive({ data: { type: "state", state: { instance: "haus-vm", online: false } } });
  assert.equal(element("lHostAdmin").hidden, false, "VM offline does not hide host administration");
  receive({ data: { type: "hostAdminOffer", instance: "another-vm", offer: null } });
  assert.equal(element("lHostAdmin").hidden, false, "wrong-instance update ignored");
  receive({ data: { type: "hostAdminOffer", instance: "haus-vm", offer: null } });
  assert.equal(element("lHostAdmin").hidden, true, "loss of admin access hides the entry");
  console.log("Host administration startup, slow-probe, instance-switch and sidebar rendering tests passed");
})().catch(e => { console.error(e); process.exitCode = 1; });
