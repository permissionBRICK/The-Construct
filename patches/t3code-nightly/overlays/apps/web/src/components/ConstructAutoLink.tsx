import { useEffect, useMemo, useRef } from "react";

import { connectPairing as connectPairingAtom } from "~/connection/onboarding";
import { isElectron } from "../env";
import { useDesktopUpdateState } from "../state/desktopUpdate";
import { useEnvironments } from "../state/environments";
import { useAtomCommand } from "../state/use-atom-command";
import { constructLinkedRemotes, planConstructAutoLink } from "./constructInstances.logic";
import { linkConstructInstance } from "./constructInstances.link";
import { getConstructUpdateInfo } from "./constructUpdate.logic";
import { stackedThreadToast, toastManager } from "./ui/toast";

/**
 * Auto-link (plan §4.12 "T3 Desktop topology"): every Construct VM of this PC that runs
 * T3 and is not yet one of this app's remote environments gets linked on its own — the
 * default local VM included. Runs off the same facts the Providers page renders (the
 * Construct update state's instances + the app's environment list), through
 * planConstructAutoLink's rule: never twice, never a connection the user removed,
 * backed off after a failure, nothing while a Construct script runs.
 *
 * One attempt per instance per app session: the marker the main process records is
 * what the NEXT session consults, and an attempt whose marker could not be written must
 * not loop meanwhile. Mounted at the root; renders nothing, and nothing in browsers or
 * stock builds.
 */
export function ConstructAutoLink() {
  return isElectron ? <ConstructAutoLinkContent /> : null;
}

function ConstructAutoLinkContent() {
  const state = useDesktopUpdateState();
  const info = getConstructUpdateInfo(state);
  const { environments } = useEnvironments();
  const connectPairing = useAtomCommand(connectPairingAtom, { reportFailure: false });
  const attemptedRef = useRef(new Set<string>());

  const remotes = useMemo(
    () =>
      constructLinkedRemotes(
        environments.map((environment) => ({
          environmentId: String(environment.environmentId),
          label: environment.label,
          displayUrl: environment.displayUrl,
          targetKind: environment.entry.target._tag,
        })),
      ),
    [environments],
  );

  useEffect(() => {
    const bridge = window.desktopBridge;
    if (!bridge || info === null) return;
    if (typeof bridge.linkConstructInstance !== "function") return;
    const names = planConstructAutoLink({
      info,
      remotes,
      inFlight: attemptedRef.current,
      now: Date.now(),
    });
    for (const name of names) {
      attemptedRef.current.add(name);
      void linkConstructInstance({ bridge, name, connectPairing }).then((outcome) => {
        if (outcome.ok) {
          toastManager.add(
            stackedThreadToast({
              type: "success",
              title: `Linked ${name}`,
              description: `The Construct VM "${name}" was added as a remote environment. It reconnects on app startup.`,
            }),
          );
          return;
        }
        if (outcome.interrupted) {
          // The app is closing; the next start tries again.
          attemptedRef.current.delete(name);
          return;
        }
        toastManager.add(
          stackedThreadToast({
            type: "error",
            title: `Could not link ${name}`,
            description: `${outcome.error} Retry from Settings → Providers → Construct.`,
          }),
        );
      });
    }
  }, [connectPairing, info, remotes]);

  return null;
}
