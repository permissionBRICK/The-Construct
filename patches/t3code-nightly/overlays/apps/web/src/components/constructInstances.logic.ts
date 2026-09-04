import type { ConstructInstanceInfo, ConstructUpdateInfo } from "@t3tools/contracts";

/**
 * Pairing the remotes T3 is LINKED TO with the Construct VMs this PC manages
 * (B14, plan §4.12 "T3 Desktop updater").
 *
 * T3 Code Desktop links several remotes at once, so the Providers page cannot show one
 * "Construct" row about one VM any more: it shows one row per linked remote. The two
 * halves of that live in different processes and neither duplicates the other —
 *
 *   apps/desktop/src/updates/ConstructUpdates.ts   reads the filesystem: the instance
 *                                                  registry, each VM's own state, and the
 *                                                  launch plan for one named instance.
 *   this file (renderer)                           knows which remotes the app is
 *                                                  actually connected to, and pairs them.
 *
 * Pure: no React, no Effect, no IPC — it takes the facts and returns rows.
 */

/** The T3 web GUI's ports on a VM reached at its own address: plain HTTP and the TLS
 *  proxy (`T3CODE_PORT` / `T3CODE_HTTPS_PORT`; bin/setup-t3-https.sh). A VM behind a host
 *  forward answers on an allocated port instead, which is why a port RECORDED for the
 *  instance always wins over this list. */
export const CONSTRUCT_T3_PORTS: ReadonlyArray<number> = [5177, 5178];

/** One remote the app is linked to, as its environment catalog knows it. */
export interface ConstructLinkedRemote {
  readonly id: string;
  readonly baseUrl: string;
  /** What the app calls it, when it has a name of its own. */
  readonly label?: string;
}

/** One entry of the app's environment list, as much of it as this module needs.
 *  `targetKind` is `entry.target._tag` — T3's own statement of what the connection IS. */
export interface ConstructEnvironmentLike {
  readonly environmentId: string;
  readonly label: string;
  /** The address the connection was made at; null when the target carries none. */
  readonly displayUrl: string | null;
  readonly targetKind: string;
}

/** The connection kind a LINKED REMOTE has: a server this app was pointed at by URL.
 *  `PrimaryConnectionTarget` is the app's OWN bundled server (never a remote, whatever
 *  address it is reached at), and Relay/Ssh targets carry no HTTP origin to match. */
export const CONSTRUCT_LINKED_TARGET_KIND = "BearerConnectionTarget";

/**
 * The LINKED REMOTES among the app's environments — the rows the Providers page is about.
 *
 * The KIND decides, not the address: T3 states what each connection is
 * (`entry.target._tag`), so a linked remote is a `BearerConnectionTarget` and the app's
 * own bundled server (`PrimaryConnectionTarget`) is not one no matter which address it
 * happens to be reached at. Guessing from the URL would get both cases wrong in practice
 * — a linked remote tunnelled through loopback would be dropped, and a primary server on
 * a LAN address would be rendered as a remote.
 *
 * An entry whose address is not an http(s) URL is still dropped afterwards: there is no
 * host:port to match, so nothing could be said about it either way.
 *
 * Pure, and separated from the React component so this mapping is unit-tested rather than
 * only the planner it feeds.
 */
export function constructLinkedRemotes(
  environments: ReadonlyArray<ConstructEnvironmentLike>,
): ReadonlyArray<ConstructLinkedRemote> {
  const out: ConstructLinkedRemote[] = [];
  for (const environment of environments) {
    if (environment.targetKind !== CONSTRUCT_LINKED_TARGET_KIND) continue;
    if (environment.displayUrl === null) continue;
    if (constructRemoteEndpoint(environment.displayUrl) === null) continue;
    out.push({
      id: String(environment.environmentId),
      baseUrl: environment.displayUrl,
      label: environment.label,
    });
  }
  return out;
}

export interface ConstructRemoteEndpoint {
  readonly host: string;
  readonly port: number;
}

/** The host and port of a remote's base URL, lower-cased and unbracketed; null when the
 *  string is not an http(s) URL. The port is always resolved (the scheme's default when
 *  the URL states none), because it is half of the matching key. Pure. */
export function constructRemoteEndpoint(baseUrl: string): ConstructRemoteEndpoint | null {
  const raw = String(baseUrl ?? "").trim();
  if (!raw) return null;
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return null;
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") return null;
  let host = url.hostname.toLowerCase();
  if (host.startsWith("[") && host.endsWith("]")) host = host.slice(1, -1);
  if (!host) return null;
  const port = url.port ? Number(url.port) : url.protocol === "https:" ? 443 : 80;
  if (!Number.isInteger(port) || port <= 0 || port > 65535) return null;
  return { host, port };
}

