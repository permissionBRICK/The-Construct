import { assert, describe, it } from "@effect/vitest";
import type { ConstructUpdateInfo, DesktopUpdateState } from "@t3tools/contracts";

import {
  applyConstructInfoToState,
  checkConstructUpdates,
  constructAvailableVersionLabel,
  constructT3BaseVersion,
  constructReprovisionIdentityArgs,
  constructSupportsInstanceName,
  constructUpdateFromCompare,
  DEFAULT_CONSTRUCT_VM_TARGET,
  deriveConstructUpdateInfo,
  findConstructScriptsDir,
  isConstructManagedBuild,
  isConstructProvisionStale,
  isNewerConstructT3Version,
  collectConstructInstances,
  planConstructInstanceReprovision,
  planConstructLaunch,
  constructInstanceStatePath,
  readConstructInstanceState,
  readConstructInstances,
  readConstructInstancesFromRegistry,
  readConstructMarkers,
  readConstructMarkersFromDir,
  readConstructVmTarget,
  readConstructVmTargetFromRegistry,
  resolveConstructAction,
  resolveConstructT3Channel,
  CONSTRUCT_PAIRING_LINK_SCRIPT,
  constructT3LinkStorePath,
  parseConstructPairingLinkOutput,
  planConstructPairingLink,
  readConstructT3Link,
  recordConstructInstanceT3Link,
  type ConstructFileSystem,
  type ConstructJsonResponse,
} from "./ConstructUpdates.ts";

const join = (...parts: string[]) => parts.join("\\");

const INSTALLED = "dc44958114c7c43145c8f7830f6185235a1d752b";
const PROVISIONED = "b262652aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const LOCAL_APP_DATA = "C:\\Users\\alice\\AppData\\Local";
const SCRIPTS_DIR = `${LOCAL_APP_DATA}\\The-Construct\\permissionBRICK-The-Construct-main\\The-Construct-main`;

function makeFs(input: {
  readonly dirs?: Record<string, ReadonlyArray<string>>;
  readonly files?: Record<string, { mtime: number; text?: string }>;
}): ConstructFileSystem {
  const dirs = input.dirs ?? {};
  const files = input.files ?? {};
  return {
    listDirectories: (dir) => dirs[dir] ?? [],
    fileMtimeMs: (path) => files[path]?.mtime ?? null,
    readTextFile: (path) => files[path]?.text ?? null,
  };
}

function installedFs(settings: unknown, extraFiles: Record<string, { mtime: number }> = {}) {
  const container = `${LOCAL_APP_DATA}\\The-Construct`;
  const slug = `${container}\\permissionBRICK-The-Construct-main`;
  const older = `${container}\\some-fork-main\\The-Construct-main`;
  return makeFs({
    dirs: {
      [container]: [slug, `${container}\\artifacts`, `${container}\\some-fork-main`],
      [slug]: [SCRIPTS_DIR],
      [`${container}\\some-fork-main`]: [older],
      [`${container}\\artifacts`]: [`${container}\\artifacts\\t3code`],
    },
    files: {
      [`${SCRIPTS_DIR}\\Auto-Install.ps1`]: { mtime: 200 },
      [`${older}\\Auto-Install.ps1`]: { mtime: 100 },
      [`${SCRIPTS_DIR}\\.construct-settings.json`]: { mtime: 200, text: JSON.stringify(settings) },
      [`${SCRIPTS_DIR}\\Update-T3Code.ps1`]: { mtime: 200 },
      [`${SCRIPTS_DIR}\\Update-Construct.ps1`]: { mtime: 200 },
      ...extraFiles,
    },
  });
}

function fetcher(responses: Record<string, ConstructJsonResponse | null>) {
  const calls: string[] = [];
  const fetchJson = async (url: string) => {
    calls.push(url);
    return url in responses ? responses[url]! : null;
  };
  return { fetchJson, calls };
}

const COMPARE_URL = `https://api.github.com/repos/permissionBRICK/The-Construct/compare/${INSTALLED}...main`;
const NPM_LATEST_URL = "https://registry.npmjs.org/t3/latest";
const NPM_NIGHTLY_URL = "https://registry.npmjs.org/t3/nightly";

// The cast keeps this fixture valid across upstream versions: `omittedReleaseCount`
// is required on newer states and unknown on older ones.
const baseState = {
  enabled: false,
  status: "disabled",
  channel: "latest",
  currentVersion: "0.0.38-construct.bb8cb346",
  hostArch: "x64",
  appArch: "x64",
  runningUnderArm64Translation: false,
  availableVersion: null,
  downloadedVersion: null,
  releaseNotes: [],
  omittedReleaseCount: 0,
  downloadPercent: null,
  checkedAt: null,
  message: null,
  errorContext: null,
  canRetry: false,
} as DesktopUpdateState;

describe("ConstructUpdates build identity", () => {
  it("recognises Construct builds by their version suffix", () => {
    assert.isTrue(isConstructManagedBuild("0.0.38-construct.bb8cb346"));
    assert.isTrue(isConstructManagedBuild("0.0.39-nightly.20260901.1-construct.0123abcd"));
    assert.isFalse(isConstructManagedBuild("0.0.38"));
    assert.isFalse(isConstructManagedBuild("1.2.3"));
    assert.isFalse(isConstructManagedBuild("0.0.39-nightly.20260901.1"));
  });

  it("strips the suffix and resolves the channel from the base version", () => {
    assert.equal(constructT3BaseVersion("0.0.38-construct.bb8cb346"), "0.0.38");
    assert.equal(
      constructT3BaseVersion("0.0.39-nightly.20260901.1-construct.0123abcd"),
      "0.0.39-nightly.20260901.1",
    );
    assert.equal(resolveConstructT3Channel("0.0.38"), "latest");
    assert.equal(resolveConstructT3Channel("0.0.39-nightly.20260901.1"), "nightly");
    assert.equal(resolveConstructT3Channel("0.0.30-nightly.20260728"), "nightly");
  });
});

describe("ConstructUpdates markers", () => {
  it("applies the panel's defaults and normalises commits", () => {
    const markers = readConstructMarkers({
      installedCommit: ` ${INSTALLED.toUpperCase()} `,
      provisionedCommit: "not-a-sha",
    });
    assert.equal(markers.repo, "permissionBRICK/The-Construct");
    assert.equal(markers.ref, "main");
    assert.equal(markers.installedCommit, INSTALLED);
    assert.isNull(markers.provisionedCommit);
    assert.isFalse(isConstructProvisionStale(markers));
  });

  it("honours a custom repo/ref and flags a stale provision only when both commits are known", () => {
    const markers = readConstructMarkers({
      constructRepo: "alice/The-Construct",
      constructRef: "feature/x",
      installedCommit: INSTALLED,
      provisionedCommit: PROVISIONED,
    });
    assert.equal(markers.repo, "alice/The-Construct");
    assert.equal(markers.ref, "feature/x");
    assert.isTrue(isConstructProvisionStale(markers));
    assert.isFalse(
      isConstructProvisionStale(
        readConstructMarkers({ installedCommit: INSTALLED, provisionedCommit: INSTALLED }),
      ),
    );
  });

  it("rejects a repo/ref that would not survive a command line", () => {
    const markers = readConstructMarkers({ constructRepo: 'x/y" & calc', constructRef: "a b" });
    assert.equal(markers.repo, "permissionBRICK/The-Construct");
    assert.equal(markers.ref, "main");
  });
});

describe("ConstructUpdates scripts dir discovery", () => {
  it("picks the newest Auto-Install.ps1 up to two levels under The-Construct", () => {
    const fs = installedFs({});
    assert.equal(findConstructScriptsDir(LOCAL_APP_DATA, fs, join), SCRIPTS_DIR);
  });

  it("returns null without LOCALAPPDATA or without any install", () => {
    assert.isNull(findConstructScriptsDir(undefined, makeFs({}), join));
    assert.isNull(findConstructScriptsDir(LOCAL_APP_DATA, makeFs({}), join));
  });
});

describe("ConstructUpdates remote results", () => {
  it("maps the GitHub compare response like the control panel", () => {
    assert.deepEqual(constructUpdateFromCompare({ status: 200, json: { ahead_by: 3 } }), {
      available: true,
      behind: 3,
    });
    assert.deepEqual(constructUpdateFromCompare({ status: 200, json: { ahead_by: 0 } }), {
      available: false,
      behind: 0,
    });
    // The installed commit vanished upstream (history rewrite): offer the update.
    assert.deepEqual(constructUpdateFromCompare({ status: 404, json: null }), {
      available: true,
      behind: null,
    });
    assert.isNull(constructUpdateFromCompare(null));
    assert.isNull(constructUpdateFromCompare({ status: 403, json: null }));
    assert.isNull(constructUpdateFromCompare({ status: 200, json: { ahead_by: "3" } }));
  });

  it("compares upstream T3 versions per channel", () => {
    assert.isTrue(isNewerConstructT3Version("0.0.39", "0.0.38", "latest"));
    assert.isFalse(isNewerConstructT3Version("0.0.38", "0.0.38", "latest"));
    assert.isFalse(isNewerConstructT3Version("0.0.37", "0.0.38", "latest"));
    assert.isFalse(isNewerConstructT3Version(null, "0.0.38", "latest"));
    // A nightly on the registry must not flag a stable build, and vice versa.
    assert.isFalse(isNewerConstructT3Version("0.0.39-nightly.20260901.1", "0.0.38", "latest"));
    assert.isFalse(isNewerConstructT3Version("0.0.39", "0.0.39-nightly.20260901.1", "nightly"));
    // Nightlies share a semver core; the date segment decides.
    assert.isTrue(
      isNewerConstructT3Version(
        "0.0.39-nightly.20260902.1",
        "0.0.39-nightly.20260901.1",
        "nightly",
      ),
    );
    assert.isFalse(
      isNewerConstructT3Version(
        "0.0.39-nightly.20260901.1",
        "0.0.39-nightly.20260901.1",
        "nightly",
      ),
    );
  });
});

