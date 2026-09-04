import { WS_METHODS, type EnvironmentId } from "@t3tools/contracts";
import { request } from "@t3tools/client-runtime/rpc";
import { createRuntimeCommand, runInEnvironment } from "@t3tools/client-runtime/state/runtime";

import { connectionAtomRuntime } from "../connection/runtime";

/** Whether the omniloop daemon is up on the environment's VM. */
export const readConstructOmniloopStatus = createRuntimeCommand(connectionAtomRuntime, {
  label: "construct-omniloop:status",
  execute: (target: { readonly environmentId: EnvironmentId }) =>
    runInEnvironment(target.environmentId, request(WS_METHODS.constructOmniloopStatus, {})),
});

/** A fresh proxy ticket plus the dashboard path to load in an iframe. */
export const issueConstructOmniloopTicket = createRuntimeCommand(connectionAtomRuntime, {
  label: "construct-omniloop:ticket",
  execute: (target: { readonly environmentId: EnvironmentId }) =>
    runInEnvironment(target.environmentId, request(WS_METHODS.constructOmniloopTicket, {})),
});

/** Current status of the given omniloop workflows. */
export const readConstructOmniloopWorkflows = createRuntimeCommand(connectionAtomRuntime, {
  label: "construct-omniloop:workflows",
  execute: (target: {
    readonly environmentId: EnvironmentId;
    readonly workflowIds: ReadonlyArray<string>;
  }) =>
    runInEnvironment(
      target.environmentId,
      request(WS_METHODS.constructOmniloopWorkflows, { workflowIds: [...target.workflowIds] }),
    ),
});
