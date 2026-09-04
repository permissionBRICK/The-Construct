import type { ConstructT3LinkInfo, DesktopBridge } from "@t3tools/contracts";
import {
  type AtomCommandResult,
  isAtomCommandInterrupted,
  squashAtomCommandFailure,
} from "@t3tools/client-runtime/state/runtime";

/**
 * Link ONE Construct instance's T3 server as a remote environment of this app (plan
 * §4.12 "T3 Desktop topology", auto-link). Shared by the automatic path
 * (ConstructAutoLink.tsx) and the Providers row's Link button so both do exactly the
 * same three steps:
 *
 *   1. ask the main process for a one-time pairing link — it runs
 *      Get-ConstructT3PairingLink.ps1 hidden, which mints the link on the VM over SSH,
 *   2. register the remote through the app's own connect command (what the Connections
 *      dialog does when a link is pasted; the environment takes the remote's own label),
 *   3. report the outcome back so the instance's state file remembers it — done once,
 *      backed off after a failure, and never re-added after the user removes it.
 *
 * Resolves with the outcome; never rejects. A step-2 interruption (the app is closing)
 * records nothing, so the next start tries again.
 */
export type ConstructLinkBridge = Pick<
  DesktopBridge,
  "linkConstructInstance" | "recordConstructInstanceT3Link"
>;

/** The app's own connect command (`connectPairing` in ~/connection/onboarding): it fetches
 *  the remote's descriptor, bootstraps the bearer session and persists the environment,
 *  resolving with the new environment's id. The environment's label is the remote's own
 *  (its descriptor), the same as when a link is pasted into the Connections dialog. */
export type ConstructConnectPairing = (input: {
  readonly pairingUrl: string;
}) => Promise<AtomCommandResult<unknown, unknown>>;

export type ConstructLinkOutcome =
  | { readonly ok: true; readonly environmentId: string; readonly baseUrl: string | null }
  | { readonly ok: false; readonly error: string; readonly interrupted: boolean };

/** The origin a pairing link names (the token rides in the fragment); null when the
 *  link is not an http(s) URL. Pure. */
export function constructPairingLinkOrigin(pairUrl: string): string | null {
  try {
    const url = new URL(pairUrl);
    if (url.protocol !== "http:" && url.protocol !== "https:") return null;
    return url.origin;
  } catch {
    return null;
  }
}

function marker(
  status: ConstructT3LinkInfo["status"],
  over: Partial<Omit<ConstructT3LinkInfo, "status" | "at">>,
  now: () => string,
): ConstructT3LinkInfo {
  return {
    status,
    at: now(),
    environmentId: over.environmentId ?? null,
    baseUrl: over.baseUrl ?? null,
    error: over.error ?? null,
  };
}

export async function linkConstructInstance(input: {
  readonly bridge: ConstructLinkBridge;
  readonly name: string;
  readonly connectPairing: ConstructConnectPairing;
  readonly now?: () => string;
}): Promise<ConstructLinkOutcome> {
  const now = input.now ?? (() => new Date().toISOString());
  const record = async (link: ConstructT3LinkInfo) => {
    try {
      await input.bridge.recordConstructInstanceT3Link(input.name, link);
    } catch {
      // The marker is bookkeeping; the link itself already happened (or failed).
    }
  };

  let minted: Awaited<ReturnType<ConstructLinkBridge["linkConstructInstance"]>>;
  try {
    minted = await input.bridge.linkConstructInstance(input.name);
  } catch (error) {
    minted = { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
  if (!minted.ok) {
    await record(marker("failed", { error: minted.error }, now));
    return { ok: false, error: minted.error, interrupted: false };
  }

  const baseUrl = constructPairingLinkOrigin(minted.pairUrl);
  const result = await input.connectPairing({ pairingUrl: minted.pairUrl });
  if (result._tag === "Failure") {
    if (isAtomCommandInterrupted(result)) {
      return { ok: false, error: "The link was interrupted.", interrupted: true };
    }
    const cause = squashAtomCommandFailure(result);
    const message = cause instanceof Error ? cause.message : "Could not add the remote.";
    await record(marker("failed", { baseUrl, error: message }, now));
    return { ok: false, error: message, interrupted: false };
  }

  const environmentId = String(result.value);
  await record(marker("linked", { environmentId, baseUrl }, now));
  return { ok: true, environmentId, baseUrl };
}
