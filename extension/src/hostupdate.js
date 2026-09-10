"use strict";

const crypto = require("crypto");
const ACTIVE = ["checking", "staged", "draining", "handedOff", "applying"];
const TERMINAL = ["succeeded", "cancelled", "stageFailed", "applyFailed", "rolledBack", "rolledBackWithDatabase", "resolvedByAdmin"];

// One host's update intent. New services carry it durably themselves; older
// services are advanced through their existing stage/apply API by this client.
function createHostUpdater({ client, now = Date.now, pending = null, save = async () => {}, changed = () => {} }) {
  const state = { status: null, pending, checking: false, error: "" };
  let lastCheck = -Infinity;
  async function remember(value) { await save(value); state.pending = value; changed(); }
  async function check(force = false) {
    if (!force && now() - lastCheck < 15 * 60 * 1000) return;
    lastCheck = now(); state.checking = true; state.error = ""; changed();
    try {
      const result = await client.updatesCheck({});
      state.status = { ...(state.status || {}), installed: result.installed || state.status?.installed,
        latestKnown: result.latest ? { ...result.latest, checkedAt: result.checkedAt } : null };
      return result;
    } catch (e) { state.error = e.message; throw e; }
    finally { state.checking = false; changed(); }
  }
  async function advance() {
    let p = state.pending;
    if (!p) return;
    if (!p.updateId) {
      let result;
      try { result = await client.updatesStage({ releaseTag: p.releaseTag, operationKey: p.operationKey, autoApply: true }); }
      catch (e) {
        if (e.status >= 400 && e.status < 500 && ![408, 429].includes(e.status)) await remember(null);
        throw e;
      }
      if (!result.updateId) throw new Error("The host did not return an update ID.");
      await remember({ ...p, updateId: result.updateId });
      state.status = await client.updatesStatus();
      p = state.pending;
    }
    const cur = state.status && state.status.current;
    if (!cur || (cur.updateId || cur.id) !== p.updateId) {
      await remember(null);
      throw new Error("The host's current update changed. Open update details before retrying.");
    }
    if (TERMINAL.includes(cur.state) || ["interrupted", "recoveryFailed"].includes(cur.state)) {
      await remember(null);
    } else if (cur.state === "staged" && !p.serverAutoApply) {
      try { await client.updatesApply({ updateId: p.updateId, operationKey: p.operationKey + "-apply" }); }
      catch (e) {
        if (e.status >= 400 && e.status < 500 && ![408, 429].includes(e.status)) await remember(null);
        throw e;
      }
      state.status = await client.updatesStatus();
    }
  }
  async function refresh({ checkLatest = true } = {}) {
    state.error = "";
    try {
      const priorLatest = state.status && state.status.latestKnown;
      state.status = await client.updatesStatus();
      // Older hosts persist only the commit, not compatibility or release tag.
      if (priorLatest && state.status.latestKnown?.commit === priorLatest.commit)
        state.status.latestKnown = { ...priorLatest, ...state.status.latestKnown };
      await advance();
      if (checkLatest && !state.pending && !ACTIVE.includes(state.status.current?.state)) await check();
    } catch (e) { state.error = e.message; throw e; }
    finally { changed(); }
  }
  async function start() {
    state.error = "";
    try {
      await refresh({ checkLatest: false });
      if (state.pending) return;
      const cur = state.status.current;
      if (cur?.state === "staged") {
        // Finish a package the old two-button UI already downloaded.
        await remember({ updateId: cur.updateId || cur.id, operationKey: crypto.randomUUID(), serverAutoApply: false });
      } else {
        if (ACTIVE.includes(cur?.state) || ["interrupted", "recoveryFailed"].includes(cur?.state))
          throw new Error("An update is already active or needs recovery. See update details.");
        const result = await check(true);
        if (!result.latest) throw new Error("No host release is available.");
        if (result.latest.commit === result.installed?.commit) return;
        if (result.latest.compatible === false) throw new Error("Update is incompatible: " + (result.latest.reasons || []).join(", "));
        await remember({ releaseTag: result.latest.releaseTag, operationKey: crypto.randomUUID(), serverAutoApply: state.status.supportsAutoApply === true });
      }
      await advance();
    } catch (e) { state.error = e.message; throw e; }
    finally { changed(); }
  }
  return { state, check, refresh, start };
}

module.exports = { createHostUpdater, ACTIVE };
