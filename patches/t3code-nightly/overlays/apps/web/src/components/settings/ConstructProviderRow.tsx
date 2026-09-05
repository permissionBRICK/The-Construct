import { ArrowUpCircleIcon, LoaderIcon, RefreshCwIcon, ServerCogIcon } from "lucide-react";
import { useCallback, useMemo, useState } from "react";

import { connectPairing as connectPairingAtom } from "~/connection/onboarding";
import { isElectron } from "../../env";
import { cn } from "../../lib/utils";
import { useDesktopUpdateState } from "../../state/desktopUpdate";
import { useEnvironments } from "../../state/environments";
import { useAtomCommand } from "../../state/use-atom-command";
import { startConstructUpdate } from "../constructUpdate";
import { linkConstructInstance } from "../constructInstances.link";
import {
  constructLinkedRemotes,
  getConstructRowDetail,
  planConstructProviderRows,
  type ConstructProviderRow,
} from "../constructInstances.logic";
import {
  getConstructDisabledReason,
  getConstructUpdateActionLabel,
  getConstructUpdateDetail,
  getConstructUpdateHeadline,
  getConstructUpdateInfo,
  getConstructVersionLabel,
} from "../constructUpdate.logic";
import { Button } from "../ui/button";
import { stackedThreadToast, toastManager } from "../ui/toast";
import { Tooltip, TooltipPopup, TooltipTrigger } from "../ui/tooltip";
import { SettingsSection } from "./settingsLayout";

/**
 * Construct's entry on the Providers page of a Construct-built Desktop app. It is not a
 * chat provider — you cannot pick it as a harness — but like the Codex/Claude rows it
 * tracks an installed version (the Construct commit on this PC) against its upstream
 * (the GitHub ref) and against what the VM runs (the provisioned commit), plus the
 * upstream T3 Code release, and offers the one-click fix: update Construct here, or
 * reprovision the VM. It lives in its own section rather than inside an environment's
 * provider list because it describes this PC, and it matters most exactly when the VM
 * is offline or not yet connected — when the environment list is not rendered at all.
 * Renders nothing in browsers and stock builds.
 *
 * ONE ROW PER LINKED REMOTE (B14, plan §4.12 "T3 Desktop updater"): T3 links several
 * remotes at once, so under the host-wide Construct row there is one row per remote this
 * app is connected to. A remote that matches an instance in this PC's registry carries
 * THAT VM's provisioned commit, its own stale state and its own upstream T3 release, and
 * its Reprovision targets it by name; a remote that matches nothing is shown read-only,
 * saying so, rather than being hidden. `Update Construct` stays the single host-wide
 * action on the Construct row — Update-Construct.ps1 takes no target.
 */
export function ConstructProviderSection() {
  return isElectron ? <ConstructProviderSectionContent /> : null;
}

