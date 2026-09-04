import type { EnvironmentId } from "@t3tools/contracts";
import { AsyncResult } from "effect/unstable/reactivity";
import { Workflow } from "lucide-react";
import { useEffect, useState } from "react";

import { readConstructOmniloopStatus } from "../../state/constructOmniloop";
import { useServerConfigs } from "../../state/entities";
import { useAtomCommand } from "../../state/use-atom-command";

/** Right-panel surface facts shared by the tab strip and the launcher cards. */
export const OMNILOOP_SURFACE_LABEL = "Omniloop";
export const OMNILOOP_SURFACE_DESCRIPTION = "Watch the workflows this VM's omniloop runs.";
export const OMNILOOP_SURFACE_SHORTCUT = "O";
export const OMNILOOP_DISABLED_REASON = "Omniloop is not running on this thread's VM.";
export const OMNILOOP_UNAVAILABLE_HINT = "Available while omniloop runs on the VM.";
export const OmniloopIcon = Workflow;

const STATUS_POLL_MS = 30_000;
/** Last known daemon state per environment, so a re-mounted tab strip renders instantly. */
const knownStatus = new Map<EnvironmentId, boolean>();

/**
 * True while the environment's server is Construct-patched (capability
 * `constructOmniloop`) and its omniloop daemon answers. Polled every 30 s: the
 * daemon starts on demand and stops on its own, so the tab comes and goes with it.
 */
export function useConstructOmniloopAvailable(environmentId: EnvironmentId | null): boolean {
  const serverConfigs = useServerConfigs();
  const capable =
    environmentId !== null &&
    serverConfigs.get(environmentId)?.environment.capabilities.constructOmniloop === true;
  const readStatus = useAtomCommand(readConstructOmniloopStatus, {
    reportFailure: false,
    reportDefect: false,
  });
  // Keyed by environment so a switch renders the last known answer for the new
  // environment at once, without resetting state inside the effect.
  const [polled, setPolled] = useState<{ environmentId: EnvironmentId; running: boolean } | null>(
    null,
  );

  useEffect(() => {
    if (!capable || environmentId === null) return;
    let cancelled = false;
    const poll = async () => {
      const result = await readStatus({ environmentId });
      if (cancelled) return;
      const running = AsyncResult.isSuccess(result) ? result.value.running : false;
      knownStatus.set(environmentId, running);
      setPolled({ environmentId, running });
    };
    void poll();
    const timer = setInterval(() => void poll(), STATUS_POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [capable, environmentId, readStatus]);

  if (!capable || environmentId === null) return false;
  return polled?.environmentId === environmentId
    ? polled.running
    : (knownStatus.get(environmentId) ?? false);
}