describe("ConstructUpdates action + state derivation", () => {
  it("offers Update Construct first, then a reprovision of the default instance", () => {
    assert.equal(
      resolveConstructAction({
        scriptsDir: SCRIPTS_DIR,
        constructUpdateAvailable: true,
        provisionStale: true,
        t3UpdateAvailable: true,
      }),
      "update-construct",
    );
    // A stale VM, or a newer upstream T3, keeps its proactive offer (pill, toast, About
    // button) for the DEFAULT instance -- the offline / not-yet-paired case the control
    // exists for. Every other linked VM is reprovisioned from its own Providers row.
    assert.equal(
      resolveConstructAction({
        scriptsDir: SCRIPTS_DIR,
        constructUpdateAvailable: false,
        provisionStale: true,
        t3UpdateAvailable: false,
      }),
      "reprovision",
    );
    assert.equal(
      resolveConstructAction({
        scriptsDir: SCRIPTS_DIR,
        constructUpdateAvailable: false,
        provisionStale: false,
        t3UpdateAvailable: true,
      }),
      "reprovision",
    );
    assert.isNull(
      resolveConstructAction({
        scriptsDir: SCRIPTS_DIR,
        constructUpdateAvailable: false,
        provisionStale: false,
        t3UpdateAvailable: false,
      }),
    );
    // Nothing can be launched without the scripts, whatever the checks say.
    assert.isNull(
      resolveConstructAction({
        scriptsDir: null,
        constructUpdateAvailable: true,
        provisionStale: true,
        t3UpdateAvailable: true,
      }),
    );
  });

  it("folds an available action into an `available` state the stock pill understands", () => {
    const info = deriveConstructUpdateInfo({
      scriptsDir: SCRIPTS_DIR,
      instances: [],
      target: DEFAULT_CONSTRUCT_VM_TARGET,
      markers: readConstructMarkers({ installedCommit: INSTALLED, provisionedCommit: INSTALLED }),
      compare: { available: true, behind: 3 },
      t3Version: "0.0.38",
      t3LatestVersion: "0.0.38",
      t3LatestByChannel: { latest: null, nightly: null },
      channel: "latest",
      runningAction: null,
      checkedAt: "2026-09-03T10:00:00.000Z",
      error: null,
    });
    assert.equal(info.action, "update-construct");
    assert.equal(constructAvailableVersionLabel(info), "Construct main (3 commits behind)");
    const state = applyConstructInfoToState(baseState, info);
    assert.equal(state.status, "available");
    assert.isTrue(state.enabled);
    assert.equal(state.availableVersion, "Construct main (3 commits behind)");
    assert.isNull(state.message);
    assert.equal(state.construct, info);
  });

  it("publishes per-VM staleness and keeps the default instance's reprovision offer", () => {
    const t3 = deriveConstructUpdateInfo({
      scriptsDir: SCRIPTS_DIR,
      instances: [],
      target: DEFAULT_CONSTRUCT_VM_TARGET,
      markers: readConstructMarkers({ installedCommit: INSTALLED, provisionedCommit: INSTALLED }),
      compare: { available: false, behind: 0 },
      t3Version: "0.0.38",
      t3LatestVersion: "0.0.39",
      t3LatestByChannel: { latest: null, nightly: null },
      channel: "latest",
      runningAction: null,
      checkedAt: null,
      error: null,
    });
    // A newer upstream T3 is published per instance AND still offered host-wide for the
    // default instance.
    assert.equal(t3.action, "reprovision");
    assert.isTrue(t3.t3UpdateAvailable);
    // ...and the label helper still words it for a row that is running one.
    assert.equal(
      constructAvailableVersionLabel({ ...t3, runningAction: "reprovision" }),
      "T3 Code 0.0.39",
    );

    const stale = deriveConstructUpdateInfo({
      scriptsDir: SCRIPTS_DIR,
      instances: [],
      target: DEFAULT_CONSTRUCT_VM_TARGET,
      markers: readConstructMarkers({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED }),
      compare: { available: false, behind: 0 },
      t3Version: "0.0.38",
      t3LatestVersion: "0.0.38",
      t3LatestByChannel: { latest: null, nightly: null },
      channel: "latest",
      runningAction: null,
      checkedAt: null,
      error: null,
    });
    assert.equal(stale.action, "reprovision");
    assert.isTrue(stale.provisionStale);
    assert.equal(
      constructAvailableVersionLabel({ ...stale, runningAction: "reprovision" }),
      "Construct main@dc44958",
    );
  });

  it("marks a running script as an indeterminate download and a failed check as an error", () => {
    const running = applyConstructInfoToState(baseState, {
      ...deriveConstructUpdateInfo({
        scriptsDir: SCRIPTS_DIR,
        instances: [],
        target: DEFAULT_CONSTRUCT_VM_TARGET,
        markers: readConstructMarkers({
          installedCommit: INSTALLED,
          provisionedCommit: PROVISIONED,
        }),
        compare: null,
        t3Version: "0.0.38",
        t3LatestVersion: null,
        t3LatestByChannel: { latest: null, nightly: null },
        channel: "latest",
        runningAction: "reprovision",
        checkedAt: null,
        error: null,
      }),
    });
    assert.equal(running.status, "downloading");
    assert.isNull(running.downloadPercent);

    const failed = applyConstructInfoToState(
      baseState,
      deriveConstructUpdateInfo({
        scriptsDir: SCRIPTS_DIR,
        instances: [],
        target: DEFAULT_CONSTRUCT_VM_TARGET,
        markers: readConstructMarkers({ installedCommit: INSTALLED, provisionedCommit: INSTALLED }),
        compare: null,
        t3Version: "0.0.38",
        t3LatestVersion: null,
        t3LatestByChannel: { latest: null, nightly: null },
        channel: "latest",
        runningAction: null,
        checkedAt: null,
        error: "Could not check GitHub for Construct updates.",
      }),
    );
    assert.equal(failed.status, "error");
    assert.equal(failed.errorContext, "check");
    assert.equal(failed.message, "Could not check GitHub for Construct updates.");

    const current = applyConstructInfoToState(
      baseState,
      deriveConstructUpdateInfo({
        scriptsDir: SCRIPTS_DIR,
        instances: [],
        target: DEFAULT_CONSTRUCT_VM_TARGET,
        markers: readConstructMarkers({ installedCommit: INSTALLED, provisionedCommit: INSTALLED }),
        compare: { available: false, behind: 0 },
        t3Version: "0.0.38",
        t3LatestVersion: "0.0.38",
        t3LatestByChannel: { latest: null, nightly: null },
        channel: "latest",
        runningAction: null,
        checkedAt: null,
        error: null,
      }),
    );
    assert.equal(current.status, "up-to-date");
    assert.isNull(current.availableVersion);
  });
});

