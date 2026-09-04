import { describe, expect, it } from "vite-plus/test";
import type {
  ConstructUpdateInfo,
  DesktopUpdateActionResult,
  DesktopUpdateState,
} from "@t3tools/contracts";

import {
  getConstructLaunchConfirmationMessage,
  getConstructLaunchOutcome,
  getConstructUpdateButtonLabel,
  getConstructUpdateDetail,
  getConstructUpdateHeadline,
  getConstructUpdateNotificationKey,
  getConstructUpdateTooltip,
  getConstructVersionLabel,
} from "./constructUpdate.logic";
import { getDesktopUpdateButtonTooltip } from "./desktopUpdate.logic";

const INSTALLED = "dc44958114c7c43145c8f7830f6185235a1d752b";
const PROVISIONED = "b262652aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

const current: ConstructUpdateInfo = {
  repo: "permissionBRICK/The-Construct",
  ref: "main",
  scriptsDir: "C:\\Users\\alice\\AppData\\Local\\The-Construct\\x\\The-Construct-main",
  vmName: "agent-vm",
  vmHost: "agent-vm.mshome.net",
  instances: [],
  installedCommit: INSTALLED,
  provisionedCommit: INSTALLED,
  behind: 0,
  constructUpdateAvailable: false,
  provisionStale: false,
  t3Version: "0.0.38",
  t3LatestVersion: "0.0.38",
  t3LatestByChannel: { latest: "0.0.38", nightly: null },
  t3UpdateAvailable: false,
  action: null,
  runningAction: null,
  checkedAt: "2026-09-03T10:00:00.000Z",
  error: null,
};

