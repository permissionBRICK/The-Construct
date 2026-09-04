import { describe, expect, it } from "@effect/vitest";
import * as Effect from "effect/Effect";
import * as NodeOS from "node:os";

import {
  classifyDiskSpace,
  constructDiskPath,
  DISK_FULL_BYTES,
  DISK_LOW_BYTES,
  readConstructDiskSpace,
} from "./constructDiskSpace.ts";

const GiB = 1024 * 1024 * 1024;

describe("classifyDiskSpace", () => {
  it("is full once only the root reserve is left", () => {
    expect(classifyDiskSpace(300 * GiB, 0)).toBe("full");
    expect(classifyDiskSpace(300 * GiB, DISK_FULL_BYTES - 1)).toBe("full");
  });

  it("is low under the byte margin or under 2 % of the volume", () => {
    expect(classifyDiskSpace(300 * GiB, DISK_FULL_BYTES)).toBe("low");
    expect(classifyDiskSpace(300 * GiB, DISK_LOW_BYTES - 1)).toBe("low");
    // 2 % of 300 GiB is 6 GiB: a larger volume warns earlier than the flat margin.
    expect(classifyDiskSpace(300 * GiB, 5 * GiB)).toBe("low");
    expect(classifyDiskSpace(300 * GiB, 7 * GiB)).toBe("ok");
  });

  it("is ok with room to spare", () => {
    expect(classifyDiskSpace(40 * GiB, 3 * GiB)).toBe("ok");
  });
});

describe("constructDiskPath", () => {
  it("measures T3CODE_HOME when set, else the home directory", () => {
    expect(constructDiskPath({ T3CODE_HOME: "/srv/t3 " })).toBe("/srv/t3");
    expect(constructDiskPath({ T3CODE_HOME: "" })).toBe(NodeOS.homedir());
    expect(constructDiskPath({})).toBe(NodeOS.homedir());
  });
});

describe("readConstructDiskSpace", () => {
  it.effect("reports the measured volume", () =>
    Effect.gen(function* () {
      const space = yield* readConstructDiskSpace(NodeOS.tmpdir());
      expect(space.path).toBe(NodeOS.tmpdir());
      expect(space.totalBytes).toBeGreaterThan(0);
      expect(space.availableBytes).toBeGreaterThanOrEqual(0);
      expect(space.availableBytes).toBeLessThanOrEqual(space.totalBytes);
      expect(space.state).toBe(classifyDiskSpace(space.totalBytes, space.availableBytes));
    }),
  );

  it.effect("fails with a tagged error for a missing path", () =>
    Effect.gen(function* () {
      const result = yield* Effect.flip(readConstructDiskSpace("/definitely/not/here"));
      expect(result._tag).toBe("ConstructDiskSpaceError");
      expect(result.message).toContain("/definitely/not/here");
    }),
  );
});