/** Does this instance answer T3 on that port? Pure — and it is a real answer, never
 *  "any port will do".
 *
 *  1. The port the provisioner PUBLISHED for this VM (`t3Port` — the origin the guest
 *     actually serves, including a host forward's allocated port) is the answer whenever
 *     this PC has one.
 *  2. Without one, a VM reached at its OWN address answers on the T3 ports Construct
 *     configures it with (`T3CODE_PORT` / `T3CODE_HTTPS_PORT`) — a documented default,
 *     not a guess.
 *  3. A VM published by a host SERVICE with no recorded port matches NOTHING: its
 *     forward's port is allocated by the service and is not derivable here, so the honest
 *     answer is "this PC has not seen that VM's T3 yet" (its row says so and the next
 *     reprovision records it) rather than claiming a remote on an unknown port is it. */
function instanceAnswersOnPort(instance: ConstructInstanceInfo, port: number): boolean {
  if (instance.t3Port !== null) return instance.t3Port === port;
  const publishedByService = instance.publicHost !== null && instance.publicHost !== "";
  return publishedByService ? false : CONSTRUCT_T3_PORTS.includes(port);
}

/** Which instance is reachable at this base URL, or null. Both halves of `host:port` are
 *  used (see instanceAnswersOnPort); a host+port TWO instances claim is ambiguous and
 *  matches neither, because reprovisioning the wrong machine is worse than offering no
 *  button. Pure. */
export function matchConstructRemoteToInstance(
  baseUrl: string,
  instances: ReadonlyArray<ConstructInstanceInfo>,
): ConstructInstanceInfo | null {
  const endpoint = constructRemoteEndpoint(baseUrl);
  if (endpoint === null) return null;
  // publicHost / vmHost ONLY. An ssh ALIAS is not evidence about an HTTP origin — it is
  // a name in ~/.ssh/config that nothing outside ssh resolves — so a remote reached at
  // something that merely equals an alias is not proof it is that VM, and offering its
  // Reprovision would rebuild a machine on a coincidence.
  const byHost = instances.filter((instance) =>
    [instance.publicHost, instance.vmHost].some(
      (name) => typeof name === "string" && name.toLowerCase() === endpoint.host,
    ),
  );
  const hits = byHost.filter((instance) => instanceAnswersOnPort(instance, endpoint.port));
  if (hits.length === 1) return hits[0]!;
  // A service-published VM whose T3 port this PC has not recorded yet (provisioned before
  // the port was written, or by another PC) is still THE instance at that host when it
  // is the only one claiming it: the host name is a per-VM name on a service that
  // renders one, so one claimant is not a coincidence. The next reprovision records the
  // port and the exact rule takes over.
  if (hits.length === 0 && byHost.length === 1) {
    const only = byHost[0]!;
    const publishedByService = only.t3Port === null && only.publicHost !== null && only.publicHost !== "";
    if (publishedByService) return only;
  }
  return null;
}

export interface ConstructProviderRow {
  readonly id: string;
  readonly baseUrl: string;
  /** The instance name for a Construct VM, else the app's own label / the host. */
  readonly label: string;
  readonly instanceName: string | null;
  readonly host: string;
  readonly port: number | null;
  /** The Construct this PC has installed; the same for every row. */
  readonly installedCommit: string | null;
  /** What THIS instance was last provisioned with. */
  readonly provisionedCommit: string | null;
  readonly provisionStale: boolean;
  /** The T3 release this Desktop build was made from. */
  readonly t3Version: string;
  /** The newest upstream release on THIS INSTANCE's channel; null when unknown. */
  readonly t3LatestVersion: string | null;
  readonly t3UpdateAvailable: boolean;
  /** Is a per-row Reprovision offered? False for a remote that is not a Construct
   *  instance of this PC, and while another Construct action is running. */
  readonly canReprovision: boolean;
  /** Why there is no button, for the read-only row. "" when there is one. */
  readonly note: string;
  /** Is this row a remote the app is LINKED to (true), or an instance of this PC's
   *  registry that no linked remote matches (false)? */
  readonly linked: boolean;
  /** Is a Link offered? Only for an unlinked instance whose VM runs T3. */
  readonly canLink: boolean;
}

/** Does this instance have a T3 server worth linking? The provisioner records the
 *  origin (`t3BaseUrl`) / port (`t3Port`) of every VM it set T3 up on, and the VM's own
 *  saved opt-in (`t3Enabled`) covers the default local VM, whose origin is never
 *  recorded (its ports are the documented defaults). Pure. */
