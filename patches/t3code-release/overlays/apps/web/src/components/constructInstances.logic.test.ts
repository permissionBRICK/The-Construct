import { assert, describe, it } from "@effect/vitest";
import type { ConstructInstanceInfo, ConstructUpdateInfo } from "@t3tools/contracts";

import {
  constructLinkedRemotes,
  constructRemoteEndpoint,
  getConstructRowDetail,
  matchConstructRemoteToInstance,
  planConstructProviderRows,
} from "./constructInstances.logic.ts";

const INSTALLED = "dc44958114c7c43145c8f7830f6185235a1d752b";
const PROVISIONED = "b262652aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

function instance(over: Partial<ConstructInstanceInfo> & { name: string }): ConstructInstanceInfo {
  return {
    vmHost: `${over.name}.mshome.net`,
    publicHost: null,
    hostAlias: over.name,
    isDefault: false,
    provisionedCommit: null,
    channel: null,
    t3Port: null,
    ...over,
  };
}

const AGENT = instance({ name: "agent-vm", isDefault: true, provisionedCommit: INSTALLED });
const WORK = instance({
  name: "work-vm",
  provisionedCommit: PROVISIONED,
  channel: "nightly",
});
const FAR = instance({
  name: "far-vm",
  vmHost: "buildbox.example.local",
  publicHost: "far-vm.vpn.example.local",
  provisionedCommit: INSTALLED,
  channel: "latest",
  // What Provision-AgentVM.ps1 published for it: the host forward's allocated port.
  t3Port: 23011,
});
const INSTANCES = [AGENT, FAR, WORK];

function info(over: Partial<ConstructUpdateInfo> = {}): ConstructUpdateInfo {
  return {
    repo: "permissionBRICK/The-Construct",
    ref: "main",
    scriptsDir: "C:\\scripts",
    vmName: "agent-vm",
    vmHost: "agent-vm.mshome.net",
    instances: INSTANCES,
    installedCommit: INSTALLED,
    provisionedCommit: INSTALLED,
    behind: null,
    constructUpdateAvailable: false,
    provisionStale: false,
    t3Version: "0.0.38",
    t3LatestVersion: "0.0.38",
    t3LatestByChannel: { latest: "0.0.38", nightly: null },
    t3UpdateAvailable: false,
    action: null,
    runningAction: null,
    checkedAt: "now",
    error: null,
    ...over,
  };
}

describe("constructRemoteEndpoint", () => {
  it("resolves host and port, including the scheme's default", () => {
    assert.deepEqual(constructRemoteEndpoint("https://work-vm.mshome.net:5178"), {
      host: "work-vm.mshome.net",
      port: 5178,
    });
    assert.deepEqual(constructRemoteEndpoint("https://WORK-VM.mshome.net/"), {
      host: "work-vm.mshome.net",
      port: 443,
    });
    assert.deepEqual(constructRemoteEndpoint("http://[fe80::1]:5177/"), {
      host: "fe80::1",
      port: 5177,
    });
  });

  it("answers null for anything that is not an http(s) URL", () => {
    assert.isNull(constructRemoteEndpoint("not a url"));
    assert.isNull(constructRemoteEndpoint("file:///etc/passwd"));
    assert.isNull(constructRemoteEndpoint(""));
  });
});

