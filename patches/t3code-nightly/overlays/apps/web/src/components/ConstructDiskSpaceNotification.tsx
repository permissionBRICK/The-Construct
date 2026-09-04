import type { ConstructDiskSpace, EnvironmentId } from "@t3tools/contracts";
import { AsyncResult } from "effect/unstable/reactivity";
import { useEffect, useMemo, useRef } from "react";

import { readConstructDiskSpace } from "../state/constructDiskSpace";
import { useServerConfigs } from "../state/entities";
import { useEnvironments } from "../state/environments";
import { useAtomCommand } from "../state/use-atom-command";
import {
  constructDiskSpaceNotificationKey,
  describeConstructDiskSpace,
} from "./constructDiskSpace.logic";
import { stackedThreadToast, toastManager } from "./ui/toast";

const POLL_INTERVAL_MS = 60_000;
type DiskToastId = ReturnType<typeof toastManager.add>;
/** Prompts the user closed: silenced until that environment's severity changes. */
const dismissedDiskSpaceKeys = new Set<string>();

/**
 * Warns when a connected Construct VM's disk is running full. Every server that
 * advertises the `constructDiskSpace` capability (a Construct-patched T3 server) is
 * asked once a minute; a `low` volume raises an info prompt, a `full` one (only the
 * root reserve left, so non-root writes fail) an error prompt. Closing a prompt keeps
 * it away until the severity changes; a volume that recovers withdraws its prompt.
 */
export function ConstructDiskSpaceNotification() {
  const serverConfigs = useServerConfigs();
  const { environments } = useEnvironments();
  const read = useAtomCommand(readConstructDiskSpace, { reportFailure: false, reportDefect: false });
  const environmentIds = [...serverConfigs]
    .filter(([, config]) => config.environment.capabilities.constructDiskSpace === true)
    .map(([environmentId]) => environmentId);
  // A stable dependency: the effect below restarts only when the SET of servers changes.
  const environmentKey = environmentIds.join("\n");
  const labels = useMemo(
    () => new Map(environments.map((env) => [env.environmentId, env.label])),
    [environments],
  );
  // Read by the poll below without restarting it when a label changes.
  const labelsRef = useRef(labels);
  useEffect(() => {
    labelsRef.current = labels;
  }, [labels]);
  const toastsRef = useRef(new Map<EnvironmentId, { readonly key: string; readonly toastId: DiskToastId }>());

  useEffect(() => {
    const ids = environmentKey === "" ? [] : (environmentKey.split("\n") as EnvironmentId[]);
    let cancelled = false;
    const toasts = toastsRef.current;

    const closeToast = (environmentId: EnvironmentId) => {
      const active = toasts.get(environmentId);
      if (active === undefined) return;
      toastManager.close(active.toastId);
      toasts.delete(environmentId);
    };

    const show = (environmentId: EnvironmentId, space: ConstructDiskSpace) => {
      const key = constructDiskSpaceNotificationKey(environmentId, space.state);
      const notice = describeConstructDiskSpace(
        space,
        labelsRef.current.get(environmentId) ?? "Construct VM",
      );
      if (notice === null) {
        closeToast(environmentId);
        return;
      }
      if (toasts.get(environmentId)?.key === key || dismissedDiskSpaceKeys.has(key)) return;
      closeToast(environmentId);
      const toastId = toastManager.add(
        stackedThreadToast({
          type: notice.type,
          title: notice.title,
          description: notice.description,
          timeout: 0,
          data: {
            hideCopyButton: true,
            onClose: () => {
              dismissedDiskSpaceKeys.add(key);
              if (toasts.get(environmentId)?.toastId === toastId) toasts.delete(environmentId);
            },
          },
        }),
      );
      toasts.set(environmentId, { key, toastId });
    };

    const poll = async () => {
      for (const environmentId of ids) {
        const result = await read({ environmentId });
        if (cancelled) return;
        // An offline or stock server simply has nothing to say; the prompt stays as is.
        if (AsyncResult.isFailure(result)) continue;
        show(environmentId, result.value);
      }
    };

    void poll();
    const timer = setInterval(() => void poll(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
      for (const environmentId of toasts.keys()) closeToast(environmentId);
    };
  }, [environmentKey, read]);

  return null;
}
