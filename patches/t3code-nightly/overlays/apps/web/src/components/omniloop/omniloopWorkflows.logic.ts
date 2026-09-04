import type { ConstructOmniloopWorkflow, OrchestrationThreadActivity } from "@t3tools/contracts";

const WORKFLOW_ID = /\bwf_[A-Za-z0-9_-]{6,}\b/g;
const LIVE_STATUSES: ReadonlySet<ConstructOmniloopWorkflow["status"]> = new Set([
  "pending",
  "running",
  "paused",
]);

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

/** An MCP tool call that went to the omniloop server (`mcp__omniloop__submit` and friends). */
export function isOmniloopToolActivity(activity: OrchestrationThreadActivity): boolean {
  const payload = asRecord(activity.payload);
  if (payload?.itemType !== "mcp_tool_call") return false;
  const data = asRecord(payload.data);
  const item = asRecord(data?.item);
  const names = [data?.toolName, item?.tool, item?.server, payload.title];
  return names.some((name) => typeof name === "string" && /omniloop/i.test(name));
}

/**
 * Workflow ids a thread has touched, oldest first. Omniloop's `submit` result
 * carries the new id and `status`/`await`/`inspect` calls name it in their
 * arguments, so scanning the tool payload text finds every workflow the agent
 * dealt with without knowing each tool's shape.
 */
export function extractOmniloopWorkflowIds(
  activities: ReadonlyArray<OrchestrationThreadActivity>,
): ReadonlyArray<string> {
  const ids = new Set<string>();
  for (const activity of activities) {
    if (!isOmniloopToolActivity(activity)) continue;
    const payload = asRecord(activity.payload);
    const text = JSON.stringify(payload?.data ?? "") + " " + (activity.summary ?? "");
    for (const match of text.matchAll(WORKFLOW_ID)) ids.add(match[0]);
  }
  return [...ids];
}

export function isLiveOmniloopWorkflow(workflow: ConstructOmniloopWorkflow): boolean {
  return LIVE_STATUSES.has(workflow.status);
}

export interface OmniloopWorkflowNotice {
  readonly title: string;
  readonly workflowId: string;
}

/** The composer banner for a thread's workflows, or null while none is live. */
export function describeOmniloopWorkflows(
  workflows: ReadonlyArray<ConstructOmniloopWorkflow>,
): OmniloopWorkflowNotice | null {
  const live = workflows.filter(isLiveOmniloopWorkflow);
  if (live.length === 0) return null;
  const newest = live[live.length - 1]!;
  if (live.length === 1) {
    const verb = newest.status === "paused" ? "paused" : newest.status === "pending" ? "queued" : "running";
    return { title: `Omniloop workflow ${verb}: ${newest.name}`, workflowId: newest.id };
  }
  const running = live.filter((workflow) => workflow.status === "running").length;
  const detail = running === live.length ? "running" : `${running} running, ${live.length - running} waiting`;
  return { title: `${live.length} omniloop workflows ${detail}`, workflowId: newest.id };
}