function ConstructProviderSectionContent() {
  const state = useDesktopUpdateState();
  const info = getConstructUpdateInfo(state);
  const [isBusy, setIsBusy] = useState(false);
  // The remotes this app is LINKED to. `constructLinkedRemotes` decides which of the
  // app's environments are one, from T3's own statement of what each connection IS
  // (`entry.target._tag`) rather than from what its address looks like.
  const { environments } = useEnvironments();
  const rows = useMemo(
    () =>
      planConstructProviderRows({
        remotes: constructLinkedRemotes(
          environments.map((environment) => ({
            environmentId: String(environment.environmentId),
            label: environment.label,
            displayUrl: environment.displayUrl,
            targetKind: environment.entry.target._tag,
          })),
        ),
        info,
        t3LatestByChannel: info?.t3LatestByChannel ?? {},
      }),
    [environments, info],
  );

  const runAction = useCallback(async () => {
    const bridge = window.desktopBridge;
    if (!bridge || info === null || isBusy) return;
    setIsBusy(true);
    try {
      if (info.action !== null) {
        await startConstructUpdate(bridge, info);
        return;
      }
      const result = await bridge.checkForUpdate();
      if (!result.checked) {
        toastManager.add(
          stackedThreadToast({
            type: "error",
            title: "Could not check for Construct updates",
            description: result.state.message ?? "Construct-managed updates are disabled.",
          }),
        );
      }
    } catch (error) {
      toastManager.add(
        stackedThreadToast({
          type: "error",
          title: "Could not check for Construct updates",
          description: error instanceof Error ? error.message : "Update check failed.",
        }),
      );
    } finally {
      setIsBusy(false);
    }
  }, [info, isBusy]);

  if (info === null) return null;

  const headline = getConstructUpdateHeadline(info);
  const disabledReason = getConstructDisabledReason(state);
  const detail =
    disabledReason === null
      ? getConstructUpdateDetail(info)
      : `${getConstructUpdateDetail(info)} ${disabledReason}`;
  const running = info.runningAction !== null;
  const checking = state?.status === "checking";
  const hasOffer = info.action !== null && !running;
  const needsAttention = info.error !== null && !hasOffer && !running;

  return (
    <SettingsSection title="Construct" id="construct-updates">
      <div className="mx-3 overflow-hidden rounded-lg border border-border/70 sm:mx-4">
        <div className="p-1">
          <div className="group flex min-h-19 items-start gap-3 rounded-md px-3 py-2 transition-colors hover:bg-foreground/4">
            <span className="mt-0.5 inline-flex size-4 shrink-0 items-center justify-center text-muted-foreground">
              <ServerCogIcon aria-hidden className="size-4" />
            </span>
            <span className="min-w-0 flex-1">
              <span className="flex min-w-0 items-center gap-2">
                <span className="truncate text-sm font-medium text-foreground">Construct</span>
                <code className="text-xs text-muted-foreground">
                  {getConstructVersionLabel(info)}
                </code>
                {hasOffer ? (
                  <span role="img" aria-label="Update available" className="inline-flex shrink-0">
                    <ArrowUpCircleIcon className="size-3.5 text-update-foreground" />
                  </span>
                ) : null}
              </span>
              <Tooltip>
                <TooltipTrigger
                  render={
                    <span className="mt-0.5 flex items-start gap-1.5 text-[13px] leading-[1.45] text-muted-foreground/80">
                      {needsAttention ? (
                        <span className="flex h-[1.45em] shrink-0 items-center">
                          <span className="size-1.5 shrink-0 rounded-full bg-warning" aria-hidden />
                        </span>
                      ) : null}
                      <span className="line-clamp-2 [overflow-wrap:anywhere]">
                        {headline}
                        {" · "}
                        {detail}
                      </span>
                    </span>
                  }
                />
                <TooltipPopup
                  side="bottom"
                  align="start"
                  className="max-w-[min(24rem,calc(100vw-2rem))]"
                >
                  <div className="grid gap-1 text-left">
                    <span className="text-sm font-medium">{headline}</span>
                    <span className="text-xs text-muted-foreground">{detail}</span>
                    <span className="text-xs text-muted-foreground">
                      Construct {info.repo}@{info.ref} · VM {info.vmName} ({info.vmHost}) · T3 Code{" "}
                      {info.t3Version}
                      {info.t3LatestVersion !== null ? ` (upstream ${info.t3LatestVersion})` : ""}
                    </span>
                  </div>
                </TooltipPopup>
              </Tooltip>
            </span>
            <span className="flex h-5 shrink-0 items-center">
              <Button
                type="button"
                size="xs"
                variant={hasOffer ? "default" : "outline"}
                disabled={isBusy || running || checking || disabledReason !== null}
                title={disabledReason ?? undefined}
                onClick={() => void runAction()}
                aria-label={
                  hasOffer && info.action !== null
                    ? getConstructUpdateActionLabel(info.action)
                    : "Check for Construct updates"
                }
              >
                {running || checking ? (
                  <LoaderIcon className={cn("size-3", "animate-spin")} />
                ) : hasOffer ? null : (
                  <RefreshCwIcon className="size-3" />
                )}
                {running
                  ? "Running…"
                  : checking
                    ? "Checking…"
                    : hasOffer && info.action !== null
                      ? getConstructUpdateActionLabel(info.action)
                      : "Check"}
              </Button>
            </span>
          </div>
          {rows.map((row) => (
            <ConstructInstanceRow key={row.id} row={row} disabledReason={disabledReason} />
          ))}
        </div>
      </div>
    </SettingsSection>
  );
}

/**
 * One linked remote, or one UNLINKED instance. A linked row this PC's registry
 * recognises offers **Reprovision** for THAT instance (`reprovisionConstructInstance`,
 * which runs `Update-T3Code.ps1 -InstanceName <name>`); a remote that matches nothing
 * is read-only and says why. An instance of the registry that runs T3 but that no
 * remote matches offers **Link** — the manual counterpart of the automatic link
 * (ConstructAutoLink.tsx), which also re-adds a connection the user removed by hand.
 */
