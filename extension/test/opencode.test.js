"use strict";
// Plain-node tests for the optional OpenCode background-watcher live control.
// Run: node opencode.test.js

const fs = require("node:fs");
const path = require("node:path");
const opencode = require("../src/opencode");

let pass = 0, fail = 0;
function ok(name, cond) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name); }
}

ok("plan: unchanged settings do nothing",
  opencode.planBackgroundWatcherLiveAction(false, false) === null &&
  opencode.planBackgroundWatcherLiveAction(true, true) === null);
ok("plan: off to on enables", opencode.planBackgroundWatcherLiveAction(true, false) === "enable");
ok("plan: on to off disables", opencode.planBackgroundWatcherLiveAction(false, true) === "disable");

const enable = opencode.buildBackgroundWatcherEnableScript();
const disable = opencode.buildBackgroundWatcherDisableScript();
ok("enable: targets OpenCode's global plugin directory", enable.includes(opencode.TARGET));
ok("enable: validates before the atomic move", enable.includes("node --check") && enable.includes("mv -f --"));
ok("enable: persists the feature flag", enable.includes("cfgset OPENCODE_BACKGROUND_WATCHER true"));
ok("enable: can migrate only the archive's old watcher symlink",
  enable.includes("*/opencode-cortecs-config/plugins/background.js"));
ok("enable: refuses to overwrite an unmanaged plugin",
  enable.includes("refusing to replace unmanaged OpenCode plugin"));
ok("disable: removes only a marker-owned plugin",
  disable.includes("grep -Fq \"$MARKER\"") && disable.includes("refusing to remove unmanaged OpenCode plugin"));
ok("disable: persists the off state", disable.includes("cfgset OPENCODE_BACKGROUND_WATCHER false"));

const plugin = fs.readFileSync(path.join(__dirname, "..", "vm", "opencode-background.js"), "utf8");
ok("plugin: Construct ownership marker is present", plugin.includes(opencode.MARKER));
ok("plugin: exposes only the requested background tool family",
  plugin.includes("background:") && plugin.includes("background_output:") && plugin.includes("background_kill:"));
ok("plugin: retains watcher wake-up behavior", plugin.includes("session.promptAsync"));
ok("plugin: excludes the Cortecs request hook and fallback setting",
  !plugin.includes("enable_model_fallback") && !plugin.includes("chat.params") && !plugin.includes("providerID"));

console.log(`\n${pass} passed, ${fail} failed`);
process.exitCode = fail ? 1 : 0;
