"use strict";
// One deterministic exporter per area. No timestamps, local home paths, or live I/O.
const fs = require("fs");
const path = require("path");
const scripts = require("../src/guest-scripts");
const forwards = require("../src/forwarder");
const notify = require("../src/notify");
const audio = require("../src/audio");
const ssh = require("../src/ssh");
const t3 = require("../src/t3code");
const usage = require("../src/usage");
const probe = require("../src/probe");
const q = forwards.shQuote;
const directory = path.resolve(__dirname, "../../test/fixtures/companion-parity");

function guestScripts() {
  const rows = [];
  const add = (name, values, output = scripts.render(name, values)) => rows.push({ name, values, output });
  for (const dir of ["/etc/construct/forwards", "/tmp/spool with 'quotes' {{dir}}"] ) {
    add("forwards-capability", { dir: q(dir) }, forwards.buildCapabilityScript({ dir }));
    add("forwards-reconcile", { dir: q(dir), me: q("cc-test"), ttl: "90", lockTtl: "60" }, forwards.buildReconcileScript({ dir, windowId: "cc-test" }));
    add("forwards-watch", { dir: q(dir), heartbeat: "60", fallback: "30" }, forwards.buildWatchScript({ dir }));
    const doc = { v: 1, id: "demo", status: "open", localPort: 5173 };
    add("forwards-ack", { dir: q(dir + "/acks"), id: "demo", b64: q(Buffer.from(JSON.stringify(doc) + "\n").toString("base64")) }, forwards.buildAckScript("demo", doc, { dir }));
    add("forwards-remove", { paths: q(dir + "/acks/demo.json") + " " + q(dir + "/close/demo.json") }, forwards.buildRemoveScript([{ sub: "acks", id: "demo" }, { sub: "close", id: "demo" }], { dir }));
    add("forwards-release", { own: q(dir + "/.owner"), me: q("cc-test") });
  }
  const claim = scripts.render("notify-claim-function");
  add("notify-claim-function", {});
  for (const dir of ["/run/construct/notify", "/tmp/notify with 'quotes'"]) {
    add("notify-claim", { dir: q(dir), claim }, notify.buildClaimScript(dir));
    add("notify-watch", { dir: q(dir), claim, heartbeat: "60", fallback: "3" }, notify.buildWatchScript({ dir }));
  }
  add("probe", {}, probe.REMOTE_PROBE);
  for (const text of ["", "#!/bin/bash\necho 'hello'\n# Unicode: ü\n"]) {
    add("audio-enable", { port: "8767", count: "8", shim: q(Buffer.from(text).toString("base64")), enable: q(Buffer.from(text).toString("base64")) }, audio.buildEnableScript(text, text));
    add("audio-disable", { self: "0", port: "8767", count: "8", disable: q(Buffer.from(text).toString("base64")) }, audio.buildDisableScript(text));
  }
  add("t3-pairing", {}, t3.buildPairingScript());
  for (const name of ["dev", "build-2"]) add("t3-pairing-instance", { instance: name }, t3.buildPairingScript({ name }));
  for (const report of ["daily", "monthly", "total"]) add("usage", { report }, usage.buildUsageScript(report));
  for (const name of scripts.names.filter(name => name.startsWith("construct-"))) add(name, {});
  return rows;
}