export function constructInstanceHasT3(instance: ConstructInstanceInfo): boolean {
  return instance.t3Enabled === true || instance.t3Port !== null || instance.t3BaseUrl !== null;
}

/** The instances at least one linked remote matches (matchConstructRemoteToInstance's
 *  rule, so "linked" here means exactly what the Providers rows show). Pure. */
export function constructLinkedInstanceNames(
  remotes: ReadonlyArray<ConstructLinkedRemote>,
  instances: ReadonlyArray<ConstructInstanceInfo>,
): ReadonlySet<string> {
  const names = new Set<string>();
  for (const remote of remotes) {
    const instance = matchConstructRemoteToInstance(remote.baseUrl, instances);
    if (instance !== null) names.add(instance.name);
  }
  return names;
}

/** How long a failed automatic link waits before it is tried again. */
export const CONSTRUCT_AUTO_LINK_RETRY_MS = 30 * 60 * 1000;

/**
 * Which instances the app should link NOW, on its own (plan §4.12 "T3 Desktop
 * topology", auto-link): every instance of this PC's registry that runs T3 and that no
 * linked remote matches — the default local VM included —
 *
 *   * never one that is already linked,
 *   * never one whose marker says "linked": the user removed that connection by hand,
 *     and the automatic path must not put it back (the row's manual Link still can),
 *   * a failed one only after CONSTRUCT_AUTO_LINK_RETRY_MS, so a VM that is off does
 *     not get an SSH attempt every poll,
 *   * never one an attempt is already in flight for,
 *   * nothing at all while a Construct script runs (a reprovision is rebuilding the
 *     very server a link would pair with).
 *
 * Pure.
 */
export function planConstructAutoLink(input: {
  readonly info: ConstructUpdateInfo | null;
  readonly remotes: ReadonlyArray<ConstructLinkedRemote>;
  readonly inFlight: ReadonlySet<string>;
  readonly now: number;
  readonly retryAfterMs?: number;
}): ReadonlyArray<string> {
  const info = input.info;
  if (info === null || info.runningAction !== null || info.scriptsDir === null) return [];
  const retryAfter = input.retryAfterMs ?? CONSTRUCT_AUTO_LINK_RETRY_MS;
  const linked = constructLinkedInstanceNames(input.remotes, info.instances);
  const out: string[] = [];
  for (const instance of info.instances) {
    if (!constructInstanceHasT3(instance)) continue;
    if (linked.has(instance.name)) continue;
    if (input.inFlight.has(instance.name)) continue;
    const marker = instance.t3Link ?? null;
    if (marker !== null) {
      if (marker.status === "linked") continue;
      const at = Date.parse(marker.at);
      if (!Number.isNaN(at) && input.now - at < retryAfter) continue;
    }
    out.push(instance.name);
  }
  return out;
}

/** Is `latest` newer than `installed` on the same channel? The renderer's copy of the
 *  question ConstructUpdates.isNewerConstructT3Version answers for the app's own build:
 *  here it is asked per row, and a mismatch of channel is never an update. Deliberately
 *  string-conservative — a version it cannot compare is not an offer. Pure. */
function isNewerT3Version(latest: string | null, installed: string): boolean {
  if (latest === null || latest === installed) return false;
  const parts = (value: string) =>
    value
      .split(/[.+-]/)
      .map((chunk) => (/^\d+$/.test(chunk) ? Number(chunk) : chunk))
      .filter((chunk) => chunk !== "");
  const a = parts(latest);
  const b = parts(installed);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const x = a[i];
    const y = b[i];
    if (x === undefined) return false;
    if (y === undefined) return true;
    if (x === y) continue;
    if (typeof x === "number" && typeof y === "number") return x > y;
    return String(x) > String(y);
  }
  return false;
}

/**
 * One Providers row per LINKED REMOTE. Rows for remotes this PC's registry knows carry
 * that instance's own staleness and a Reprovision that targets it BY NAME; a remote that
 * matches nothing gets a read-only row saying so, rather than being hidden — the user
 * linked it, and silently dropping it would look like the app lost the connection.
 *
 * "Update Construct" stays a single host-wide action (Update-Construct.ps1 takes no
 * target), so it is deliberately not part of these rows.
 *
 * Pure.
 */
