import type { ConstructOmniloopWorkflow, OrchestrationThreadActivity } from "@t3tools/contracts";
import { describe, expect, it } from "vite-plus/test";

import {
  describeOmniloopWorkflows,
  extractOmniloopWorkflowIds,
  isOmniloopToolActivity,
} from "./omniloopWorkflows.logic";

const activity = (
  id: string,
  payload: unknown,
  summary = "tool",
): OrchestrationThreadActivity =>
  ({
    id,
    tone: "tool",
    kind: "tool.completed",
    summary,
    payload,
    turnId: null,
    createdAt: "2026-09-04T10:00:00.000Z",
  }) as unknown as OrchestrationThreadActivity;

const workflow = (
  id: string,
  status: ConstructOmniloopWorkflow["status"],
  name = id,
): ConstructOmniloopWorkflow => ({ id, name, status });

describe("extractOmniloopWorkflowIds", () => {
  it("finds ids in omniloop submit results and later status calls, oldest first", () => {
    const ids = extractOmniloopWorkflowIds([
      activity("1", {
        itemType: "mcp_tool_call",
        data: {
          toolName: "mcp__omniloop__submit",
          result: { content: '{"status":"started","workflow_id":"wf_V1StGXR8Z5jd"}' },
        },
      }),
      activity("2", {
        itemType: "mcp_tool_call",
        data: { item: { tool: "status", server: "omniloop", arguments: { workflow_id: "wf_Abc123xyz789" } } },
      }),
      activity("3", {
        itemType: "mcp_tool_call",
        data: { toolName: "mcp__omniloop__await", input: { workflow_id: "wf_V1StGXR8Z5jd" } },
      }),
    ]);
    expect(ids).toEqual(["wf_V1StGXR8Z5jd", "wf_Abc123xyz789"]);
  });

  it("ignores other tools even when they mention workflow-looking ids", () => {
    const activities = [
      activity("1", {
        itemType: "mcp_tool_call",
        data: { toolName: "mcp__t3-code__preview_open", result: { content: "wf_NotOmniloop1" } },
      }),
      activity("2", { itemType: "command_execution", data: { command: "echo wf_NotOmniloop2" } }),
    ];
    expect(activities.map(isOmniloopToolActivity)).toEqual([false, false]);
    expect(extractOmniloopWorkflowIds(activities)).toEqual([]);
  });
});

describe("describeOmniloopWorkflows", () => {
  it("is silent once every workflow has settled", () => {
    expect(
      describeOmniloopWorkflows([workflow("wf_a", "completed"), workflow("wf_b", "failed")]),
    ).toBeNull();
    expect(describeOmniloopWorkflows([])).toBeNull();
  });

  it("names a single live workflow and links to it", () => {
    expect(describeOmniloopWorkflows([workflow("wf_a", "running", "review loop")])).toEqual({
      title: "Omniloop workflow running: review loop",
      workflowId: "wf_a",
    });
    expect(describeOmniloopWorkflows([workflow("wf_a", "paused", "review loop")])?.title).toBe(
      "Omniloop workflow paused: review loop",
    );
  });

  it("counts several live workflows and links to the newest", () => {
    const notice = describeOmniloopWorkflows([
      workflow("wf_done", "completed"),
      workflow("wf_a", "running"),
      workflow("wf_b", "pending"),
    ]);
    expect(notice).toEqual({ title: "2 omniloop workflows 1 running, 1 waiting", workflowId: "wf_b" });
  });
});
