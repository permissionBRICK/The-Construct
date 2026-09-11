"use strict";
const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");
const readline = require("readline");
const { once } = require("events");
const { createClient, snapshotMessages } = require("../src/companion");
(async () => {
  const dll = path.resolve(__dirname, "../../companion/src/Construct.Companion.Host/bin/Debug/net10.0/Construct.Companion.Host.dll");
  assert.ok(fs.existsSync(dll), "Build the Companion solution before this integration suite");
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "companion-host-"));
  let count = 0;
  try {
    for (const remoteOnly of [false, true]) {
      const child = spawn("dotnet", [dll, "--fake", ...(remoteOnly ? ["--remote-only"] : [])], { stdio: ["ignore", "pipe", "pipe"] });
      const exited = once(child, "exit");
      const lines = readline.createInterface({ input: child.stdout });
      let client;
      const timer = setTimeout(() => child.kill(), 20000);
      try {
        const [line] = await once(lines, "line");
        const endpoint = JSON.parse(line);
        const file = path.join(dir, "endpoint.json");
        fs.writeFileSync(file, JSON.stringify(endpoint));
        const events = [];
        let connected;
        const streamReady = new Promise(resolve => { connected = resolve; });
        client = createClient({ endpointPath: file, onConnect: connected, onEvent: (kind, data) => events.push({ kind, data }) });
        await client.start("auto");
        assert.equal(client.status, "alive"); count++;
        await streamReady;
        const state = await client.request("GET", "/v1/state");
        assert.deepEqual(state.instances, remoteOnly ? ["remote-vm"] : ["agent-vm", "remote-vm"]); count++;
        const snapshot = await client.snapshot("remote-vm");
        assert.equal(snapshot.state.state.backend, "hyperv-remote"); count++;
        assert.equal(snapshot.state.state.connectedInstance, null); count++;
        const panelState = snapshotMessages(snapshot, "attached")[0].state;
        assert.equal(panelState.children, snapshot.children.children); count++;
        assert.equal(panelState.idlePolicy, snapshot.idlePolicy.idlePolicy); count++;
        assert.equal(panelState.hostAdminOffer, snapshot.hostAdminOffer.offer); count++;
        await client.proxy("remote-vm", { type: "command", id: "notSupported" });
        for (let i = 0; i < 200 && !events.some(e => e.data.message?.id === "notSupported"); i++) await new Promise(r => setTimeout(r, 10));
        assert.ok(events.some(e => e.kind === "message" && e.data.instance === "remote-vm" && e.data.message.error)); count++;
        await client.activate("settings", "remote-vm"); count++;
        await client.request("POST", "/v1/quit", { reason: "user" });
        client.dispose(); client = null;
        const [code] = await exited;
        assert.equal(code, 0); count++;
      } finally {
        if (client) client.dispose();
        lines.close();
        if (child.exitCode === null && child.signalCode === null) child.kill();
        await exited; clearTimeout(timer);
      }
    }
  } finally { fs.rmSync(dir, { recursive: true }); }
  console.log(`companion real host: ${count} passed`);
})().catch(error => { console.error(error); process.exitCode = 1; });
