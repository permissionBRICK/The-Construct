"use strict";

/**
 * UI design (theme) registry for the two webview surfaces.
 *
 * A theme changes ONLY the look: every theme renders the same panel.html /
 * launcher.html with the same panel.js / launcher.js — the theme is a single
 * extra stylesheet layered AFTER the shared panel.css (the {{themeUri}} link),
 * so functionality can never fork between designs. Adding a design later
 * (mission-control, datasheet, …) = one entry here + media/themes/<id>.css
 * + media/theme-previews/<id>.png; no other code changes.
 *
 * Pure module: no vscode dependency, so it is unit-testable in plain node.
 */

const THEMES = require("../media/theme-cards.json");

/** The design used when nothing was chosen yet: VS Code Native, so a fresh
 *  install looks like a built-in panel and follows the user's editor theme. */
const DEFAULT_THEME = "native";

/** Known id -> entry, else null (null/""/unknown all mean "not chosen"). */
function normalizeThemeId(value) {
  const v = String(value == null ? "" : value).trim().toLowerCase();
  return THEMES.some((t) => t.id === v) ? v : null;
}

/** Stylesheet path relative to media/ for a (normalized-or-null) theme id. */
function cssFileFor(id) {
  return "themes/" + (normalizeThemeId(id) || DEFAULT_THEME) + ".css";
}

/** Preview thumbnail path relative to media/ for a theme id. */
function previewFileFor(id) {
  return "theme-previews/" + (normalizeThemeId(id) || DEFAULT_THEME) + ".png";
}

const escapeHtml = (s) =>
  String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

/**
 * The theme-picker webview document. Pure string builder so the card markup is
 * unit-testable; extension.js supplies webview-resolved preview URIs.
 * `cards` = [{id, label, blurb, previewUri}] (already in display order).
 * The picker is its own small webview and is deliberately styled with VS Code's
 * injected --vscode-* variables (NOT panel.css) so it looks at home in any
 * editor theme before a design was chosen.
 */
function buildPickerHtml({ cspSource, nonce, cards }) {
  const cardHtml = (cards || [])
    .map(
      (c) =>
        '<button class="card" data-theme="' + escapeHtml(c.id) + '">' +
        '<img src="' + escapeHtml(c.previewUri) + '" alt="Preview of the ' + escapeHtml(c.label) + ' design" />' +
        '<span class="card-label">' + escapeHtml(c.label) + "</span>" +
        '<span class="card-blurb">' + escapeHtml(c.blurb) + "</span>" +
        '<span class="card-cta">Use this design</span>' +
        "</button>"
    )
    .join("\n");

  return require("fs").readFileSync(require("path").join(__dirname, "../media/theme-picker.html"), "utf8")
    .replace(/{{cspSource}}/g, () => cspSource)
    .replace(/{{nonce}}/g, () => nonce)
    .replace(/{{cardHtml}}/g, () => cardHtml);
}

module.exports = { THEMES, DEFAULT_THEME, normalizeThemeId, cssFileFor, previewFileFor, buildPickerHtml };
