// @effect-diagnostics nodeBuiltinImport:off - measures the filesystem this server runs on.
import {
  ConstructDiskSpaceError,
  type ConstructDiskSpace,
  type ConstructDiskSpaceState,
} from "@t3tools/contracts";
import * as Effect from "effect/Effect";
import * as NodeFSP from "node:fs/promises";
import * as NodeOS from "node:os";

/** Below this the root reserve is all that is left: non-root processes cannot write. */
export const DISK_FULL_BYTES = 256 * 1024 * 1024;
/** Below this (or below 2 % of the volume) the margin is thin enough to warn. */
export const DISK_LOW_BYTES = 2 * 1024 * 1024 * 1024;
export const DISK_LOW_FRACTION = 0.02;

export function classifyDiskSpace(totalBytes: number, availableBytes: number): ConstructDiskSpaceState {
  if (availableBytes < DISK_FULL_BYTES) return "full";
  if (availableBytes < DISK_LOW_BYTES || availableBytes < totalBytes * DISK_LOW_FRACTION) return "low";
  return "ok";
}

/** The directory whose volume is measured: the server's data home (one disk on a VM). */
export function constructDiskPath(env: NodeJS.ProcessEnv = process.env): string {
  const home = env.T3CODE_HOME?.trim();
  return home ? home : NodeOS.homedir();
}

export const readConstructDiskSpace = (
  path: string = constructDiskPath(),
): Effect.Effect<ConstructDiskSpace, ConstructDiskSpaceError> =>
  Effect.tryPromise({
    try: async () => {
      const stats = await NodeFSP.statfs(path);
      const totalBytes = stats.blocks * stats.bsize;
      // f_bavail: blocks available to unprivileged users (f_bfree minus the root reserve).
      const availableBytes = stats.bavail * stats.bsize;
      return { path, totalBytes, availableBytes, state: classifyDiskSpace(totalBytes, availableBytes) };
    },
    catch: (cause) =>
      new ConstructDiskSpaceError({
        message: `Could not read the free space of ${path}: ${cause instanceof Error ? cause.message : String(cause)}`,
      }),
  });
