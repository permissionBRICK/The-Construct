"use strict";
const assert = require("assert");
const { shouldOfferInstall } = require("../src/companion");
const eligible = { platform: "win32", deferred: false, offered: false, installed: false,
  setting: "auto", preference: true, registered: 1 };
assert.equal(shouldOfferInstall(eligible), true);
for (const patch of [{ platform: "linux" }, { deferred: true }, { offered: true },
  { installed: true }, { setting: "off" }, { setting: false }, { preference: false }, { registered: 0 }]) {
  assert.equal(shouldOfferInstall({ ...eligible, ...patch }), false);
}
assert.equal(shouldOfferInstall({ ...eligible, registered: 2 }), true);
console.log("companion install offer: 10 passed");
