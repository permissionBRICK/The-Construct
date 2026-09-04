import { WS_METHODS, type EnvironmentId } from "@t3tools/contracts";
import { request } from "@t3tools/client-runtime/rpc";
import { createRuntimeCommand, runInEnvironment } from "@t3tools/client-runtime/state/runtime";

import { connectionAtomRuntime } from "../connection/runtime";

/** Free space on the disk the environment's T3 server (the Construct VM) runs on. */
export const readConstructDiskSpace = createRuntimeCommand(connectionAtomRuntime, {
  label: "construct-disk-space:read",
  execute: (target: { readonly environmentId: EnvironmentId }) =>
    runInEnvironment(target.environmentId, request(WS_METHODS.constructDiskSpace, {})),
});
