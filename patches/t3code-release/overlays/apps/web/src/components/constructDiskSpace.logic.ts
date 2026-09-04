import type { ConstructDiskSpace, ConstructDiskSpaceState, EnvironmentId } from "@t3tools/contracts";

export function formatDiskBytes(bytes: number): string {
  const gb = bytes / 1_000_000_000;
  if (gb >= 10) return `${Math.round(gb)} GB`;
  if (gb >= 1) return `${gb.toFixed(1)} GB`;
  return `${Math.round(bytes / 1_000_000)} MB`;
}

export interface ConstructDiskSpaceNotice {
  readonly type: "error" | "info";
  readonly title: string;
  readonly description: string;
}

/** The popup for a measured volume, or null while there is nothing to warn about. */
export function describeConstructDiskSpace(
  space: ConstructDiskSpace,
  environmentLabel: string,
): ConstructDiskSpaceNotice | null {
  const left = `${formatDiskBytes(space.availableBytes)} left for non-root processes on ${space.path} (${formatDiskBytes(space.totalBytes)} volume).`;
  switch (space.state) {
    case "full":
      return {
        type: "error",
        title: `${environmentLabel}: the Construct disk is full`,
        description: `${left} Only the root reserve remains, so agents and the T3 server will start failing to write. Free up space on the VM now.`,
      };
    case "low":
      return {
        type: "info",
        title: `${environmentLabel}: the Construct disk is almost full`,
        description: `${left} Free up space on the VM before writes start failing.`,
      };
    case "ok":
      return null;
  }
}

/** One prompt per environment and severity: closing it silences that severity until it changes. */
export function constructDiskSpaceNotificationKey(
  environmentId: EnvironmentId,
  state: ConstructDiskSpaceState,
): string {
  return `${environmentId}:${state}`;
}