function ConstructInstanceRow({
  row,
  disabledReason,
}: {
  readonly row: ConstructProviderRow;
  readonly disabledReason: string | null;
}) {
  const [isBusy, setIsBusy] = useState(false);
  const detail = getConstructRowDetail(row);
  const offer = row.provisionStale || row.t3UpdateAvailable;
  const connectPairing = useAtomCommand(connectPairingAtom, { reportFailure: false });

  const link = useCallback(async () => {
    const bridge = window.desktopBridge;
    if (!bridge || row.instanceName === null || isBusy) return;
    if (typeof bridge.linkConstructInstance !== "function") {
      toastManager.add(
        stackedThreadToast({
          type: "error",
          title: `Could not link ${row.instanceName}`,
          description: "This Desktop build cannot mint pairing links; pair the VM from the Construct panel instead.",
        }),
      );
      return;
    }
    setIsBusy(true);
    try {
      const outcome = await linkConstructInstance({ bridge, name: row.instanceName, connectPairing });
      if (outcome.ok) {
        toastManager.add(
          stackedThreadToast({
            type: "success",
            title: `Linked ${row.instanceName}`,
            description: "The VM's T3 server was added as a remote environment. It reconnects on app startup.",
          }),
        );
      } else if (!outcome.interrupted) {
        toastManager.add(
          stackedThreadToast({
            type: "error",
            title: `Could not link ${row.instanceName}`,
            description: outcome.error,
          }),
        );
      }
    } finally {
      setIsBusy(false);
    }
  }, [connectPairing, isBusy, row.instanceName]);

  const reprovision = useCallback(async () => {
    const bridge = window.desktopBridge;
    if (!bridge || row.instanceName === null || isBusy) return;
    setIsBusy(true);
    try {
      const result = await bridge.reprovisionConstructInstance(row.instanceName);
      toastManager.add(
        result.accepted
          ? stackedThreadToast({
              type: "info",
              title: `Reprovisioning ${row.instanceName}`,
              description:
                "Follow the Construct reprovision console window. When it installs a new T3 Code Desktop build, this app closes and reopens on its own.",
            })
          : stackedThreadToast({
              type: "error",
              title: `Could not reprovision ${row.instanceName}`,
              description:
                result.state.message ?? "Another Construct action is running, or updates are disabled.",
            }),
      );
    } catch (error) {
      toastManager.add(
        stackedThreadToast({
          type: "error",
          title: `Could not reprovision ${row.instanceName}`,
          description: error instanceof Error ? error.message : "An unexpected error occurred.",
        }),
      );
    } finally {
      setIsBusy(false);
    }
  }, [isBusy, row.instanceName]);

  return (
    <div className="group flex min-h-19 items-start gap-3 rounded-md px-3 py-2 transition-colors hover:bg-foreground/4">
      <span className="mt-0.5 inline-flex size-4 shrink-0 items-center justify-center text-muted-foreground">
        <ServerCogIcon aria-hidden className="size-4" />
      </span>
      <span className="min-w-0 flex-1">
        <span className="flex min-w-0 items-center gap-2">
          <span className="truncate text-sm font-medium text-foreground">{row.label}</span>
          <code className="text-xs text-muted-foreground">
            {row.host}
            {row.port === null ? "" : `:${row.port}`}
          </code>
          {offer ? (
            <span role="img" aria-label="Reprovision available" className="inline-flex shrink-0">
              <ArrowUpCircleIcon className="size-3.5 text-update-foreground" />
            </span>
          ) : null}
        </span>
        <span className="mt-0.5 flex items-start gap-1.5 text-[13px] leading-[1.45] text-muted-foreground/80">
          <span className="line-clamp-2 [overflow-wrap:anywhere]">{detail}</span>
        </span>
      </span>
      <span className="flex h-5 shrink-0 items-center">
        {row.instanceName === null ? null : !row.linked ? (
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={isBusy || !row.canLink || disabledReason !== null}
            title={disabledReason ?? (row.canLink ? undefined : row.note)}
            onClick={() => void link()}
            aria-label={`Link ${row.instanceName}`}
          >
            {isBusy ? <LoaderIcon className={cn("size-3", "animate-spin")} /> : null}
            Link
          </Button>
        ) : (
          <Button
            type="button"
            size="xs"
            variant={offer ? "default" : "outline"}
            disabled={isBusy || !row.canReprovision || disabledReason !== null}
            title={disabledReason ?? (row.canReprovision ? undefined : row.note)}
            onClick={() => void reprovision()}
            aria-label={`Reprovision ${row.instanceName}`}
          >
            {isBusy ? <LoaderIcon className={cn("size-3", "animate-spin")} /> : null}
            Reprovision
          </Button>
        )}
      </span>
    </div>
  );
}