describe("checkConstructUpdates", () => {
  it("reads the markers, asks GitHub + npm, and offers the Construct update first", async () => {
    const { fetchJson, calls } = fetcher({
      [COMPARE_URL]: { status: 200, json: { ahead_by: 2 } },
      [NPM_LATEST_URL]: { status: 200, json: { version: "0.0.39" } },
    });
    const info = await checkConstructUpdates({
      appVersion: "0.0.38-construct.bb8cb346",
      localAppData: LOCAL_APP_DATA,
      fs: installedFs({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED }),
      fetchJson,
      previous: null,
      runningAction: null,
      now: () => "2026-09-03T10:00:00.000Z",
      joinPath: join,
    });
    assert.deepEqual(calls, [COMPARE_URL, NPM_LATEST_URL]);
    assert.equal(info.scriptsDir, SCRIPTS_DIR);
    assert.equal(info.installedCommit, INSTALLED);
    assert.equal(info.provisionedCommit, PROVISIONED);
    assert.equal(info.behind, 2);
    assert.isTrue(info.constructUpdateAvailable);
    assert.isTrue(info.provisionStale);
    assert.equal(info.t3Version, "0.0.38");
    assert.equal(info.t3LatestVersion, "0.0.39");
    assert.isTrue(info.t3UpdateAvailable);
    assert.equal(info.action, "update-construct");
    assert.isNull(info.error);
    assert.equal(info.checkedAt, "2026-09-03T10:00:00.000Z");
  });

  it("uses the nightly registry tag for nightly builds", async () => {
    const { fetchJson, calls } = fetcher({
      [COMPARE_URL]: { status: 200, json: { ahead_by: 0 } },
      [NPM_NIGHTLY_URL]: { status: 200, json: { version: "0.0.39-nightly.20260902.1" } },
    });
    const info = await checkConstructUpdates({
      appVersion: "0.0.39-nightly.20260901.1-construct.0123abcd",
      localAppData: LOCAL_APP_DATA,
      fs: installedFs({ installedCommit: INSTALLED, provisionedCommit: INSTALLED }),
      fetchJson,
      previous: null,
      runningAction: null,
      now: () => "now",
      joinPath: join,
    });
    assert.deepEqual(calls, [COMPARE_URL, NPM_NIGHTLY_URL]);
    assert.isTrue(info.t3UpdateAvailable);
    // The fact is published for the rows, and the default instance's offer stands.
    assert.equal(info.action, "reprovision");
  });

  it("keeps the local stale-provision signal when the network is down", async () => {
    const { fetchJson } = fetcher({});
    const info = await checkConstructUpdates({
      appVersion: "0.0.38-construct.bb8cb346",
      localAppData: LOCAL_APP_DATA,
      fs: installedFs({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED }),
      fetchJson,
      previous: null,
      runningAction: null,
      now: () => "now",
      joinPath: join,
    });
    assert.isFalse(info.constructUpdateAvailable);
    assert.isTrue(info.provisionStale);
    // The local stale signal survives a dead network: the default instance's reprovision
    // is still offered, and the check errors ride along.
    assert.equal(info.action, "reprovision");
    assert.include(info.error ?? "", "GitHub");
    assert.include(info.error ?? "", "npm");
  });

  it("reports a missing Construct install instead of offering anything", async () => {
    const { fetchJson, calls } = fetcher({});
    const info = await checkConstructUpdates({
      appVersion: "0.0.38-construct.bb8cb346",
      localAppData: LOCAL_APP_DATA,
      fs: makeFs({}),
      fetchJson,
      previous: null,
      runningAction: null,
      now: () => "now",
      joinPath: join,
    });
    assert.isNull(info.scriptsDir);
    assert.isNull(info.action);
    assert.include(info.error ?? "", "not installed");
    // No installed commit -> no GitHub call; the npm lookup still runs.
    assert.deepEqual(calls, [NPM_LATEST_URL]);
    assert.equal(applyConstructInfoToState(baseState, info).status, "error");
  });

  it("re-reads the markers locally and carries the remote results over while a script runs", async () => {
    const previous: ConstructUpdateInfo = {
      repo: "permissionBRICK/The-Construct",
      ref: "main",
      scriptsDir: SCRIPTS_DIR,
      vmName: "agent-vm",
      vmHost: "agent-vm.mshome.net",
      instances: [],
      installedCommit: INSTALLED,
      provisionedCommit: PROVISIONED,
      behind: 0,
      constructUpdateAvailable: false,
      provisionStale: true,
      t3Version: "0.0.38",
      t3LatestVersion: "0.0.38",
      t3LatestByChannel: { latest: "0.0.38", nightly: null },
      t3UpdateAvailable: false,
      action: "reprovision",
      runningAction: "reprovision",
      checkedAt: "earlier",
      error: null,
    };
    // The reprovision finished: the provisioner recorded the installed commit.
    const info = await checkConstructUpdates({
      appVersion: "0.0.38-construct.bb8cb346",
      localAppData: LOCAL_APP_DATA,
      fs: installedFs({ installedCommit: INSTALLED, provisionedCommit: INSTALLED }),
      previous,
      runningAction: "reprovision",
      now: () => "now",
      joinPath: join,
    });
    assert.isFalse(info.provisionStale);
    assert.isNull(info.action);
    assert.equal(info.runningAction, "reprovision");
    assert.equal(info.t3LatestVersion, "0.0.38");
    assert.equal(info.behind, 0);
  });

  it("drops carried-over compare results once the installed commit changed", async () => {
    const previous: ConstructUpdateInfo = {
      repo: "permissionBRICK/The-Construct",
      ref: "main",
      scriptsDir: SCRIPTS_DIR,
      vmName: "agent-vm",
      vmHost: "agent-vm.mshome.net",
      instances: [],
      installedCommit: PROVISIONED,
      provisionedCommit: PROVISIONED,
      behind: 2,
      constructUpdateAvailable: true,
      provisionStale: false,
      t3Version: "0.0.38",
      t3LatestVersion: "0.0.38",
      t3LatestByChannel: { latest: "0.0.38", nightly: null },
      t3UpdateAvailable: false,
      action: "update-construct",
      runningAction: "update-construct",
      checkedAt: "earlier",
      error: null,
    };
    // Update-Construct.ps1 finished: installedCommit moved on, so the old "2 behind" no
    // longer applies; the VM is now behind the installed Construct instead.
    const info = await checkConstructUpdates({
      appVersion: "0.0.38-construct.bb8cb346",
      localAppData: LOCAL_APP_DATA,
      fs: installedFs({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED }),
      previous,
      runningAction: null,
      now: () => "now",
      joinPath: join,
    });
    assert.isFalse(info.constructUpdateAvailable);
    assert.isNull(info.behind);
    assert.isTrue(info.provisionStale);
    assert.equal(info.action, "reprovision");
  });
});

describe("constructSupportsInstanceName", () => {
  const script = `${SCRIPTS_DIR}\\Update-T3Code.ps1`;
  const withScript = (text: string | null): ConstructFileSystem => ({
    ...installedFs({}),
    readTextFile: (path) => (path === script ? text : null),
  });

  it("is false when the script cannot be read at all", () => {
    assert.isFalse(constructSupportsInstanceName(SCRIPTS_DIR, withScript(null), join));
  });

  it("is false for a script that predates the parameter", () => {
    const older = "param(\n  [string]$VmHost,\n  [string]$HostAlias\n)\n";
    assert.isFalse(constructSupportsInstanceName(SCRIPTS_DIR, withScript(older), join));
  });

  it("is true once the parameter is declared", () => {
    const newer = "param(\n  [string]$VmHost,\n  [string]$InstanceName = \"\"\n)\n";
    assert.isTrue(constructSupportsInstanceName(SCRIPTS_DIR, withScript(newer), join));
  });

  it("ignores a mention in a comment or a doc block", () => {
    const commented =
      "<#\n  .PARAMETER InstanceName\n#>\nparam(\n  # $InstanceName is not declared here\n  [string]$VmHost\n)\n";
    assert.isFalse(constructSupportsInstanceName(SCRIPTS_DIR, withScript(commented), join));
  });
});

describe("planConstructLaunch", () => {
  const info = { scriptsDir: SCRIPTS_DIR, repo: "permissionBRICK/The-Construct", ref: "main" };

  it("runs Update-T3Code.ps1 in a new console through cmd start /wait", () => {
    const result = planConstructLaunch(
      "reprovision",
      info,
      DEFAULT_CONSTRUCT_VM_TARGET,
      "win32",
      installedFs({}),
      join,
    );
    assert.isTrue(result.ok);
    if (!result.ok) return;
    assert.equal(result.plan.command, "cmd.exe");
    assert.equal(result.plan.scriptPath, `${SCRIPTS_DIR}\\Update-T3Code.ps1`);
    assert.deepEqual(result.plan.args, [
      "/d",
      "/s",
      "/c",
      `"start "Construct reprovision" /wait powershell.exe -NoProfile -ExecutionPolicy Bypass -File "${SCRIPTS_DIR}\\Update-T3Code.ps1""`,
    ]);
  });

  it("passes the tracked repo/ref to Update-Construct.ps1", () => {
    const result = planConstructLaunch(
      "update-construct",
      { ...info, repo: "alice/The-Construct", ref: "feature/x" },
      DEFAULT_CONSTRUCT_VM_TARGET,
      "win32",
      installedFs({}),
      join,
    );
    assert.isTrue(result.ok);
    if (!result.ok) return;
    assert.include(result.plan.args[3], `-File "${SCRIPTS_DIR}\\Update-Construct.ps1"`);
    assert.include(result.plan.args[3], '-Repo "alice/The-Construct" -Ref "feature/x"');
    // With a result path the script closes its console by itself (no "Press Enter").
    assert.match(result.plan.env?.CONSTRUCT_UPDATE_RESULT ?? "", /construct-update-\d+\.result$/);
  });

  it("gives the reprovision no result path (the provisioner owns its own pause rules)", () => {
    const result = planConstructLaunch(
      "reprovision",
      info,
      DEFAULT_CONSTRUCT_VM_TARGET,
      "win32",
      installedFs({}),
      join,
    );
    assert.isTrue(result.ok);
    if (!result.ok) return;
    assert.isUndefined(result.plan.env);
  });

  it("refuses off-Windows, missing scripts, and hostile paths", () => {
    assert.isFalse(
      planConstructLaunch(
        "reprovision",
        info,
        DEFAULT_CONSTRUCT_VM_TARGET,
        "darwin",
        installedFs({}),
        join,
      ).ok,
    );
    assert.isFalse(
      planConstructLaunch(
        "reprovision",
        { ...info, scriptsDir: null },
        DEFAULT_CONSTRUCT_VM_TARGET,
        "win32",
        installedFs({}),
        join,
      ).ok,
    );
    const withoutScript = installedFs({});
    const noReprovision: ConstructFileSystem = {
      ...withoutScript,
      fileMtimeMs: (path) =>
        path.endsWith("Update-T3Code.ps1") ? null : withoutScript.fileMtimeMs(path),
    };
    const missing = planConstructLaunch(
      "reprovision",
      info,
      DEFAULT_CONSTRUCT_VM_TARGET,
      "win32",
      noReprovision,
      join,
    );
    assert.isFalse(missing.ok);
    if (!missing.ok) assert.include(missing.error, "Update-T3Code.ps1 was not found");

    const hostileDir = `${LOCAL_APP_DATA}\\The-Construct\\x" & calc "`;
    const hostile = planConstructLaunch(
      "reprovision",
      { ...info, scriptsDir: hostileDir },
      DEFAULT_CONSTRUCT_VM_TARGET,
      "win32",
      { ...withoutScript, fileMtimeMs: () => 1 },
      join,
    );
    assert.isFalse(hostile.ok);
  });
});