describe("matchConstructRemoteToInstance", () => {
  it("matches a local VM on its own T3 ports", () => {
    assert.equal(
      matchConstructRemoteToInstance("https://work-vm.mshome.net:5178", INSTANCES)?.name,
      "work-vm",
    );
    assert.equal(
      matchConstructRemoteToInstance("http://work-vm.mshome.net:5177/", INSTANCES)?.name,
      "work-vm",
    );
  });

  it("does NOT match a local VM's host on some other port", () => {
    // The host alone is not the key: a different service on the same machine is not
    // this VM's T3, and reprovisioning on that evidence would rebuild the wrong thing.
    assert.isNull(matchConstructRemoteToInstance("https://work-vm.mshome.net:8443", INSTANCES));
  });

  it("uses the port RECORDED for the instance when there is one", () => {
    const forwarded = [instance({ name: "work-vm", t3Port: 23011 })];
    assert.equal(
      matchConstructRemoteToInstance("https://work-vm.mshome.net:23011", forwarded)?.name,
      "work-vm",
    );
    assert.isNull(matchConstructRemoteToInstance("https://work-vm.mshome.net:5178", forwarded));
  });

  it("matches a service-published host on the port the provisioner PUBLISHED", () => {
    // far-vm's endpoint file records 23011 (see FAR), which is the port its host forward
    // was allocated — that is what the row matches on, and only that.
    assert.equal(
      matchConstructRemoteToInstance("https://far-vm.vpn.example.local:23011", INSTANCES)?.name,
      "far-vm",
    );
    assert.isNull(
      matchConstructRemoteToInstance("https://far-vm.vpn.example.local:5178", INSTANCES),
    );
  });

  it("matches a service-published VM whose port this PC has not seen when it is the ONLY claimant", () => {
    // No recorded port yet (provisioned before the port was written, or from another PC):
    // the host name is a per-VM name the service renders, so one claimant is not a
    // coincidence -- the row is offered and the next reprovision records the port.
    const unseen = [instance({ name: "far-vm", publicHost: "far-vm.vpn.example.local" })];
    assert.equal(matchConstructRemoteToInstance("https://far-vm.vpn.example.local:23011", unseen)?.name, "far-vm");
    assert.equal(matchConstructRemoteToInstance("https://far-vm.vpn.example.local:5178", unseen)?.name, "far-vm");
    // ...but a recorded port is exact again, and a LOCAL VM (no publicHost) on a non-T3
    // port never gets the host-only fallback.
    const recorded = [instance({ name: "far-vm", publicHost: "far-vm.vpn.example.local", t3Port: 23011 })];
    assert.isNull(matchConstructRemoteToInstance("https://far-vm.vpn.example.local:5178", recorded));
    const local = [instance({ name: "work-vm" })];
    assert.isNull(matchConstructRemoteToInstance("https://work-vm.mshome.net:9999", local));
    // Two service VMs claiming one host name without a recorded port match nothing.
    const two = [
      instance({ name: "far-vm", publicHost: "shared.vpn.example.local" }),
      instance({ name: "near-vm", publicHost: "shared.vpn.example.local" }),
    ];
    assert.isNull(matchConstructRemoteToInstance("https://shared.vpn.example.local:23011", two));
  });

  it("does NOT match on an ssh alias", () => {
    // `work-vm` is also the instance's ssh alias. An alias is a name in ~/.ssh/config
    // that nothing outside ssh resolves, so a linked HTTP origin that happens to equal
    // one is not evidence about which VM it is.
    const aliasOnly = [instance({ name: "work-vm", vmHost: "10.0.0.7", hostAlias: "work-vm" })];
    assert.isNull(matchConstructRemoteToInstance("https://work-vm:5178", aliasOnly));
    // ...while its actual sshHost still matches.
    assert.equal(matchConstructRemoteToInstance("https://10.0.0.7:5178", aliasOnly)?.name, "work-vm");
  });

  it("matches nothing for an unknown remote or an unusable URL", () => {
    assert.isNull(matchConstructRemoteToInstance("https://laptop.local:5178", INSTANCES));
    assert.isNull(matchConstructRemoteToInstance("not a url", INSTANCES));
  });

  it("refuses to guess when two instances claim one host and port", () => {
    const ambiguous = [
      instance({ name: "a-vm", publicHost: "shared.example", t3Port: 5178 }),
      instance({ name: "b-vm", publicHost: "shared.example", t3Port: 5178 }),
    ];
    assert.isNull(matchConstructRemoteToInstance("https://shared.example:5178", ambiguous));
    // ...but the same host on the port only ONE of them records is unambiguous.
    const split = [
      instance({ name: "a-vm", publicHost: "shared.example", t3Port: 5178 }),
      instance({ name: "b-vm", publicHost: "shared.example", t3Port: 23011 }),
    ];
    assert.equal(
      matchConstructRemoteToInstance("https://shared.example:23011", split)?.name,
      "b-vm",
    );
  });
});