function sshArgs() {
  const rows = [];
  // Replace the environment-dependent key prefix with an explicit fixture root.
  const keyPath = "/fixture/home/.ssh/test key";
  for (const hasKey of [false, true]) for (const port of [22, 2222, 0, 65536]) {
    const cfg = { ...ssh.DEFAULTS, keyName: "test key", vmHost: "vm.example", hostAlias: "vm-alias", sshPort: port };
    const stable = args => args.map(arg => arg === ssh.keyPath(cfg) ? keyPath : arg);
    for (const command of ["true", ssh.wrapScriptCommand("echo 'ü'\n")])
      rows.push({ kind: "run", cfg, keyPath: hasKey ? keyPath : null, command, output: stable(ssh.buildSshArgs(cfg, command, hasKey)) });
    for (const opts of [{}, { bindHost: "*" }, { bindHost: "::", connectAddress: "[2001:db8::1]", connectPort: 443 }, { bindHost: "bad", connectAddress: "child.example" }])
      rows.push({ kind: "forward", cfg, keyPath: hasKey ? keyPath : null, localPort: 18801, vmPort: 5173, opts, output: stable(ssh.buildLocalForwardArgs(cfg, 18801, 5173, hasKey, opts)) });
  }
  for (const input of [null, "", "127.0.0.1", "001.2.3.4", "[::1]", "::ffff:1.2.3.004", "fe80::1%eth0", "[fe80::1%3]", "fe80::1%a_b", "fe80::1%a:b", "--option", "a/b", "foo_bar", "-host", "a".repeat(65), "\uFEFFchild.example\uFEFF", "\u0085child.example"]) rows.push({ kind: "address", input, output: ssh.normalizeConnectAddress(input) });
  for (const input of [null, "", "0.0.0.0", "*", "::", "bad", "\uFEFF*\uFEFF", "\u0085*"]) rows.push({ kind: "bind", input, output: ssh.normalizeBindHost(input) });
  for (const input of [0, -1, 22, 2222, 65535, 65536]) rows.push({ kind: "port", input, output: ssh.normalizeSshPort(input) });
  return rows;
}

function hostLabel() {
  // Same IPv6 matrix as forwarder.test.js / construct-expose.test.sh / service ForwardHostTests.
  const valid = ["::", "::1", "fe80::1", "2001:db8::8a2e:370:7334", "1:2:3:4:5:6:7:8", "0:0:0:0:0:0:0:0", "1::", "::2", "0::0", "::ffff:10.0.0.1", "1:2:3:4:5:6:1.2.3.4", "::1.2.3.4", "1::1.2.3.4", "1:2:3:4:5:6:7::", "fe80::0204:61ff:fe9d:f156", "ABCD::1"];
  const invalid = ["::::", "1::2::3", "1:2:3:4:5:6:7:8:9", "1.2.3:4", "....:", ":::", ":1", "1:", "12345::1", "::ffff:999.1.1.1", "::ffff:1.2.3.004", "1:2", "1:2:3:4:5:6:7", "1:::", "1::2:3:4:5:6:7:8", "::1.2.3.4.5", "1:2:3:4:5:6:7:1.2.3.4", "192.0.2.1::", "192.0.2.1::1", "1:192.0.2.1::", "1.2.3.4::1:2"];
  return [null, "", "localhost", " PC name ", "host.example", "127.0.0.1", "a_b-c", "a/b", "[]", "[fe80::1", "fe80::1]", "[[::1]]", "fe80::1%eth0", "a\u0085b", "\u0085host", "host\u0085", "a\uFEFFb", "x".repeat(201), ...valid, ...valid.map(v => `[${v}]`), ...invalid].map(input => ({ input, normalized: forwards.sanitizeHostLabel(input), urlHost: forwards.urlHostFor(input), bindHost: forwards.bindHostFor(input) }));
}
function shellQuoting() {
  return [null, "", "plain", "a'b", "'", "a\nline\r\n", "$(id); `id` \\ \"", "ü🧱", "{{dir}}"].map(input => ({ input, output: q(input) }));
}
function sortKeys(value) {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value && typeof value === "object") return Object.fromEntries(Object.keys(value).sort().map(key => [key, sortKeys(value[key])]));
  return value;
}
function serialize(value) { return JSON.stringify(sortKeys(value), null, 2) + "\n"; }
function exportAll() { return { "guest-scripts": guestScripts(), "ssh-args": sshArgs(), "host-label": hostLabel(), "shell-quoting": shellQuoting() }; }
if (require.main === module) {
  fs.mkdirSync(directory, { recursive: true });
  for (const [area, value] of Object.entries(exportAll())) fs.writeFileSync(path.join(directory, area + ".json"), serialize(value));
}
module.exports = { guestScripts, sshArgs, hostLabel, shellQuoting, exportAll, serialize, directory };