describe("ConstructUpdates markers on disk", () => {
  it("strips the UTF-8 BOM Windows PowerShell writes", () => {
    const fs = installedFs({});
    const bomFs: ConstructFileSystem = {
      ...fs,
      readTextFile: (path) =>
        path.endsWith(".construct-settings.json")
          ? "\uFEFF" +
            JSON.stringify({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED })
          : fs.readTextFile(path),
    };
    const markers = readConstructMarkersFromDir(SCRIPTS_DIR, bomFs, join);
    assert.equal(markers.installedCommit, INSTALLED);
    assert.equal(markers.provisionedCommit, PROVISIONED);
  });

  it("treats an unreadable or malformed settings file as unknown markers", () => {
    const fs = installedFs({});
    const broken: ConstructFileSystem = { ...fs, readTextFile: () => "{ not json" };
    assert.isNull(readConstructMarkersFromDir(SCRIPTS_DIR, broken, join).installedCommit);
    const missing: ConstructFileSystem = { ...fs, readTextFile: () => null };
    assert.isNull(readConstructMarkersFromDir(SCRIPTS_DIR, missing, join).installedCommit);
  });

  // ── B12: provisionedCommit is PER VM ──────────────────────────────────────
  // installedCommit / repo / ref describe the INSTALL (one checkout, one answer);
  // provisionedCommit describes ONE VM. The default instance keeps both at the legacy top
  // level of .construct-settings.json, so nothing changes for a single-VM PC; every other
  // instance keeps its own under `The-Construct\instances\<name>.json`.
  const STATE_DIR = `${LOCAL_APP_DATA}\\The-Construct\\instances`;
  const OTHER_PROVISIONED = "0e786a3cccccccccccccccccccccccccccccccc0";

  it("resolves the per-instance state path only for a non-default instance", () => {
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "agent-vm", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "", join));
    assert.isNull(constructInstanceStatePath(undefined, "work-vm", join));
    assert.equal(constructInstanceStatePath(LOCAL_APP_DATA, "work-vm", join), `${STATE_DIR}\\work-vm.json`);
  });

  it("holds the name to THE ONE name rule, case included", () => {
    // "Agent-VM" is not a valid instance name at all, so it is refused rather than
    // silently treated as the default — the same call instancestate.js and
    // Test-ConstructDefaultInstanceStore make.
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "Agent-VM", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "Work-VM", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "work_vm", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "work-", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "construct-work", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "a".repeat(64), join));
    assert.isNotNull(constructInstanceStatePath(LOCAL_APP_DATA, "a".repeat(63), join));
  });

  it("refuses a name that would escape the instances directory", () => {
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "../evil", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "a\\b", join));
    assert.isNull(constructInstanceStatePath(LOCAL_APP_DATA, "a..b", join));
  });

  it("reads a non-default instance's provisionedCommit from its own file", () => {
    const base = installedFs({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED });
    const withState: ConstructFileSystem = {
      ...base,
      readTextFile: (path) =>
        path === `${STATE_DIR}\\work-vm.json`
          ? JSON.stringify({ version: 1, instance: "work-vm", provisionedCommit: OTHER_PROVISIONED })
          : base.readTextFile(path),
    };
    const markers = readConstructMarkersFromDir(SCRIPTS_DIR, withState, join, {
      localAppData: LOCAL_APP_DATA,
      instanceName: "work-vm",
    });
    assert.equal(markers.installedCommit, INSTALLED, "installedCommit stays install-wide");
    assert.equal(markers.provisionedCommit, OTHER_PROVISIONED);
  });

  it("does NOT let a non-default instance inherit the legacy top-level marker", () => {
    const base = installedFs({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED });
    const markers = readConstructMarkersFromDir(SCRIPTS_DIR, base, join, {
      localAppData: LOCAL_APP_DATA,
      instanceName: "work-vm",
    });
    assert.equal(markers.installedCommit, INSTALLED);
    assert.isNull(markers.provisionedCommit, "an instance with no state file is unknown, not the default VM's");
  });

  it("keeps the default instance on the legacy single file", () => {
    const base = installedFs({ installedCommit: INSTALLED, provisionedCommit: PROVISIONED });
    const markers = readConstructMarkersFromDir(SCRIPTS_DIR, base, join, {
      localAppData: LOCAL_APP_DATA,
      instanceName: "agent-vm",
    });
    assert.equal(markers.provisionedCommit, PROVISIONED);
    // ...and identical to the call with no options at all (an older caller).
    assert.deepEqual(markers, readConstructMarkersFromDir(SCRIPTS_DIR, base, join));
  });

  it("never lets a per-instance file shadow the installed commit", () => {
    const base = installedFs({ installedCommit: INSTALLED, constructRepo: "permissionBRICK/The-Construct", constructRef: "main" });
    const hostile: ConstructFileSystem = {
      ...base,
      readTextFile: (path) =>
        path === `${STATE_DIR}\\work-vm.json`
          ? JSON.stringify({
              installedCommit: OTHER_PROVISIONED,
              constructRepo: "evil/repo",
              constructRef: "evil",
              provisionedCommit: OTHER_PROVISIONED,
            })
          : base.readTextFile(path),
    };
    const markers = readConstructMarkersFromDir(SCRIPTS_DIR, hostile, join, {
      localAppData: LOCAL_APP_DATA,
      instanceName: "work-vm",
    });
    assert.equal(markers.installedCommit, INSTALLED);
    assert.equal(markers.repo, "permissionBRICK/The-Construct");
    assert.equal(markers.ref, "main");
    assert.equal(markers.provisionedCommit, OTHER_PROVISIONED);
  });

  it("judges a non-default target's staleness from its own marker", () => {
    const stale = readConstructMarkers(
      { installedCommit: INSTALLED },
      { provisionedCommit: OTHER_PROVISIONED },
    );
    assert.isTrue(isConstructProvisionStale(stale));
    const fresh = readConstructMarkers({ installedCommit: INSTALLED }, { provisionedCommit: INSTALLED });
    assert.isFalse(isConstructProvisionStale(fresh));
  });
});

