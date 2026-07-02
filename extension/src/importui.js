"use strict";
// Pure helper for the Projects-tab "import from remote config repo" flow. The
// collision/rename path in extension.js cannot be unit-tested (it drives vscode
// input boxes and fs), so its DECISION core lives here as a pure function: given a
// user-chosen rename target for a colliding upstream profile, decide whether the
// target is acceptable and produce the exact bytes to write. No vscode, no fs —
// unit-tested alongside projects.js/host.js.
//
// A renamed collision import must be FIRST-CLASS, identical to a fresh create: a
// safe, non-reserved, not-already-taken target name, plus full provenance
// (canonical profile + manifest entry preserving remoteUrl/ref/pathInRemote with
// importedAs=<newName> + stored base). Without that provenance the renamed import
// would be untracked — not shareable via the remote command, never pushed, and not
// 3-way-updatable on the next import — which is exactly the bug this guards against.

const projects = require("./projects");
const host = require("./host");

/**
 * Plan a RENAMED collision import. `orig` is the selected upstream file
 * ({ remoteUrl, ref, relPath, content }); `takenNames` is the set of profile
 * names already present in the config dir. Returns a rejection
 * `{ ok:false, error }` (error ∈ empty|reserved|unsafe|taken|unparseable|invalid)
 * or an accepted plan `{ ok:true, name, profileJson, manifestEntry, baseContent }`.
 * Pure.
 */
function planRenamedImport(newNameRaw, orig, takenNames) {
  const name = String(newNameRaw == null ? "" : newNameRaw).trim();
  if (!name) return { ok: false, error: "empty" };
  if (projects.isReservedProfileName(name)) return { ok: false, error: "reserved" };
  if (host.safeProfileName(name) !== name) return { ok: false, error: "unsafe" };
  // Case-INSENSITIVE collision check: profile names are filenames, and on
  // Windows/macOS the config dir is a case-insensitive filesystem, so "api" and
  // "API" are the same file. Comparing case-sensitively would let a rename to
  // "API" silently overwrite an existing "api" (the very data-loss this guards).
  const takenList = takenNames instanceof Set ? Array.from(takenNames) : (takenNames || []);
  const takenLower = new Set(takenList.map((n) => String(n).toLowerCase()));
  if (takenLower.has(name.toLowerCase())) return { ok: false, error: "taken" };
  if (!orig || typeof orig.content !== "string") return { ok: false, error: "unparseable" };
  let obj;
  try { obj = JSON.parse(orig.content); } catch (_) { return { ok: false, error: "unparseable" }; }
  if (!obj || typeof obj !== "object" || Array.isArray(obj)) return { ok: false, error: "invalid" };
  obj.name = name; // the on-disk name is authoritative (canonicalProfileJson re-stamps it too)
  const profileJson = projects.canonicalProfileJson(name, obj);
  if (!profileJson) return { ok: false, error: "invalid" };
  return {
    ok: true,
    name,
    profileJson,
    manifestEntry: { remoteUrl: orig.remoteUrl, ref: orig.ref, pathInRemote: orig.relPath, importedAs: name },
    baseContent: orig.content,
  };
}

module.exports = { planRenamedImport };