// The cast keeps this fixture valid across upstream versions: `omittedReleaseCount`
// is required on newer states and unknown on older ones.
const baseState = {
  enabled: true,
  status: "up-to-date",
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

describe("construct update presentation", () => {
  it("describes an up-to-date install", () => {
    expect(getConstructVersionLabel(current)).toBe("main@dc44958");
    expect(getConstructUpdateHeadline(current)).toBe("Construct is up to date");
    expect(getConstructUpdateDetail(current)).toContain("T3 Code 0.0.38 is the newest release");
    expect(getConstructUpdateTooltip(current)).toBe("Construct and T3 Code are up to date");
    expect(getConstructUpdateButtonLabel(current)).toBe("Check for Updates");
    expect(getConstructUpdateNotificationKey(current)).toBeNull();
    expect(getConstructLaunchConfirmationMessage(current)).toBeNull();
  });

  it("offers the Construct update with its distance", () => {
    const info: ConstructUpdateInfo = {
      ...current,
      behind: 3,
      constructUpdateAvailable: true,
      action: "update-construct",
    };
    expect(getConstructUpdateHeadline(info)).toBe("Construct update available");
    expect(getConstructUpdateDetail(info)).toContain("3 new commits");
    expect(getConstructUpdateDetail(info)).toContain("reprovision the VM afterwards");
    expect(getConstructUpdateTooltip(info)).toBe(
      "Construct update available — click to update Construct on this PC.",
    );
    expect(getConstructUpdateButtonLabel(info)).toBe("Update Construct");
    expect(getConstructLaunchConfirmationMessage(info)).toBeNull();
    expect(getConstructUpdateNotificationKey(info)).toBe(
      `construct:update-construct:${INSTALLED}:${INSTALLED}:-`,
    );
    // A rewritten history (no distance) still reads as an update.
    expect(getConstructUpdateDetail({ ...info, behind: null })).toContain("no longer on main");
  });

  it("explains a pending reprovision from both causes", () => {
    const stale: ConstructUpdateInfo = {
      ...current,
      provisionedCommit: PROVISIONED,
      provisionStale: true,
      action: "reprovision",
    };
    expect(getConstructUpdateHeadline(stale)).toBe("VM reprovision pending");
    expect(getConstructUpdateDetail(stale)).toContain("provisioned with Construct b262652");
    expect(getConstructUpdateDetail(stale)).toContain("this PC has dc44958");
    expect(getConstructUpdateButtonLabel(stale)).toBe("Reprovision VM");
    expect(getConstructLaunchConfirmationMessage(stale)).toContain(
      'Reprovision the Construct VM "agent-vm" (agent-vm.mshome.net) now?',
    );

    const t3: ConstructUpdateInfo = {
      ...current,
      t3LatestVersion: "0.0.39",
      t3UpdateAvailable: true,
      action: "reprovision",
    };
    expect(getConstructUpdateHeadline(t3)).toBe("T3 Code update available");
    expect(getConstructUpdateDetail(t3)).toContain("T3 Code 0.0.39 is available upstream");
    expect(getConstructUpdateNotificationKey(t3)).toBe(
      `construct:reprovision:${INSTALLED}:${INSTALLED}:0.0.39`,
    );

    const both: ConstructUpdateInfo = {
      ...stale,
      ...t3,
      provisionStale: true,
      provisionedCommit: PROVISIONED,
    };
    expect(getConstructUpdateHeadline(both)).toBe("VM reprovision pending");
    expect(getConstructUpdateDetail(both)).toContain("b262652");
    expect(getConstructUpdateDetail(both)).toContain("0.0.39");
  });

  it("marks a running script and a failed check", () => {
    const running: ConstructUpdateInfo = {
      ...current,
      action: "reprovision",
      runningAction: "reprovision",
    };
    expect(getConstructUpdateHeadline(running)).toBe("Construct reprovision running");
    expect(getConstructUpdateTooltip(running)).toBe("Construct reprovision running…");
    expect(getConstructUpdateButtonLabel(running)).toBe("Running…");
    expect(getConstructUpdateNotificationKey(running)).toBeNull();

    const failed: ConstructUpdateInfo = {
      ...current,
      error: "Could not check GitHub for Construct updates.",
    };
    expect(getConstructUpdateHeadline(failed)).toBe("Construct update check failed");
    expect(getConstructUpdateTooltip(failed)).toContain("Could not check GitHub");
  });

  it("routes the stock tooltip through the Construct wording when present", () => {
    const state: DesktopUpdateState = {
      ...baseState,
      status: "available",
      availableVersion: "Construct main (3 commits behind)",
      construct: {
        ...current,
        behind: 3,
        constructUpdateAvailable: true,
        action: "update-construct",
      },
    };
    expect(getDesktopUpdateButtonTooltip(state)).toBe(
      "Construct update available — click to update Construct on this PC.",
    );
    expect(
      getDesktopUpdateButtonTooltip({
        ...baseState,
        status: "available",
        availableVersion: "1.1.0",
      }),
    ).toBe("Update 1.1.0 ready to download");
  });

  it("reads the launch outcome from the download result", () => {
    const started: DesktopUpdateActionResult = {
      accepted: true,
      completed: false,
      state: {
        ...baseState,
        status: "downloading",
        construct: { ...current, action: "reprovision", runningAction: "reprovision" },
      },
    };
    expect(getConstructLaunchOutcome(started)).toEqual({ kind: "started", action: "reprovision" });

    const failed: DesktopUpdateActionResult = {
      accepted: true,
      completed: false,
      state: {
        ...baseState,
        status: "available",
        message: "Update-T3Code.ps1 was not found in C:\\x.",
        errorContext: "download",
        construct: {
          ...current,
          action: "reprovision",
          error: "Update-T3Code.ps1 was not found in C:\\x.",
        },
      },
    };
    expect(getConstructLaunchOutcome(failed)).toEqual({
      kind: "failed",
      message: "Update-T3Code.ps1 was not found in C:\\x.",
    });

    expect(
      getConstructLaunchOutcome({
        accepted: false,
        completed: false,
        state: { ...baseState, construct: current },
      }),
    ).toEqual({ kind: "nothing" });
  });
});