describe("ConstructUpdates target VM (instances.json)", () => {
  const registryPath = `${LOCAL_APP_DATA}\\The-Construct\\instances.json`;
  function fsWithRegistry(text: string | null, base = installedFs({})): ConstructFileSystem {
    return {
      ...base,
      readTextFile: (path) => (path === registryPath ? text : base.readTextFile(path)),
    };
  }

  it("is the implicit default without a registry, an empty one, or the plain agent-vm entry", () => {
    assert.deepEqual(
      readConstructVmTargetFromRegistry(LOCAL_APP_DATA, fsWithRegistry(null), join),
      DEFAULT_CONSTRUCT_VM_TARGET,
    );
    assert.deepEqual(
      readConstructVmTargetFromRegistry(LOCAL_APP_DATA, fsWithRegistry("\uFEFF  "), join),
      DEFAULT_CONSTRUCT_VM_TARGET,
    );
    assert.deepEqual(
      readConstructVmTargetFromRegistry(undefined, fsWithRegistry("{}"), join),
      DEFAULT_CONSTRUCT_VM_TARGET,
    );
    const plain = readConstructVmTarget({
      version: 1,
      defaultInstance: "agent-vm",
      instances: {
        "agent-vm": { backend: "hyperv-local", sshHost: "agent-vm.mshome.net", sshPort: 22 },
      },
    });
    assert.isTrue(plain.isDefault);
    assert.isNull(plain.problem);
    assert.deepEqual(constructReprovisionIdentityArgs(plain), []);
  });

  it("derives a non-default default instance like instances.js and passes its identity", () => {
    const target = readConstructVmTarget({
      version: 1,
      defaultInstance: "work",
      instances: {
        "agent-vm": {},
        work: {
          backend: "hyperv-remote",
          sshHost: "10.0.0.7",
          sshPort: "2222",
          scriptsDir: SCRIPTS_DIR,
        },
      },
    });
    assert.isFalse(target.isDefault);
    assert.isNull(target.problem);
    assert.equal(target.name, "work");
    assert.equal(target.vmHost, "10.0.0.7");
    assert.equal(target.hostAlias, "work");
    assert.equal(target.sshPort, 2222);
    assert.equal(target.keyName, "construct_work_ed25519");
    assert.equal(target.scriptsDir, SCRIPTS_DIR);
    assert.deepEqual(constructReprovisionIdentityArgs(target), [
      "-VmHost",
      '"10.0.0.7"',
      "-HostAlias",
      '"work"',
      "-SshPort",
      "2222",
      "-LocalKeyName",
      '"construct_work_ed25519"',
    ]);
    // NAME-ONLY TARGETING (B11, plan §4.12): once the installed Update-T3Code.ps1
    // declares -InstanceName it resolves all four out of the same registry this module
    // parsed, so one argument replaces them.
    assert.deepEqual(constructReprovisionIdentityArgs(target, true), ["-InstanceName", '"work"']);
    // The implicit default VM still forwards NOTHING, either way — that is the
    // zero-change path, and an older provisioner keeps working on it.
    assert.deepEqual(constructReprovisionIdentityArgs(DEFAULT_CONSTRUCT_VM_TARGET, true), []);
    // A local instance with its canonical identity (derived when absent) is accepted.
    const local = readConstructVmTarget({
      version: 1,
      defaultInstance: "lab",
      instances: { lab: {} },
    });
    assert.isNull(local.problem);
    assert.equal(local.vmHost, "lab.mshome.net");
    assert.equal(local.hostAlias, "lab");
    assert.isFalse(local.isDefault);
    // A default instance with an explicit but default-equal identity still counts as default.
    const explicit = readConstructVmTarget({
      version: 1,
      instances: {
        "agent-vm": {
          sshHost: "agent-vm.mshome.net",
          hostAlias: "agent-vm",
          keyName: "agent_vm_ed25519",
          sshPort: 22,
        },
      },
    });
    assert.isTrue(explicit.isDefault);
    assert.isNull(explicit.problem);
  });

  it("refuses what the panel's registry rules refuse, naming the reason", () => {
    const problemOf = (doc: unknown) => readConstructVmTarget(doc).problem ?? "";
    // The panel resolves a missing default entry to agent-vm; only the implicit default
    // may be acted on silently.
    assert.equal(readConstructVmTarget({ version: 1, defaultInstance: "agent-vm" }).problem, null);
    assert.include(
      problemOf({ version: 1, defaultInstance: "lab", instances: {} }),
      "no such entry",
    );
    assert.include(
      problemOf({ defaultInstance: "construct-lab", instances: { "construct-lab": {} } }),
      "invalid default instance",
    );
    // Local Hyper-V instances must carry their canonical identity.
    assert.include(
      problemOf({ defaultInstance: "lab", instances: { lab: { sshHost: "other.example" } } }),
      "local Hyper-V instance",
    );
    assert.include(
      problemOf({ defaultInstance: "lab", instances: { lab: { sshPort: 2200 } } }),
      "sshPort 2200",
    );
    assert.include(
      problemOf({ defaultInstance: "agent-vm", instances: { "agent-vm": { hostAlias: "vm" } } }),
      "hostAlias",
    );
    // Remote instances must state their endpoint and keep vmName == name.
    assert.include(
      problemOf({ defaultInstance: "work", instances: { work: { backend: "hyperv-remote" } } }),
      "without an sshHost",
    );
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: "10.0.0.7", vmName: "Other" } },
      }),
      "vmName",
    );
    assert.include(
      problemOf({ defaultInstance: "work", instances: { work: { backend: "vmware" } } }),
      "unknown backend",
    );
    // Format rules.
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: 'x" & calc' } },
      }),
      "unusable sshHost",
    );
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: "10.0.0.7", hostAlias: "a b" } },
      }),
      "unusable hostAlias",
    );
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: "10.0.0.7", keyName: "../x" } },
      }),
      "unusable keyName",
    );
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: "10.0.0.7", keyName: "CON.key" } },
      }),
      "unusable keyName",
    );
    // Collisions with another entry make the registry ambiguous.
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: {
          work: { backend: "hyperv-remote", sshHost: "10.0.0.7" },
          other: { backend: "hyperv-remote", sshHost: "10.0.0.7" },
        },
      }),
      "share the endpoint",
    );
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: {
          work: { backend: "hyperv-remote", sshHost: "10.0.0.7" },
          other: { backend: "hyperv-remote", sshHost: "10.0.0.8", hostAlias: "WORK" },
        },
      }),
      "share the ssh alias",
    );
    // Typed-field and backend rules (instances.js badString / backendProblems).
    assert.include(
      problemOf({ defaultInstance: "work", instances: { work: { backend: 42 } } }),
      '"backend"',
    );
    assert.include(
      problemOf({ defaultInstance: "work", instances: { work: { backend: "" } } }),
      '"backend"',
    );
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: { work: { backend: "Hyperv-Remote", sshHost: "10.0.0.7" } },
      }),
      "not spelled",
    );
    assert.include(
      problemOf({ defaultInstance: "work", instances: { work: { sshHost: 7 } } }),
      '"sshHost" that is not a string',
    );
    assert.include(
      problemOf({ defaultInstance: "work", instances: { work: { scriptsDir: false } } }),
      '"scriptsDir"',
    );
    // Host rule parity: no zone ids, brackets or trailing dots; strict IPv4-mapped tails.
    for (const host of [
      "fe80::1%eth0",
      "[::1]",
      "host.example.",
      "::ffff:1.2.3.04",
      "1::2::3",
      "-bad.example",
    ]) {
      assert.include(
        problemOf({
          defaultInstance: "work",
          instances: { work: { backend: "hyperv-remote", sshHost: host } },
        }),
        "unusable sshHost",
        host,
      );
    }
    for (const host of ["10.0.0.7", "vm.corp.example", "2001:db8::7", "::ffff:10.0.0.7"]) {
      assert.isNull(
        readConstructVmTarget({
          defaultInstance: "work",
          instances: { work: { backend: "hyperv-remote", sshHost: host } },
        }).problem,
        host,
      );
    }
    // Alias tokens must not contain "..", key names must not be Windows device stems.
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: "10.0.0.7", hostAlias: "a..b" } },
      }),
      "unusable hostAlias",
    );
    // An unusable sshPort is silently the default, as in the panel.
    assert.equal(
      readConstructVmTarget({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: "10.0.0.7", sshPort: "70000" } },
      }).sshPort,
      22,
    );
    // A rejected sibling is skipped, as in the panel: it neither collides nor blocks.
    assert.isNull(
      readConstructVmTarget({
        defaultInstance: "work",
        instances: {
          work: { backend: "hyperv-remote", sshHost: "10.0.0.7" },
          broken: { backend: 42, sshHost: "10.0.0.7" },
          alias: {
            backend: "hyperv-remote",
            sshHost: "10.0.0.9",
            hostAlias: "work",
            keyName: "CON",
          },
        },
      }).problem,
    );
    // A backend this build does not know is kept for collision detection, like the panel
    // keeps it — it only cannot be the reprovision target itself.
    assert.include(
      problemOf({
        defaultInstance: "work",
        instances: {
          work: { backend: "hyperv-remote", sshHost: "10.0.0.7" },
          legacy: { backend: "proxmox", sshHost: "10.0.0.7" },
        },
      }),
      "share the endpoint",
    );
    assert.include(
      problemOf({
        defaultInstance: "legacy",
        instances: { legacy: { backend: "proxmox", sshHost: "10.0.0.7" } },
      }),
      "unknown backend",
    );
    // IPv6 endpoints pass the same host rule the panel applies.
    assert.isNull(
      readConstructVmTarget({
        defaultInstance: "work",
        instances: { work: { backend: "hyperv-remote", sshHost: "2001:db8::7" } },
      }).problem,
    );
  });

  it("falls back to the default VM and says why when the registry is unusable", () => {
    const badJson = readConstructVmTargetFromRegistry(
      LOCAL_APP_DATA,
      fsWithRegistry("{ nope"),
      join,
    );
    assert.isTrue(badJson.isDefault);
    assert.include(badJson.problem ?? "", "not valid JSON");
    assert.include(readConstructVmTarget([]).problem ?? "", "not a JSON object");
    assert.include(readConstructVmTarget({ version: 2 }).problem ?? "", "version 2");
    assert.include(
      readConstructVmTarget({ defaultInstance: "Bad Name" }).problem ?? "",
      "invalid default instance",
    );
  });

  it("routes the check through the pinned scripts dir and refuses to reprovision an ambiguous target", async () => {
    const pinned = `${LOCAL_APP_DATA}\\Checkouts\\construct`;
    const base = installedFs({ installedCommit: INSTALLED, provisionedCommit: INSTALLED });
    const files: Record<string, string> = {
      [`${pinned}\\.construct-settings.json`]: JSON.stringify({
        installedCommit: PROVISIONED,
        provisionedCommit: PROVISIONED,
      }),
      [registryPath]: JSON.stringify({
        version: 1,
        defaultInstance: "work",
        instances: { work: { scriptsDir: pinned } },
      }),
    };
    const fs: ConstructFileSystem = {
      listDirectories: base.listDirectories,
      fileMtimeMs: (path) =>
        path === `${pinned}\\Auto-Install.ps1` || path === `${pinned}\\Update-T3Code.ps1`
          ? 5
          : base.fileMtimeMs(path),
      readTextFile: (path) => files[path] ?? base.readTextFile(path),
    };
    const { fetchJson } = fetcher({
      [`https://api.github.com/repos/permissionBRICK/The-Construct/compare/${PROVISIONED}...main`]:
        { status: 200, json: { ahead_by: 0 } },
      [NPM_LATEST_URL]: { status: 200, json: { version: "0.0.38" } },
    });
    const info = await checkConstructUpdates({
      appVersion: "0.0.38-construct.bb8cb346",
      localAppData: LOCAL_APP_DATA,
      fs,
      fetchJson,
      previous: null,
      runningAction: null,
      now: () => "now",
      joinPath: join,
    });
    assert.equal(info.scriptsDir, pinned);
    assert.equal(info.installedCommit, PROVISIONED);
    assert.equal(info.vmName, "work");
    assert.equal(info.vmHost, "work.mshome.net");
    assert.isNull(info.error);

    const target = readConstructVmTargetFromRegistry(LOCAL_APP_DATA, fs, join);
    const plan = planConstructLaunch("reprovision", { ...info }, target, "win32", fs, join);
    assert.isTrue(plan.ok);
    if (plan.ok) {
      assert.include(
        plan.plan.args[3],
        `-File "${pinned}\\Update-T3Code.ps1" -VmHost "work.mshome.net" -HostAlias "work" -SshPort 22 -LocalKeyName "construct_work_ed25519"`,
      );
    }
    // ...and with a name-capable Update-T3Code.ps1 the same launch carries ONE argument.
    const nameCapable: ConstructFileSystem = {
      ...fs,
      readTextFile: (path) =>
        path === `${pinned}\\Update-T3Code.ps1`
          ? 'param(\n  [string]$VmHost,\n  [string]$InstanceName = ""\n)\n'
          : fs.readTextFile(path),
    };
    const named = planConstructLaunch("reprovision", { ...info }, target, "win32", nameCapable, join);
    assert.isTrue(named.ok);
    if (named.ok) {
      assert.include(named.plan.args[3], `-File "${pinned}\\Update-T3Code.ps1" -InstanceName "work"`);
      assert.notInclude(named.plan.args[3], "-VmHost");
    }
    // An unusable registry is surfaced by the check and blocks the reprovision launch.
    const ambiguous = {
      ...DEFAULT_CONSTRUCT_VM_TARGET,
      problem: "instances.json is not valid JSON; using the default VM.",
    };
    const refused = planConstructLaunch(
      "reprovision",
      { ...info, scriptsDir: SCRIPTS_DIR },
      ambiguous,
      "win32",
      installedFs({}),
      join,
    );
    assert.isFalse(refused.ok);
    if (!refused.ok) assert.include(refused.error, "instances.json");
    // Update-Construct.ps1 only touches this PC, so the registry does not gate it.
    const update = planConstructLaunch(
      "update-construct",
      { ...info, scriptsDir: SCRIPTS_DIR },
      ambiguous,
      "win32",
      installedFs({}),
      join,
    );
    assert.isTrue(update.ok);
  });
});