describe("planConstructProviderRows", () => {
  const remotes = [
    { id: "1", baseUrl: "https://agent-vm.mshome.net:5178" },
    { id: "2", baseUrl: "https://work-vm.mshome.net:5178" },
    { id: "3", baseUrl: "https://far-vm.vpn.example.local:23011" },
  ];

  it("gives every linked remote a row, one per instance, with its OWN staleness", () => {
    const rows = planConstructProviderRows({ remotes, info: info() });
    assert.deepEqual(
      rows.map((r) => r.label),
      ["agent-vm", "work-vm", "far-vm"],
    );
    assert.equal(rows[0]!.provisionedCommit, INSTALLED);
    assert.isFalse(rows[0]!.provisionStale);
    // work-vm was provisioned with something else: only ITS row is stale.
    assert.equal(rows[1]!.provisionedCommit, PROVISIONED);
    assert.isTrue(rows[1]!.provisionStale);
    assert.isFalse(rows[2]!.provisionStale);
    assert.isTrue(rows.every((r) => r.canReprovision));
    assert.isTrue(rows.every((r) => r.installedCommit === INSTALLED));
  });

  it("reports each row's upstream release on ITS OWN channel", () => {
    const rows = planConstructProviderRows({
      remotes,
      info: info(),
      t3LatestByChannel: { latest: "0.0.38", nightly: "0.0.39-nightly.20260904.1" },
    });
    const work = rows.find((r) => r.instanceName === "work-vm")!;
    const far = rows.find((r) => r.instanceName === "far-vm")!;
    assert.equal(work.t3LatestVersion, "0.0.39-nightly.20260904.1");
    assert.isTrue(work.t3UpdateAvailable);
    assert.equal(far.t3LatestVersion, "0.0.38");
    assert.isFalse(far.t3UpdateAvailable);
    // An instance whose channel this PC does not know says nothing rather than
    // borrowing the app's own answer.
    assert.isNull(rows.find((r) => r.instanceName === "agent-vm")!.t3LatestVersion);
  });

  it("gives an unmatched remote a read-only row that says why", () => {
    const rows = planConstructProviderRows({
      remotes: [{ id: "x", baseUrl: "https://laptop.local:5178", label: "My laptop" }],
      info: info(),
    });
    assert.lengthOf(rows, 1);
    assert.equal(rows[0]!.label, "My laptop");
    assert.isNull(rows[0]!.instanceName);
    assert.isFalse(rows[0]!.canReprovision);
    assert.include(rows[0]!.note, "Not a Construct instance of this PC");
    assert.equal(getConstructRowDetail(rows[0]!), rows[0]!.note);
  });

  it("offers no button while another Construct action is running", () => {
    const rows = planConstructProviderRows({
      remotes: [remotes[1]!],
      info: info({ runningAction: "reprovision" }),
    });
    assert.isFalse(rows[0]!.canReprovision);
    assert.include(rows[0]!.note, "already running");
  });

  it("says nothing about staleness when a commit is unknown", () => {
    const rows = planConstructProviderRows({
      remotes: [remotes[1]!],
      info: info({ installedCommit: null }),
    });
    assert.isFalse(rows[0]!.provisionStale);
    const unknown = planConstructProviderRows({
      remotes: [remotes[1]!],
      info: info({ instances: [instance({ name: "work-vm" })] }),
    });
    assert.isNull(unknown[0]!.provisionedCommit);
    assert.isFalse(unknown[0]!.provisionStale);
    assert.include(getConstructRowDetail(unknown[0]!), "unknown");
  });

  it("renders nothing Construct-specific without Construct facts", () => {
    const rows = planConstructProviderRows({ remotes, info: null });
    assert.isTrue(rows.every((r) => r.instanceName === null));
  });

  it("writes a detail line that names what the row needs", () => {
    const rows = planConstructProviderRows({
      remotes: [remotes[1]!],
      info: info(),
      t3LatestByChannel: { nightly: "0.0.39-nightly.20260904.1" },
    });
    const detail = getConstructRowDetail(rows[0]!);
    assert.include(detail, "provisioned b262652");
    assert.include(detail, "reprovision pending");
    assert.include(detail, "T3 Code 0.0.39-nightly.20260904.1 available");
  });
});

describe("constructLinkedRemotes", () => {
  const envs = [
    // The app's OWN bundled server. Not a remote whatever address it is reached at —
    // including a LAN one, which a URL-shaped guess would have rendered as a remote.
    {
      environmentId: "local",
      label: "This computer",
      displayUrl: "http://localhost:5177",
      targetKind: "PrimaryConnectionTarget",
    },
    {
      environmentId: "local-lan",
      label: "This computer (LAN)",
      displayUrl: "http://alice-pc.lan:5177",
      targetKind: "PrimaryConnectionTarget",
    },
    // Relay and ssh connections carry no http(s) origin to match on.
    { environmentId: "relay", label: "Cloud", displayUrl: null, targetKind: "RelayConnectionTarget" },
    { environmentId: "ssh", label: "Box", displayUrl: null, targetKind: "SshConnectionTarget" },
    {
      environmentId: "junk",
      label: "Broken",
      displayUrl: "not a url",
      targetKind: "BearerConnectionTarget",
    },
    // The real linked remotes — including one reached through a LOOPBACK tunnel, which a
    // URL-shaped guess would have dropped.
    {
      environmentId: "work",
      label: "work",
      displayUrl: "https://work-vm.mshome.net:5178",
      targetKind: "BearerConnectionTarget",
    },
    {
      environmentId: "far",
      label: "far",
      displayUrl: "https://far-vm.vpn.example.local:23011",
      targetKind: "BearerConnectionTarget",
    },
    {
      environmentId: "tunnelled",
      label: "through a tunnel",
      displayUrl: "http://localhost:18801",
      targetKind: "BearerConnectionTarget",
    },
  ];

  it("keeps the linked remotes, by connection KIND", () => {
    assert.deepEqual(
      constructLinkedRemotes(envs).map((r) => r.id),
      ["work", "far", "tunnelled"],
    );
  });

  it("never treats the app's own server as a remote, whatever its address", () => {
    assert.deepEqual(
      constructLinkedRemotes(envs.filter((e) => e.targetKind === "PrimaryConnectionTarget")),
      [],
    );
  });

  it("keeps a linked remote reached through a loopback tunnel", () => {
    assert.deepEqual(
      constructLinkedRemotes(envs).map((r) => r.baseUrl),
      [
        "https://work-vm.mshome.net:5178",
        "https://far-vm.vpn.example.local:23011",
        "http://localhost:18801",
      ],
    );
  });

  it("carries the address and the app's own label through", () => {
    const remotes = constructLinkedRemotes(envs);
    assert.equal(remotes[0]!.baseUrl, "https://work-vm.mshome.net:5178");
    assert.equal(remotes[0]!.label, "work");
  });

  it("feeds the planner, so the app's own server never becomes a row", () => {
    const rows = planConstructProviderRows({ remotes: constructLinkedRemotes(envs), info: info() });
    assert.deepEqual(
      rows.map((r) => r.label),
      ["work-vm", "far-vm", "through a tunnel"],
    );
  });
});
