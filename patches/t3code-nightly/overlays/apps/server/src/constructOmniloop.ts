// @effect-diagnostics nodeBuiltinImport:off globalFetch:off globalDate:off globalDateInEffect:off - talks to
// the omniloop daemon on the loopback interface and reads its config file on this VM.
import {
  ConstructOmniloopError,
  type ConstructOmniloopStatus,
  type ConstructOmniloopTicket,
  type ConstructOmniloopWorkflow,
  type ConstructOmniloopWorkflowStatus,
} from "@t3tools/contracts";
import * as Effect from "effect/Effect";
import * as NodeCrypto from "node:crypto";
import * as NodeFS from "node:fs";
import * as NodeOS from "node:os";
import * as NodePath from "node:path";

/** Proxy mount point on the T3 server: `/construct/omniloop/<ticket>/<omniloop path>`. */
export const CONSTRUCT_OMNILOOP_PROXY_PREFIX = "/construct/omniloop";
export const OMNILOOP_DEFAULT_PORT = 4700;
const TICKET_TTL_MS = 12 * 60 * 60 * 1000;
const TICKET_LIMIT = 64;
const PROBE_TIMEOUT_MS = 1500;
const WORKFLOW_TIMEOUT_MS = 4000;

const WORKFLOW_STATUSES: ReadonlySet<string> = new Set([
  "pending",
  "running",
  "paused",
  "completed",
  "failed",
  "cancelled",
  "timed_out",
]);

export interface OmniloopConfig {
  readonly port: number;
  readonly token: string | null;
}

