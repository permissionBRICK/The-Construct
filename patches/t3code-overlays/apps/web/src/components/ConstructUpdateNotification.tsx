import { useNavigate } from "@tanstack/react-router";
import { useCallback, useEffect, useRef } from "react";

import { isElectron } from "../env";
import { useDismissedProviderUpdateNotificationKeys } from "../providerUpdateDismissal";
import { useDesktopUpdateState } from "../state/desktopUpdate";
import { startConstructUpdate } from "./constructUpdate";
import {
  getConstructDisabledReason,
  getConstructUpdateActionLabel,
  getConstructUpdateDetail,
  getConstructUpdateHeadline,
  getConstructUpdateInfo,
  getConstructUpdateNotificationKey,
} from "./constructUpdate.logic";
import { stackedThreadToast, toastManager } from "./ui/toast";

const seenConstructUpdateNotificationKeys = new Set<string>();
type ConstructToastId = ReturnType<typeof toastManager.add>;

/**
 * The Construct update popup: when the Desktop app starts offering an action (a Construct
 * update for this PC, or a VM reprovision because the VM is behind the installed Construct
 * or a newer upstream T3 Code exists), raise one prompt per distinct offer with the action
 * as its primary button. Closing it dismisses that offer for good (same persisted
 * dismissal store as the provider update prompts); the sidebar pill and the Providers
 * entry keep showing it. Nothing renders in browsers or stock Desktop builds.
 */
export function ConstructUpdateNotification() {
  return isElectron ? <ConstructUpdateNotificationContent /> : null;
}

function ConstructUpdateNotificationContent() {
  const navigate = useNavigate();
  const state = useDesktopUpdateState();
  const info = getConstructUpdateInfo(state);
  // Nothing to offer while updates are disabled: the facts stay visible in Settings.
  const notificationKey =
    info === null || getConstructDisabledReason(state) !== null
      ? null
      : getConstructUpdateNotificationKey(info);
  const { dismissedNotificationKeys, dismissNotificationKey } =
    useDismissedProviderUpdateNotificationKeys();
  const activeToastRef = useRef<{
    readonly key: string;
    readonly toastId: ConstructToastId;
  } | null>(null);
  const infoRef = useRef(info);
  infoRef.current = info;

  const closeActiveToast = useCallback(() => {
    const active = activeToastRef.current;
    if (active === null) return;
    toastManager.close(active.toastId);
    activeToastRef.current = null;
  }, []);

  useEffect(() => closeActiveToast, [closeActiveToast]);

  useEffect(() => {
    // The offer changed (or went away, e.g. the script was launched elsewhere): drop a
    // prompt the user has not acted on.
    const active = activeToastRef.current;
    if (active !== null && active.key !== notificationKey) {
      closeActiveToast();
    }
    if (
      info === null ||
      notificationKey === null ||
      dismissedNotificationKeys.has(notificationKey) ||
      seenConstructUpdateNotificationKeys.has(notificationKey) ||
      activeToastRef.current !== null
    ) {
      return;
    }
    const action = info.action;
    if (action === null) return;
    seenConstructUpdateNotificationKeys.add(notificationKey);

    let toastId!: ConstructToastId;
    const runAction = () => {
      const bridge = window.desktopBridge;
      const current = infoRef.current;
      if (!bridge || current === null) return;
      toastManager.close(toastId);
      activeToastRef.current = null;
      void startConstructUpdate(bridge, current);
    };
    const openProviderSettings = () => {
      toastManager.close(toastId);
      activeToastRef.current = null;
      void navigate({ to: "/settings/providers" });
    };
    toastId = toastManager.add(
      stackedThreadToast({
        type: "info",
        title: getConstructUpdateHeadline(info),
        description: getConstructUpdateDetail(info),
        timeout: 0,
        actionProps: {
          children: getConstructUpdateActionLabel(action),
          onClick: runAction,
        },
        actionVariant: "default",
        data: {
          hideCopyButton: true,
          onClose: () => dismissNotificationKey(notificationKey),
          secondaryActionProps: {
            children: "Details",
            onClick: openProviderSettings,
          },
          secondaryActionVariant: "outline",
        },
      }),
    );
    activeToastRef.current = { key: notificationKey, toastId };
  }, [
    closeActiveToast,
    dismissNotificationKey,
    dismissedNotificationKeys,
    info,
    navigate,
    notificationKey,
  ]);

  return null;
}