// ── B14: every instance this PC manages ──────────────────────────────────────

const MULTI_REGISTRY = {
  version: 1,
  defaultInstance: "agent-vm",
  instances: {
    "agent-vm": {},
    "work-vm": { backend: "hyperv-local" },
    "far-vm": {
      backend: "hyperv-remote",
      vmName: "far-vm",
      sshHost: "buildbox.example.local",
      publicHost: "far-vm.vpn.example.local",
      service: { url: "https://buildbox.example.local:7462" },
    },
  },
};

describe("readConstructInstances", () => {
  it("reads every accepted entry, sorted, with the default marked", () => {
    const view = readConstructInstances(MULTI_REGISTRY);
    assert.isNull(view.problem);
    assert.deepEqual(
      view.instances.map((i) => i.name),
      ["agent-vm", "far-vm", "work-vm"],
    );
    assert.equal(view.defaultName, "agent-vm");
    assert.isTrue(view.instances.find((i) => i.name === "agent-vm")!.isDefault);
    assert.isFalse(view.instances.find((i) => i.name === "work-vm")!.isDefault);
    assert.equal(view.instances.find((i) => i.name === "work-vm")!.vmHost, "work-vm.mshome.net");
    assert.equal(
      view.instances.find((i) => i.name === "far-vm")!.publicHost,
      "far-vm.vpn.example.local",
    );
    // A LOCAL entry never carries a publicHost, whatever the file says.
    assert.isNull(view.instances.find((i) => i.name === "work-vm")!.publicHost);
  });

  it("synthesizes agent-vm — an absent registry IS that one instance", () => {
    for (const view of [
      readConstructInstances(null),
      readConstructInstances({ version: 1, instances: {} }),
      readConstructInstancesFromRegistry(LOCAL_APP_DATA, makeFs({ files: {} }), join),
      readConstructInstancesFromRegistry(undefined, makeFs({ files: {} }), join),
    ]) {
      assert.deepEqual(
        view.instances.map((i) => i.name),
        ["agent-vm"],
      );
      assert.isTrue(view.instances[0]!.isDefault);
      assert.equal(view.instances[0]!.vmHost, "agent-vm.mshome.net");
      assert.isNull(view.problem);
    }
  });

  it("keeps the implicit default beside the named instances", () => {
    const view = readConstructInstances({ version: 1, instances: { "work-vm": {} } });
    assert.deepEqual(
      view.instances.map((i) => i.name),
      ["agent-vm", "work-vm"],
    );
  });

  it("the TARGET reader honours the removal too, and picks the same survivor", () => {
    // Both registry readers move the default to the alphabetically first survivor when
    // it was removed; the target reader must agree, or the host-wide scripts would be
    // aimed at the very VM the removal took away.
    const target = readConstructVmTarget({
      version: 1,
      defaultInstance: "agent-vm",
      instances: { "agent-vm": null, "work-vm": { backend: "hyperv-local" } },
    });
    assert.isNull(target.problem);
    assert.equal(target.name, "work-vm");
    assert.equal(target.vmHost, "work-vm.mshome.net");
    assert.isFalse(target.isDefault);
    // Nothing left at all is a problem, not a silent fallback to the removed VM.
    const empty = readConstructVmTarget({
      version: 1,
      defaultInstance: "agent-vm",
      instances: { "agent-vm": null },
    });
    assert.include(empty.problem ?? "", "removed");
  });

  it("normalizes a default that is not there to the same survivor as the other readers", () => {
    const view = readConstructInstances({
      version: 1,
      defaultInstance: "agent-vm",
      instances: { "agent-vm": null, "work-vm": { backend: "hyperv-local" }, "zz-vm": {} },
    });
    assert.equal(view.defaultName, "work-vm");
    assert.isTrue(view.instances.find((i) => i.name === "work-vm")!.isDefault);
    assert.equal(
      readConstructVmTarget({
        version: 1,
        defaultInstance: "agent-vm",
        instances: { "agent-vm": null, "work-vm": { backend: "hyperv-local" }, "zz-vm": {} },
      }).name,
      "work-vm",
    );
  });

  it("gives the legacy mirror to agent-vm, never to whichever VM is default now", () => {
    // .construct-settings.json is the DEFAULT STORE, fixed to `agent-vm` BY NAME (B12).
    // Making another VM the registry default must not hand it agent-vm's state.
    const rows = collectConstructInstances(
      readConstructInstances({
        version: 1,
        defaultInstance: "work-vm",
        instances: { "agent-vm": {}, "work-vm": { backend: "hyperv-local" } },
      }),
      LOCAL_APP_DATA,
      makeFs({
        files: {
          [`${SCRIPTS_DIR}\\.construct-settings.json`]: {
            mtime: 1,
            text: JSON.stringify({ provisionedCommit: PROVISIONED, t3codeChannel: "nightly" }),
          },
        },
      }),
      SCRIPTS_DIR,
      join,
    );
    const agent = rows.find((r) => r.name === "agent-vm")!;
    const work = rows.find((r) => r.name === "work-vm")!;
    assert.equal(agent.provisionedCommit, PROVISIONED);
    assert.equal(agent.channel, "nightly");
    assert.isFalse(agent.isDefault);
    assert.isNull(work.provisionedCommit);
    assert.isNull(work.channel);
    assert.isTrue(work.isDefault);
  });

  it("honours an explicit removal of the synthesized default", () => {
    // "Remove instance" writes `null` for a row a reader would otherwise invent back
    // (extension/src/instances.js parseRegistry, lib/AgentVm.Instances.ps1). Without
    // this the Providers page would keep offering a VM whose client state was deleted.
    const view = readConstructInstances({
      version: 1,
      defaultInstance: "work-vm",
      instances: { "work-vm": { backend: "hyperv-local" }, "agent-vm": null },
    });
    assert.deepEqual(
      view.instances.map((i) => i.name),
      ["work-vm"],
    );
    assert.isNull(view.problem);
  });

  it("skips a rejected entry instead of guessing, and keeps the rest", () => {
    const view = readConstructInstances({
      version: 1,
      instances: {
        "work-vm": { backend: "hyperv-local", sshHost: "somewhere.else" }, // not canonical
        "good-vm": { backend: "hyperv-local" },
      },
    });
    assert.deepEqual(
      view.instances.map((i) => i.name),
      ["agent-vm", "good-vm"],
    );
  });

  it("ignores a document of another schema version whole, and says why", () => {
    const view = readConstructInstances({ version: 2, instances: { "work-vm": {} } });
    assert.deepEqual(
      view.instances.map((i) => i.name),
      ["agent-vm"],
    );
    assert.include(view.problem ?? "", "version 2");
  });

  it("reads the file when there is one", () => {
    const path = `${LOCAL_APP_DATA}\\The-Construct\\instances.json`;
    const fs = makeFs({ files: { [path]: { mtime: 1, text: JSON.stringify(MULTI_REGISTRY) } } });
    assert.lengthOf(readConstructInstancesFromRegistry(LOCAL_APP_DATA, fs, join).instances, 3);
  });
});

