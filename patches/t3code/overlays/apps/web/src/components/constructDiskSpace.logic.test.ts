import type { ConstructDiskSpace, EnvironmentId } from "@t3tools/contracts";
import { describe, expect, it } from "vite-plus/test";

import {
  constructDiskSpaceNotificationKey,
  describeConstructDiskSpace,
  formatDiskBytes,
} from "./constructDiskSpace.logic";

const GB = 1_000_000_000;

describe("formatDiskBytes", () => {
  it("picks a unit the reader can act on", () => {
    expect(formatDiskBytes(300 * GB)).toBe("300 GB");
    expect(formatDiskBytes(12.6 * GB)).toBe("13 GB");
    expect(formatDiskBytes(1.25 * GB)).toBe("1.3 GB");
    expect(formatDiskBytes(120_000_000)).toBe("120 MB");
    expect(formatDiskBytes(0)).toBe("0 MB");
  });
});

describe("describeConstructDiskSpace", () => {
  const measured = (state: ConstructDiskSpace["state"], availableBytes: number): ConstructDiskSpace => ({
    path: "/root",
    totalBytes: 300 * GB,
    availableBytes,
    state,
  });

  it("is silent while the disk is fine", () => {
    expect(describeConstructDiskSpace(measured("ok", 50 * GB), "agent-vm")).toBeNull();
  });

  it("warns with the unprivileged figure when the margin is thin", () => {
    const notice = describeConstructDiskSpace(measured("low", 1.5 * GB), "agent-vm");
    expect(notice?.type).toBe("info");
    expect(notice?.title).toBe("agent-vm: the Construct disk is almost full");
    expect(notice?.description).toContain("1.5 GB left for non-root processes on /root (300 GB volume).");
  });

  it("raises an error once only the root reserve is left", () => {
    const notice = describeConstructDiskSpace(measured("full", 40_000_000), "agent-vm");
    expect(notice?.type).toBe("error");
    expect(notice?.title).toBe("agent-vm: the Construct disk is full");
    expect(notice?.description).toContain("40 MB left for non-root processes");
    expect(notice?.description).toContain("root reserve");
  });
});

describe("constructDiskSpaceNotificationKey", () => {
  it("separates environments and severities", () => {
    const a = "env-a" as EnvironmentId;
    const b = "env-b" as EnvironmentId;
    expect(constructDiskSpaceNotificationKey(a, "low")).not.toBe(constructDiskSpaceNotificationKey(a, "full"));
    expect(constructDiskSpaceNotificationKey(a, "low")).not.toBe(constructDiskSpaceNotificationKey(b, "low"));
  });
});