export function planConstructProviderRows(input: {
  readonly remotes: ReadonlyArray<ConstructLinkedRemote>;
  readonly info: ConstructUpdateInfo | null;
  /** Newest upstream release per channel. The app's own channel is always known; the
   *  other one only when an instance runs it (the main process asks npm for it then). */
  readonly t3LatestByChannel?: Readonly<Record<string, string | null>>;
}): ReadonlyArray<ConstructProviderRow> {
  const info = input.info;
  const instances = info?.instances ?? [];
  const installedCommit = info?.installedCommit ?? null;
  const running = info?.runningAction ?? null;
  const t3Version = info?.t3Version ?? "";
  const latestByChannel = input.t3LatestByChannel ?? {};
  const linkedNames = constructLinkedInstanceNames(input.remotes, instances);
  // UNLINKED instances follow the linked remotes: one row each for a VM of this PC's
  // registry that runs T3 but that the app is not connected to, offering Link (the
  // manual counterpart of the automatic link, which ignores the marker's "linked"
  // memory of a connection the user removed). A VM without T3 has nothing to link to
  // and gets no row.
  const unlinkedRows: ConstructProviderRow[] = instances
    .filter((instance) => !linkedNames.has(instance.name) && constructInstanceHasT3(instance))
    .map((instance) => {
      const endpoint = instance.t3BaseUrl === null ? null : constructRemoteEndpoint(instance.t3BaseUrl);
      const marker = instance.t3Link ?? null;
      const note =
        marker === null
          ? "Not linked in this app yet."
          : marker.status === "failed"
            ? `Automatic link failed: ${marker.error ?? "unknown error"}`
            : "Linked before; the connection was removed from this app.";
      return {
        id: `instance:${instance.name}`,
        baseUrl: instance.t3BaseUrl ?? "",
        label: instance.name,
        instanceName: instance.name,
        host: endpoint?.host ?? instance.publicHost ?? instance.vmHost,
        port: endpoint?.port ?? instance.t3Port,
        installedCommit,
        provisionedCommit: instance.provisionedCommit,
        provisionStale: false,
        t3Version,
        t3LatestVersion: null,
        t3UpdateAvailable: false,
        canReprovision: false,
        note: running === null ? note : "A Construct action is already running.",
        linked: false,
        canLink: running === null,
      };
    });
  return [...input.remotes.map((remote) => {
    const endpoint = constructRemoteEndpoint(remote.baseUrl);
    const instance = matchConstructRemoteToInstance(remote.baseUrl, instances);
    const host = endpoint?.host ?? "";
    const base = {
      id: remote.id,
      baseUrl: remote.baseUrl,
      host,
      port: endpoint?.port ?? null,
      installedCommit,
      t3Version,
      linked: true,
      canLink: false,
    };
    if (instance === null) {
      return {
        ...base,
        label: remote.label ?? (host || remote.baseUrl),
        instanceName: null,
        provisionedCommit: null,
        provisionStale: false,
        t3LatestVersion: null,
        t3UpdateAvailable: false,
        canReprovision: false,
        note: `Not a Construct instance of this PC${host ? ` (${host})` : ""} — Construct can only reprovision VMs listed in its instance registry.`,
      };
    }
    const own = instance.provisionedCommit;
    // Plain commit-string inequality (plan §4.12 "Stale detection"), never a history
    // lookup: it has to keep working when the compare API cannot resolve the base.
    const stale = installedCommit !== null && own !== null && installedCommit !== own;
    // The upstream release on THIS instance's channel. An instance whose channel this PC
    // does not know reports no upstream version rather than the app's own.
    const latest =
      instance.channel === null ? null : (latestByChannel[instance.channel] ?? null);
    return {
      ...base,
      label: instance.name,
      instanceName: instance.name,
      provisionedCommit: own,
      provisionStale: stale,
      t3LatestVersion: latest,
      t3UpdateAvailable: isNewerT3Version(latest, t3Version),
      canReprovision: running === null,
      note: running === null ? "" : "A Construct action is already running.",
    };
  }), ...unlinkedRows];
}

/** The one-line detail under a row: what this instance is, and what it needs. Pure. */
export function getConstructRowDetail(row: ConstructProviderRow): string {
  if (row.instanceName === null) return row.note;
  if (!row.linked) return row.note;
  const parts: string[] = [];
  parts.push(
    row.provisionedCommit === null
      ? "provisioned commit unknown"
      : `provisioned ${row.provisionedCommit.slice(0, 7)}`,
  );
  if (row.provisionStale) parts.push("reprovision pending");
  if (row.t3UpdateAvailable && row.t3LatestVersion !== null) {
    parts.push(`T3 Code ${row.t3LatestVersion} available`);
  }
  if (row.note !== "") parts.push(row.note);
  return parts.join(" · ");
}
