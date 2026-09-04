import type {
  ConstructOmniloopWorkflow,
  EnvironmentId,
  OrchestrationThreadActivity,
} from "@t3tools/contracts";
import { AsyncResult } from "effect/unstable/reactivity";
import { useEffect, useMemo, useState } from "react";

import { cn } from "../../lib/utils";
import { readConstructOmniloopWorkflows } from "../../state/constructOmniloop";
import { useServerConfigs } from "../../state/entities";
import { useAtomCommand } from "../../state/use-atom-command";
import { Button } from "../ui/button";
import type { ComposerBannerStackItem } from "../chat/ComposerBannerStack";
import { useConstructOmniloopAvailable } from "./omniloopSurface";
import {
  describeOmniloopWorkflows,
  extractOmniloopWorkflowIds,
  isLiveOmniloopWorkflow,
} from "./omniloopWorkflows.logic";

const WORKFLOW_POLL_MS = 15_000;
const EMPTY_WORKFLOWS: ReadonlyArray<ConstructOmniloopWorkflow> = [];

export interface ConstructOmniloopThreadState {
  /** The thread's VM runs omniloop right now: the right panel offers the surface. */
  readonly available: boolean;
  readonly workflows: ReadonlyArray<ConstructOmniloopWorkflow>;
  /** Workflows still pending, running or paused. */
  readonly liveCount: number;
  /** The composer banner announcing live workflows, with "Open" leading to the panel. */
  readonly bannerItem: ComposerBannerStackItem | null;
}

/**
 * Follows the omniloop workflows a thread started: the ids come from the
 * thread's own tool-call activities, the status from the VM's daemon (polled
 * every 15 s while any workflow is live). Nothing runs for threads that never
 * called omniloop, or on servers without the capability.
 */
export function useConstructOmniloopThread(options: {
  readonly threadRef: { readonly environmentId: EnvironmentId; readonly threadId: string } | null;
  readonly activities: ReadonlyArray<OrchestrationThreadActivity>;
  readonly onOpen: (workflowId: string | null) => void;
}): ConstructOmniloopThreadState {
  const { threadRef, activities, onOpen } = options;
  const environmentId = threadRef?.environmentId ?? null;
  const serverConfigs = useServerConfigs();
  const capable =
    environmentId !== null &&
    serverConfigs.get(environmentId)?.environment.capabilities.constructOmniloop === true;
  const available = useConstructOmniloopAvailable(environmentId);
  const workflowIds = useMemo(
    () => (capable ? extractOmniloopWorkflowIds(activities) : []),
    [capable, activities],
  );
  const idsKey = workflowIds.join(",");
  const readWorkflows = useAtomCommand(readConstructOmniloopWorkflows, {
    reportFailure: false,
    reportDefect: false,
  });
  // Keyed by the id set: a result for a previous set is stale and simply not
  // shown, so nothing has to be reset inside the effect when the thread changes.
  const [polled, setPolled] = useState<{
    idsKey: string;
    workflows: ReadonlyArray<ConstructOmniloopWorkflow>;
  } | null>(null);
  const workflows = polled?.idsKey === idsKey ? polled.workflows : EMPTY_WORKFLOWS;

  useEffect(() => {
    if (environmentId === null || idsKey === "") return;
    const ids = idsKey.split(",");
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const poll = async () => {
      const result = await readWorkflows({ environmentId, workflowIds: ids });
      if (cancelled) return;
      if (AsyncResult.isSuccess(result)) {
        setPolled({ idsKey, workflows: result.value.workflows });
        // Settled workflows never change again: stop polling until the ids do.
        if (!result.value.workflows.some(isLiveOmniloopWorkflow)) return;
      }
      timer = setTimeout(() => void poll(), WORKFLOW_POLL_MS);
    };
    void poll();
    return () => {
      cancelled = true;
      if (timer !== null) clearTimeout(timer);
    };
  }, [environmentId, idsKey, readWorkflows]);

  return useMemo(() => {
    const notice = describeOmniloopWorkflows(workflows);
    const liveCount = workflows.filter(isLiveOmniloopWorkflow).length;
    const bannerItem: ComposerBannerStackItem | null =
      notice === null || threadRef === null
        ? null
        : {
            id: `construct-omniloop:${threadRef.threadId}`,
            variant: "default",
            priority: "activity",
            icon: (
              <span
                className={cn("size-1.5 rounded-full bg-foreground", "animate-status-pulse")}
                aria-hidden="true"
              />
            ),
            title: notice.title,
            actions: (
              <Button size="xs" variant="ghost" onClick={() => onOpen(notice.workflowId)}>
                Open
              </Button>
            ),
          };
    return { available, workflows, liveCount, bannerItem };
  }, [available, onOpen, threadRef, workflows]);
}