describe("readConstructInstanceState", () => {
  const statePath = `${LOCAL_APP_DATA}\\The-Construct\\instances\\work-vm.json`;
  const settingsPath = `${SCRIPTS_DIR}\\.construct-settings.json`;

  it("reads a NAMED instance's own state file", () => {
    const fs = makeFs({
      files: {
        [statePath]: {
          mtime: 1,
          text: JSON.stringify({
            provisionedCommit: PROVISIONED,
            t3codeChannel: "nightly",
            t3Port: 5178,
          }),
        },
      },
    });
    const state = readConstructInstanceState(LOCAL_APP_DATA, "work-vm", SCRIPTS_DIR, fs, join);
    assert.equal(state.provisionedCommit, PROVISIONED);
    assert.equal(state.channel, "nightly");
    assert.equal(state.t3Port, 5178);
  });

  it("reads the DEFAULT instance from the legacy top level, where B12 keeps it", () => {
    const fs = makeFs({
      files: {
        [settingsPath]: {
          mtime: 1,
          text: JSON.stringify({
            installedCommit: INSTALLED,
            provisionedCommit: PROVISIONED,
            t3codeChannel: "stable",
            t3Port: 5177,
          }),
        },
      },
    });
    const state = readConstructInstanceState(LOCAL_APP_DATA, "agent-vm", SCRIPTS_DIR, fs, join);
    assert.equal(state.provisionedCommit, PROVISIONED);
    assert.equal(state.channel, "latest");
    assert.equal(state.t3Port, 5177);
    assert.equal(state.installedCommit, INSTALLED);
  });

  it("maps the VM's channel spelling to the npm dist-tag", () => {
    const withChannel = (value: unknown) =>
      readConstructInstanceState(
        LOCAL_APP_DATA,
        "work-vm",
        SCRIPTS_DIR,
        makeFs({ files: { [statePath]: { mtime: 1, text: JSON.stringify({ t3codeChannel: value }) } } }),
        join,
      ).channel;
    assert.equal(withChannel("stable"), "latest");
    assert.equal(withChannel("nightly"), "nightly");
    assert.isNull(withChannel("something-else"));
    assert.isNull(withChannel(7));
  });

  it("answers empty for a missing file, a bad commit and an invalid name", () => {
    const fs = makeFs({
      files: { [statePath]: { mtime: 1, text: JSON.stringify({ provisionedCommit: "nope" }) } },
    });
    assert.isNull(readConstructInstanceState(LOCAL_APP_DATA, "work-vm", SCRIPTS_DIR, fs, join).provisionedCommit);
    assert.isNull(readConstructInstanceState(LOCAL_APP_DATA, "other-vm", SCRIPTS_DIR, fs, join).provisionedCommit);
    // An unusable name has no state FILE, so it falls back to the settings file — which
    // is empty here. It never reads a path built from that name.
    assert.isNull(readConstructInstanceState(LOCAL_APP_DATA, "../evil", SCRIPTS_DIR, fs, join).provisionedCommit);
    assert.isNull(readConstructInstanceState(undefined, "work-vm", SCRIPTS_DIR, fs, join).provisionedCommit);
    assert.isNull(readConstructInstanceState(LOCAL_APP_DATA, "work-vm", null, fs, join).provisionedCommit);
  });
});

describe("collectConstructInstances", () => {
  const statePath = (name: string) =>
    `${LOCAL_APP_DATA}\\The-Construct\\instances\\${name}.json`;
  const settingsPath = `${SCRIPTS_DIR}\\.construct-settings.json`;

  it("gives every instance its OWN commit, channel and T3 port", () => {
    const fs = makeFs({
      files: {
        // The DEFAULT instance's VM-scoped half: the legacy top level (B12).
        [settingsPath]: {
          mtime: 1,
          text: JSON.stringify({
            installedCommit: INSTALLED,
            provisionedCommit: INSTALLED,
            t3codeChannel: "stable",
            t3Port: 5178,
          }),
        },
        [statePath("work-vm")]: {
          mtime: 1,
          text: JSON.stringify({
            provisionedCommit: PROVISIONED,
            t3codeChannel: "nightly",
            t3Port: 23011,
          }),
        },
      },
    });
    const rows = collectConstructInstances(
      readConstructInstances(MULTI_REGISTRY),
      LOCAL_APP_DATA,
      fs,
      SCRIPTS_DIR,
      join,
    );
    const byName = Object.fromEntries(rows.map((r) => [r.name, r]));
    assert.equal(byName["work-vm"]!.provisionedCommit, PROVISIONED);
    assert.equal(byName["work-vm"]!.channel, "nightly");
    assert.equal(byName["work-vm"]!.t3Port, 23011);
    assert.equal(byName["agent-vm"]!.provisionedCommit, INSTALLED);
    assert.equal(byName["agent-vm"]!.channel, "latest");
    assert.equal(byName["agent-vm"]!.t3Port, 5178);
    assert.isTrue(byName["agent-vm"]!.isDefault);
    // A non-default instance with no state file says "unknown" rather than borrowing it.
    assert.isNull(byName["far-vm"]!.provisionedCommit);
    assert.isNull(byName["far-vm"]!.t3Port);
    assert.equal(byName["far-vm"]!.publicHost, "far-vm.vpn.example.local");
  });
});

describe("planConstructInstanceReprovision", () => {
  const view = readConstructInstances(MULTI_REGISTRY);
  const scriptsFs = (nameCapable: boolean): ConstructFileSystem => {
    const base = installedFs({});
    return {
      ...base,
      readTextFile: (path) =>
        path === `${SCRIPTS_DIR}\\Update-T3Code.ps1`
          ? nameCapable
            ? 'param(\n  [string]$VmHost,\n  [string]$InstanceName = ""\n)\n'
            : "param(\n  [string]$VmHost\n)\n"
          : base.readTextFile(path),
    };
  };
  const info = { scriptsDir: SCRIPTS_DIR, repo: "permissionBRICK/The-Construct", ref: "main" };

  it("targets the named instance", () => {
    const plan = planConstructInstanceReprovision(
      "work-vm",
      view,
      info,
      "win32",
      scriptsFs(true),
      join,
    );
    assert.isTrue(plan.ok);
    if (plan.ok) assert.include(plan.plan.args[3], '-InstanceName "work-vm"');
  });

  it("falls back to the four identity arguments on an older Update-T3Code.ps1", () => {
    const plan = planConstructInstanceReprovision(
      "work-vm",
      view,
      info,
      "win32",
      scriptsFs(false),
      join,
    );
    assert.isTrue(plan.ok);
    if (plan.ok) {
      assert.include(plan.plan.args[3], '-VmHost "work-vm.mshome.net"');
      assert.include(plan.plan.args[3], '-LocalKeyName "construct_work-vm_ed25519"');
    }
  });

  it("names even the DEFAULT instance, so a row can never mean 'whichever is default'", () => {
    const plan = planConstructInstanceReprovision(
      "agent-vm",
      view,
      info,
      "win32",
      scriptsFs(true),
      join,
    );
    assert.isTrue(plan.ok);
    if (plan.ok) assert.include(plan.plan.args[3], '-InstanceName "agent-vm"');
  });

  it("refuses a name the registry does not hold", () => {
    const plan = planConstructInstanceReprovision(
      "nope",
      view,
      info,
      "win32",
      scriptsFs(true),
      join,
    );
    assert.isFalse(plan.ok);
    if (!plan.ok) assert.include(plan.error, "not a Construct instance");
  });

  it("refuses when the registry itself was unusable", () => {
    const broken = readConstructInstances({ version: 9 });
    const plan = planConstructInstanceReprovision(
      "agent-vm",
      broken,
      info,
      "win32",
      scriptsFs(true),
      join,
    );
    assert.isFalse(plan.ok);
    if (!plan.ok) assert.include(plan.error, "instances.json");
  });
});

describe("the default instance's legacy mirror", () => {
  it("readConstructMarkers maps the saved channel to the npm dist-tag", () => {
    assert.equal(readConstructMarkers({ t3codeChannel: "nightly" }).channel, "nightly");
    assert.equal(readConstructMarkers({ t3codeChannel: "stable" }).channel, "latest");
    assert.isNull(readConstructMarkers({ t3codeChannel: "sideways" }).channel);
    assert.isNull(readConstructMarkers({}).channel);
  });

  it("a no-registry install still reports the default VM's own channel and commit", () => {
    // The single-VM path: no instances.json, no per-instance state file — everything the
    // row needs is at the legacy top level of .construct-settings.json.
    const settings = (channel: string) =>
      makeFs({
        files: {
          [`${SCRIPTS_DIR}\\.construct-settings.json`]: {
            mtime: 1,
            text: JSON.stringify({ provisionedCommit: PROVISIONED, t3codeChannel: channel }),
          },
        },
      });
    const rows = collectConstructInstances(
      readConstructInstances(null),
      LOCAL_APP_DATA,
      settings("nightly"),
      SCRIPTS_DIR,
      join,
    );
    assert.lengthOf(rows, 1);
    assert.equal(rows[0]!.name, "agent-vm");
    assert.isTrue(rows[0]!.isDefault);
    assert.equal(rows[0]!.provisionedCommit, PROVISIONED);
    assert.equal(rows[0]!.channel, "nightly");
    // ...and on the stable channel, which is the zero-change default.
    assert.equal(
      collectConstructInstances(
        readConstructInstances(null),
        LOCAL_APP_DATA,
        settings("stable"),
        SCRIPTS_DIR,
        join,
      )[0]!.channel,
      "latest",
    );
  });

  it("a NON-default instance never borrows the default store", () => {
    const rows = collectConstructInstances(
      readConstructInstances(MULTI_REGISTRY),
      LOCAL_APP_DATA,
      makeFs({
        files: {
          [`${SCRIPTS_DIR}\\.construct-settings.json`]: {
            mtime: 1,
            text: JSON.stringify({ provisionedCommit: PROVISIONED, t3codeChannel: "nightly" }),
          },
        },
      }),
      SCRIPTS_DIR,
      join,
    );
    const work = rows.find((r) => r.name === "work-vm")!;
    assert.isNull(work.provisionedCommit);
    assert.isNull(work.channel);
  });
});

// ── Auto-link (plan §4.12 "T3 Desktop topology") ──────────────────────────────

