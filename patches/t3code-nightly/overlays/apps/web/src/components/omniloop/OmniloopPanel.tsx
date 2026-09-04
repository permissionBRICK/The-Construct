import type { EnvironmentId } from "@t3tools/contracts";
import { AsyncResult } from "effect/unstable/reactivity";
import { useEffect, useState } from "react";

import { issueConstructOmniloopTicket } from "../../state/constructOmniloop";
import { useEnvironmentHttpBaseUrl } from "../../state/environments";
import { useAtomCommand } from "../../state/use-atom-command";
import { OMNILOOP_DISABLED_REASON } from "./omniloopSurface";

/**
 * The omniloop dashboard in the right panel. It is served through the T3
 * server (`/construct/omniloop/<ticket>/...`), so it loads wherever the T3 UI
 * loads: the browser, the Desktop app, a relay client. A fresh ticket is minted
 * whenever the panel (re)mounts or the environment changes; `workflowId`
 * deep-links the dashboard to that workflow.
 */
export function OmniloopPanel({
  environmentId,
  workflowId,
}: {
  readonly environmentId: EnvironmentId | null;
  readonly workflowId?: string | null;
}) {
  const httpBaseUrl = useEnvironmentHttpBaseUrl(environmentId);
  const issueTicket = useAtomCommand(issueConstructOmniloopTicket, {
    reportFailure: false,
    reportDefect: false,
  });
  // The ticket belongs to one environment: a result for another environment is
  // simply not this panel's, so no state has to be reset when the thread changes.
  const [ticket, setTicket] = useState<{
    environmentId: EnvironmentId;
    guiPath: string | null;
  } | null>(null);

  useEffect(() => {
    if (environmentId === null) return;
    let cancelled = false;
    void issueTicket({ environmentId }).then((result) => {
      if (cancelled) return;
      setTicket({
        environmentId,
        guiPath: AsyncResult.isSuccess(result) ? result.value.guiPath : null,
      });
    });
    return () => {
      cancelled = true;
    };
  }, [environmentId, issueTicket]);

  const current = ticket?.environmentId === environmentId ? ticket : null;
  if (environmentId === null || (current !== null && current.guiPath === null)) {
    return (
      <div className="flex size-full items-center justify-center p-6 text-sm text-muted-foreground">
        {OMNILOOP_DISABLED_REASON}
      </div>
    );
  }
  if (current === null) {
    return (
      <div className="flex size-full items-center justify-center p-6 text-sm text-muted-foreground">
        Opening omniloop…
      </div>
    );
  }
  const src = `${httpBaseUrl ?? ""}${current.guiPath}${workflowId ? `#/w/${workflowId}` : ""}`;
  return (
    // No sandbox: the dashboard is served by this same server behind a ticket and
    // keeps its token in localStorage, so it needs the same-origin scripts a
    // sandbox with allow-scripts + allow-same-origin would grant anyway.
    // oxlint-disable-next-line react/iframe-missing-sandbox
    <iframe
      key={src}
      title="Omniloop"
      src={src}
      className="size-full border-0 bg-background"
      allow="clipboard-write"
    />
  );
}