/** `~/.omniloop/config.toml` (or `$OMNILOOP_HOME/config.toml`): only `port` and `token` matter. */
export function parseOmniloopConfig(toml: string, env: NodeJS.ProcessEnv = process.env): OmniloopConfig {
  let port = OMNILOOP_DEFAULT_PORT;
  let token: string | null = null;
  for (const raw of toml.split("\n")) {
    const line = raw.replace(/#.*$/, "").trim();
    const match = /^(port|token)\s*=\s*(.+)$/.exec(line);
    if (!match) continue;
    const value = match[2]!.trim().replace(/^["']|["']$/g, "");
    if (match[1] === "port" && /^\d+$/.test(value)) port = Number(value);
    if (match[1] === "token" && value) token = value;
  }
  const envPort = env.OMNILOOP_PORT?.trim();
  if (envPort && /^\d+$/.test(envPort)) port = Number(envPort);
  return { port, token };
}

export function omniloopConfigPath(env: NodeJS.ProcessEnv = process.env): string {
  const home = env.OMNILOOP_HOME?.trim();
  return NodePath.join(home ? home : NodePath.join(NodeOS.homedir(), ".omniloop"), "config.toml");
}

export function readOmniloopConfig(env: NodeJS.ProcessEnv = process.env): OmniloopConfig {
  let toml = "";
  try {
    toml = NodeFS.readFileSync(omniloopConfigPath(env), "utf8");
  } catch {
    // No config yet: the daemon has never started here. Defaults still describe it.
  }
  return parseOmniloopConfig(toml, env);
}

export const omniloopBaseUrl = (port: number): string => `http://127.0.0.1:${port}`;

/** True when the daemon answers its health check. */
export async function probeOmniloop(port: number): Promise<boolean> {
  try {
    const response = await fetch(`${omniloopBaseUrl(port)}/healthz`, {
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
    });
    return response.ok;
  } catch {
    return false;
  }
}

export const readOmniloopStatus = (
  env: NodeJS.ProcessEnv = process.env,
): Effect.Effect<ConstructOmniloopStatus, ConstructOmniloopError> =>
  Effect.tryPromise({
    try: async () => {
      const { port } = readOmniloopConfig(env);
      return { running: await probeOmniloop(port), port };
    },
    catch: (cause) => new ConstructOmniloopError({ message: describe(cause) }),
  });

// ── tickets ────────────────────────────────────────────────────────────────────
// A ticket is an opaque secret in the proxy path. The RPC that mints one is
// authorized like every other read RPC, so holding a ticket proves an
// authenticated T3 session issued it; the daemon's own token never has to
// reach a browser that cannot keep a session cookie.
export interface OmniloopTicketStore {
  mint(now?: number): { ticket: string; expiresAt: number };
  isValid(ticket: string, now?: number): boolean;
}

export function makeOmniloopTicketStore(
  ttlMs: number = TICKET_TTL_MS,
  limit: number = TICKET_LIMIT,
): OmniloopTicketStore {
  const tickets = new Map<string, number>();
  const prune = (now: number) => {
    for (const [ticket, expiresAt] of tickets) if (expiresAt <= now) tickets.delete(ticket);
    // Map iteration is insertion-ordered: the oldest tickets go first.
    while (tickets.size > limit) {
      const oldest = tickets.keys().next().value;
      if (oldest === undefined) break;
      tickets.delete(oldest);
    }
  };
  return {
    mint(now = Date.now()) {
      const ticket = NodeCrypto.randomBytes(24).toString("base64url");
      const expiresAt = now + ttlMs;
      tickets.set(ticket, expiresAt);
      prune(now);
      return { ticket, expiresAt };
    },
    isValid(ticket, now = Date.now()) {
      const expiresAt = tickets.get(ticket);
      return expiresAt !== undefined && expiresAt > now;
    },
  };
}

export const omniloopTicketStore = makeOmniloopTicketStore();

/** The dashboard entry point behind a ticket; the daemon's token rides along the
 *  way omniloop's own links carry it (the GUI stores it and unlocks admin actions). */
export function omniloopGuiPath(ticket: string, token: string | null): string {
  const query = token ? `?token=${encodeURIComponent(token)}` : "";
  return `${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/${ticket}/gui/index.html${query}`;
}

export const issueOmniloopTicket = (
  env: NodeJS.ProcessEnv = process.env,
  store: OmniloopTicketStore = omniloopTicketStore,
): Effect.Effect<ConstructOmniloopTicket, ConstructOmniloopError> =>
  Effect.sync(() => {
    const { token } = readOmniloopConfig(env);
    const { ticket, expiresAt } = store.mint();
    return {
      ticket,
      guiPath: omniloopGuiPath(ticket, token),
      expiresAt: new Date(expiresAt).toISOString(),
    };
  });

// ── workflow status ────────────────────────────────────────────────────────────
export function workflowStatusFromDetail(detail: unknown): ConstructOmniloopWorkflow | null {
  const root = detail as { workflow?: unknown } | null;
  const workflow = (root && typeof root === "object" && root.workflow ? root.workflow : root) as
    | { id?: unknown; name?: unknown; status?: unknown }
    | null;
  if (!workflow || typeof workflow !== "object" || typeof workflow.id !== "string") return null;
  const status = typeof workflow.status === "string" && WORKFLOW_STATUSES.has(workflow.status)
    ? (workflow.status as ConstructOmniloopWorkflowStatus)
    : "unknown";
  return { id: workflow.id, name: typeof workflow.name === "string" ? workflow.name : workflow.id, status };
}

export async function fetchOmniloopWorkflow(port: number, id: string): Promise<ConstructOmniloopWorkflow> {
  const unknown: ConstructOmniloopWorkflow = { id, name: id, status: "unknown" };
  if (!/^wf_[A-Za-z0-9_-]+$/.test(id)) return unknown;
  try {
    const response = await fetch(`${omniloopBaseUrl(port)}/api/workflows/${encodeURIComponent(id)}`, {
      signal: AbortSignal.timeout(WORKFLOW_TIMEOUT_MS),
    });
    if (!response.ok) return unknown;
    return workflowStatusFromDetail(await response.json()) ?? unknown;
  } catch {
    return unknown;
  }
}

export const readOmniloopWorkflows = (
  workflowIds: ReadonlyArray<string>,
  env: NodeJS.ProcessEnv = process.env,
): Effect.Effect<{ workflows: ReadonlyArray<ConstructOmniloopWorkflow> }, ConstructOmniloopError> =>
  Effect.tryPromise({
    try: async () => {
      const { port } = readOmniloopConfig(env);
      const ids = [...new Set(workflowIds)].slice(0, 32);
      return { workflows: await Promise.all(ids.map((id) => fetchOmniloopWorkflow(port, id))) };
    },
    catch: (cause) => new ConstructOmniloopError({ message: describe(cause) }),
  });

function describe(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