describe("readConstructT3Link", () => {
  it("reads a marker field by field", () => {
    assert.deepEqual(
      readConstructT3Link({ status: "linked", at: "2026-09-05T10:00:00Z", environmentId: "env-1", baseUrl: "https://work-vm.mshome.net:5178/" }),
      { status: "linked", at: "2026-09-05T10:00:00Z", environmentId: "env-1", baseUrl: "https://work-vm.mshome.net:5178", error: null },
    );
    assert.deepEqual(
      readConstructT3Link({ status: "failed", at: "2026-09-05T10:00:00Z", error: "ssh: connect timed out" }),
      { status: "failed", at: "2026-09-05T10:00:00Z", environmentId: null, baseUrl: null, error: "ssh: connect timed out" },
    );
  });
  it("treats anything malformed as never tried", () => {
    assert.isNull(readConstructT3Link(undefined));
    assert.isNull(readConstructT3Link("linked"));
    assert.isNull(readConstructT3Link({ status: "maybe", at: "2026-09-05T10:00:00Z" }));
    assert.isNull(readConstructT3Link({ status: "linked", at: "yesterday" }));
    assert.isNull(readConstructT3Link({ status: "linked" }));
  });
  it("is read from the instance's own half, with the VM's T3 opt-in and origin", () => {
    const state = readConstructMarkers(
      {},
      { t3code: true, t3BaseUrl: "https://far-vm.vpn.example.local:23011", t3Link: { status: "failed", at: "2026-09-05T10:00:00Z", error: "x" } },
    );
    assert.isTrue(state.t3Enabled);
    assert.equal(state.t3BaseUrl, "https://far-vm.vpn.example.local:23011");
    assert.equal(state.t3Link?.status, "failed");
    // A hand-edited file holding the STRING "false" is off, not truthy.
    assert.isFalse(readConstructMarkers({}, { t3code: "false" }).t3Enabled);
    assert.isNull(readConstructMarkers({}, {}).t3Enabled);
    assert.isNull(readConstructMarkers({}, { t3BaseUrl: "ftp://nope" }).t3BaseUrl);
  });
});

describe("recordConstructInstanceT3Link", () => {
  function writableFs(files: Record<string, string>) {
    const writes: Record<string, string> = {};
    const fs: ConstructFileSystem = {
      listDirectories: () => [],
      fileMtimeMs: (path) => (path in files || path in writes ? 1 : null),
      readTextFile: (path) => writes[path] ?? files[path] ?? null,
      writeTextFile: (path, text) => {
        writes[path] = text;
      },
    };
    return { fs, writes };
  }
  const LINK = { status: "linked" as const, at: "2026-09-05T10:00:00Z", environmentId: "env-1", baseUrl: "https://work-vm.mshome.net:5178", error: null };
  const FAILED = { status: "failed" as const, at: "2026-09-05T10:00:00Z", environmentId: null, baseUrl: null, error: "ssh" };

  it("writes the marker into a non-default instance's OWN file, keeping every other key", () => {
    const path = `${LOCAL_APP_DATA}\\The-Construct\\instances\\work-vm.json`;
    const { fs, writes } = writableFs({
      [path]: JSON.stringify({ version: 1, instance: "work-vm", provisionedCommit: PROVISIONED, t3Port: 5178 }),
    });
    const result = recordConstructInstanceT3Link(LOCAL_APP_DATA, "work-vm", SCRIPTS_DIR, LINK, fs, join);
    assert.isTrue(result.ok);
    const doc = JSON.parse(writes[path]!);
    assert.equal(doc.provisionedCommit, PROVISIONED);
    assert.equal(doc.t3Port, 5178);
    assert.equal(doc.instance, "work-vm");
    assert.deepEqual(doc.t3Link, { status: "linked", at: LINK.at, environmentId: "env-1", baseUrl: LINK.baseUrl });
    // ...and it reads back through the same reader the instances are collected with.
    assert.deepEqual(readConstructT3Link(doc.t3Link), LINK);
  });

  it("creates the file with the meta keys when the instance had none yet", () => {
    const path = `${LOCAL_APP_DATA}\\The-Construct\\instances\\far-vm.json`;
    const { fs, writes } = writableFs({});
    assert.isTrue(recordConstructInstanceT3Link(LOCAL_APP_DATA, "far-vm", SCRIPTS_DIR, FAILED, fs, join).ok);
    const doc = JSON.parse(writes[path]!);
    assert.equal(doc.version, 1);
    assert.equal(doc.instance, "far-vm");
    assert.deepEqual(doc.t3Link, { status: "failed", at: FAILED.at, error: "ssh" });
  });

  it("the default instance's marker goes to the legacy top level of .construct-settings.json", () => {
    const path = `${SCRIPTS_DIR}\\.construct-settings.json`;
    const { fs, writes } = writableFs({ [path]: JSON.stringify({ installedCommit: INSTALLED, t3code: true }) });
    assert.equal(constructT3LinkStorePath(LOCAL_APP_DATA, "agent-vm", SCRIPTS_DIR, join), path);
    assert.isTrue(recordConstructInstanceT3Link(LOCAL_APP_DATA, "agent-vm", SCRIPTS_DIR, LINK, fs, join).ok);
    const doc = JSON.parse(writes[path]!);
    assert.equal(doc.installedCommit, INSTALLED);
    assert.isTrue(doc.t3code);
    assert.isUndefined(doc.version);
    assert.equal(doc.t3Link.environmentId, "env-1");
  });

  it("refuses when nothing resolves, and says so", () => {
    const { fs } = writableFs({});
    assert.isFalse(recordConstructInstanceT3Link(LOCAL_APP_DATA, "agent-vm", null, LINK, fs, join).ok);
    assert.isFalse(recordConstructInstanceT3Link(LOCAL_APP_DATA, "Bad Name", SCRIPTS_DIR, LINK, fs, join).ok);
    const readOnly: ConstructFileSystem = { listDirectories: () => [], fileMtimeMs: () => null, readTextFile: () => null };
    const result = recordConstructInstanceT3Link(LOCAL_APP_DATA, "work-vm", SCRIPTS_DIR, LINK, readOnly, join);
    assert.isFalse(result.ok);
  });
});

describe("planConstructPairingLink", () => {
  const info = {
    scriptsDir: SCRIPTS_DIR,
    instances: [
      { name: "agent-vm", vmHost: "agent-vm.mshome.net", publicHost: null, hostAlias: "agent-vm", isDefault: true, provisionedCommit: null, channel: null, t3Port: null, t3Enabled: true, t3BaseUrl: null, t3Link: null },
      { name: "work-vm", vmHost: "work-vm.mshome.net", publicHost: null, hostAlias: "work-vm", isDefault: false, provisionedCommit: null, channel: null, t3Port: 5178, t3Enabled: null, t3BaseUrl: null, t3Link: null },
    ],
  };
  const withScript = makeFs({ files: { [`${SCRIPTS_DIR}\\${CONSTRUCT_PAIRING_LINK_SCRIPT}`]: { mtime: 1 } } });

  it("spawns PowerShell hidden on the script, naming the instance, with no console", () => {
    const planned = planConstructPairingLink("work-vm", info, "win32", withScript, join);
    assert.isTrue(planned.ok);
    if (!planned.ok) return;
    assert.equal(planned.plan.command, "powershell.exe");
    assert.equal(planned.plan.scriptPath, `${SCRIPTS_DIR}\\${CONSTRUCT_PAIRING_LINK_SCRIPT}`);
    assert.deepEqual([...planned.plan.args], [
      "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", planned.plan.scriptPath, "-InstanceName", "work-vm",
    ]);
    assert.notInclude(planned.plan.args, "cmd.exe");
  });

  it("the default instance is named too (the script resolves it to the default VM)", () => {
    const planned = planConstructPairingLink("agent-vm", info, "win32", withScript, join);
    assert.isTrue(planned.ok && planned.plan.args.includes("agent-vm"));
  });

  it("refuses an unknown or unusable name, a non-Windows host, a missing script and an absent Construct", () => {
    assert.isFalse(planConstructPairingLink("far-vm", info, "win32", withScript, join).ok);
    assert.isFalse(planConstructPairingLink("construct-x", info, "win32", withScript, join).ok);
    assert.isFalse(planConstructPairingLink("Work VM", info, "win32", withScript, join).ok);
    assert.isFalse(planConstructPairingLink("work-vm", info, "linux", withScript, join).ok);
    const noScript = planConstructPairingLink("work-vm", info, "win32", makeFs({}), join);
    assert.isFalse(noScript.ok);
    if (!noScript.ok) assert.include(noScript.error, CONSTRUCT_PAIRING_LINK_SCRIPT);
    assert.isFalse(planConstructPairingLink("work-vm", { ...info, scriptsDir: null }, "win32", withScript, join).ok);
    assert.isFalse(planConstructPairingLink("work-vm", null, "win32", withScript, join).ok);
  });
});

describe("parseConstructPairingLinkOutput", () => {
  it("takes the script's JSON line, whatever else landed on stdout", () => {
    const out = parseConstructPairingLinkOutput(
      'Warning: something\r\n{"ok":true,"instance":"work-vm","pairUrl":"https://work-vm.mshome.net:5178/pair#token=ABC","scopes":"administrative"}\r\n',
      { code: 0, stderr: "" },
    );
    assert.deepEqual(out, { ok: true, pairUrl: "https://work-vm.mshome.net:5178/pair#token=ABC", scopes: "administrative" });
  });
  it("carries the script's own failure reason", () => {
    const out = parseConstructPairingLinkOutput('{"ok":false,"instance":"far-vm","error":"could not reach far-vm over SSH"}\n', { code: 1, stderr: "" });
    assert.deepEqual(out, { ok: false, error: "could not reach far-vm over SSH" });
  });
  it("an unknown scope value is reported as standard, never as more", () => {
    const out = parseConstructPairingLinkOutput('{"ok":true,"pairUrl":"http://a:5177/pair#token=x","scopes":"root"}', { code: 0, stderr: "" });
    assert.isTrue(out.ok && out.scopes === "standard");
  });
  it("explains a script that printed nothing usable", () => {
    const crashed = parseConstructPairingLinkOutput("", { code: 1, stderr: "At line:1 char:1\nsomething broke\n" });
    assert.isFalse(crashed.ok);
    if (!crashed.ok) assert.include(crashed.error, "something broke");
    const silent = parseConstructPairingLinkOutput("", { code: 0, stderr: "" });
    assert.isFalse(silent.ok);
  });
});
