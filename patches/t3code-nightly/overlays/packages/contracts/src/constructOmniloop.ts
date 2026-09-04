import * as Schema from "effect/Schema";

/**
 * Omniloop (Construct's workflow orchestrator) runs as a daemon on the VM next
 * to the T3 server and serves its own dashboard on a local port. A patched
 * T3 server reports whether that daemon is up, mints tickets that let a client
 * reach the dashboard through the server's own origin, and looks up the status
 * of workflows a thread started.
 */

/** Whether the omniloop daemon answers on the VM right now. */
export const ConstructOmniloopStatus = Schema.Struct({
  running: Schema.Boolean,
  port: Schema.Number,
});
export type ConstructOmniloopStatus = typeof ConstructOmniloopStatus.Type;

/**
 * A ticket embedded in the proxy path (`/construct/omniloop/<ticket>/...`), so
 * every request the dashboard makes relative to its own URL carries it; that is
 * what lets an iframe work in clients without a session cookie (the Desktop app,
 * the relay). `guiPath` is the dashboard entry point relative to the server origin.
 */
export const ConstructOmniloopTicket = Schema.Struct({
  ticket: Schema.String,
  guiPath: Schema.String,
  expiresAt: Schema.String,
});
export type ConstructOmniloopTicket = typeof ConstructOmniloopTicket.Type;

/** Omniloop's own workflow states, plus `unknown` for an id the daemon no longer has. */
export const ConstructOmniloopWorkflowStatus = Schema.Literals([
  "pending",
  "running",
  "paused",
  "completed",
  "failed",
  "cancelled",
  "timed_out",
  "unknown",
]);
export type ConstructOmniloopWorkflowStatus = typeof ConstructOmniloopWorkflowStatus.Type;

export const ConstructOmniloopWorkflow = Schema.Struct({
  id: Schema.String,
  name: Schema.String,
  status: ConstructOmniloopWorkflowStatus,
});
export type ConstructOmniloopWorkflow = typeof ConstructOmniloopWorkflow.Type;

export const ConstructOmniloopWorkflowsInput = Schema.Struct({
  workflowIds: Schema.Array(Schema.String),
});
export type ConstructOmniloopWorkflowsInput = typeof ConstructOmniloopWorkflowsInput.Type;

export const ConstructOmniloopWorkflowsResult = Schema.Struct({
  workflows: Schema.Array(ConstructOmniloopWorkflow),
});
export type ConstructOmniloopWorkflowsResult = typeof ConstructOmniloopWorkflowsResult.Type;

export class ConstructOmniloopError extends Schema.TaggedErrorClass<ConstructOmniloopError>()(
  "ConstructOmniloopError",
  { message: Schema.String },
) {}
