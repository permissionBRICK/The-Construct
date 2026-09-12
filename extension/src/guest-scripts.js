"use strict";
// Shared guest sources are read once. Values are shell-ready text supplied by builders;
// replacement is one pass so data containing {{markers}} is never interpreted again.
const fs = require("fs");
const path = require("path");
const names = [
  "console-close", "console-ensure", "forwards-capability", "forwards-watch", "forwards-reconcile", "forwards-ack",
  "forwards-remove", "forwards-release", "notify-claim-function", "notify-claim", "notify-watch",
  "project-clone", "project-scan", "probe", "audio-enable", "audio-disable", "t3-pairing", "t3-pairing-instance", "construct-t3-pairing-base", "usage",
  "construct-rec-shim", "construct-audio-enable", "construct-audio-disable",
  "construct-patch-status", "construct-partial-streaming-enable", "construct-partial-streaming-disable",
];
const templates = Object.fromEntries(names.map(name => [name,
  fs.readFileSync(path.join(__dirname, "..", "vm", name + ".sh"), "utf8")]));
function render(name, values = {}) {
  if (!Object.hasOwn(templates, name)) throw new Error("Unknown guest script");
  return templates[name].replace(/\{\{([A-Za-z][A-Za-z0-9]*)\}\}/g, (_, key) => {
    if (!Object.hasOwn(values, key)) throw new Error("Missing guest script value: " + key);
    return String(values[key]);
  });
}
module.exports = { names, render };
