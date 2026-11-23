"use strict";
// The register-workspace decision (src/workspace.js). Run: node --test extension/test/
const test = require("node:test");
const assert = require("node:assert/strict");
const ws = require("../src/workspace");

const VM = { engine: "a1b2c3d4e5f6", vm: "work" };
const URL_ = "https://github.com/owner/shop.git";

test("the window's SSH alias and the checkout directory", () => {
  assert.equal(ws.sshAlias("ssh-remote+work"), "work");
  assert.equal(ws.sshAlias("SSH-REMOTE+Work.lan"), "Work.lan");
  assert.equal(ws.sshAlias("wsl+Ubuntu"), "");
  assert.equal(ws.sshAlias(undefined), "");
  assert.equal(ws.checkoutName("/root/repos/shop"), "shop");
  assert.equal(ws.checkoutName("/root/repos/shop/"), "shop");
  assert.equal(ws.checkoutName("/root/repos/shop/src"), "");
  assert.equal(ws.checkoutName("/root/repos"), "");
  assert.equal(ws.checkoutName("/home/x/shop"), "");
});

test("the origin url from a .git/config", () => {
  const config = [
    "[core]", "\turl = https://not-this/", "[remote \"upstream\"]", "\turl = https://upstream/x.git",
    "[remote \"origin\"]", "\turl = git@github.com:owner/shop.git", "\tfetch = +refs/heads/*:refs/remotes/origin/*",
  ].join("\n");
  assert.equal(ws.originUrl(config), "git@github.com:owner/shop.git");
  assert.equal(ws.originUrl("[remote \"origin\"]\r\n  URL = \"https://h/x.git\"\r\n"), "https://h/x.git");
  assert.equal(ws.originUrl("[remote \"upstream\"]\nurl = x\n"), "");
  assert.equal(ws.originUrl(""), "");
});

test("the attached VM is the connection with this alias", () => {
  const conns = [{ engine: "e1", vm: "other", alias: "other" }, { engine: VM.engine, vm: VM.vm, alias: "Work" }];
  assert.deepEqual(ws.attachedVm(conns, "work"), VM);
  assert.equal(ws.attachedVm(conns, "nope"), null);
  assert.equal(ws.attachedVm(null, "work"), null);
  assert.equal(ws.attachedVm(conns, ""), null);
});

test("the offer: only for a checkout with an origin that no profile has", () => {
  const base = { vm: VM, name: "shop", url: URL_, profiles: [], dismissed: [] };
  assert.deepEqual(ws.planOffer(base), { offer: true, name: "shop", url: URL_ });
  assert.equal(ws.planOffer({ ...base, url: "  " + URL_ + " " }).url, URL_, "trimmed as the old import did");
  const no = (over, why) => { const r = ws.planOffer({ ...base, ...over }); assert.equal(r.offer, false, why); assert.ok(r.reason, why); };
  no({ vm: null }, "not one of the user's VMs");
  no({ name: "" }, "not a checkout under /root/repos");
  no({ url: "" }, "no origin remote");
  no({ name: "default" }, "reserved name");
  no({ name: "Project.Schema" }, "reserved name, any case");
  no({ name: "-x" }, "not a profile name");
  no({ profiles: [{ name: "store", profile: { repos: [{ url: URL_ }] } }] }, "a profile has this repository");
  no({ profiles: [{ name: "SHOP", profile: { repos: [] } }] }, "a profile of that name, any case");
  no({ dismissed: [URL_] }, "never ask again");
  assert.equal(ws.planOffer({ ...base, profiles: [{ name: "x", profile: null }, null] }).offer, true, "odd profiles do not break it");
});

test("the profile written and the VM's desired state", () => {
  assert.deepEqual(ws.newProfile("shop", URL_), { name: "shop", repos: [{ url: URL_, directory: "shop" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} });
  assert.deepEqual(ws.withProject({ projects: ["a"], settings: { x: 1 } }, "shop"), { projects: ["a", "shop"], settings: { x: 1 } });
  assert.equal(ws.withProject({ projects: ["shop"], settings: {} }, "shop"), null, "already on");
  assert.equal(ws.withProject(null, "shop"), null, "no desired state stored: left alone");
});
