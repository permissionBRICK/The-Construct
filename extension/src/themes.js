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

const THEMES = [
  {
    id: "classic",
    label: "Classic Matrix",
    blurb: "The original green operator console — rain, glow and all.",
  },
  {
    id: "terminal",
    label: "Terminal, Refined",
    blurb: "The Matrix identity with discipline: a real phosphor scale, glow reserved for live things.",
  },
  {
    id: "native",
    label: "VS Code Native",
    blurb: "Looks like a built-in VS Code panel and follows your editor theme, light or dark.",
  },
];

/** The design used when nothing was chosen yet (also the pre-theming look). */
const DEFAULT_THEME = "classic";

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

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${cspSource} data:; style-src 'unsafe-inline'; script-src 'nonce-${nonce}'; base-uri 'none'; form-action 'none'; object-src 'none';" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>The Construct — choose a design</title>
  <style>
    body { font-family: var(--vscode-font-family, sans-serif); color: var(--vscode-foreground, #ccc); padding: 0 16px 24px; }
    .wrap { max-width: 860px; margin: 0 auto; }
    h1 { font-size: 1.35em; font-weight: 600; margin: 28px 0 4px; }
    p.lead { margin: 0 0 20px; color: var(--vscode-descriptionForeground, #999); max-width: 62ch; }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 14px; }
    .card {
      display: flex; flex-direction: column; align-items: stretch; gap: 8px; padding: 10px 10px 12px;
      text-align: left; cursor: pointer; font: inherit; color: inherit;
      background: var(--vscode-editorWidget-background, #252526);
      border: 1px solid var(--vscode-widget-border, #454545); border-radius: 6px;
    }
    .card:hover { border-color: var(--vscode-focusBorder, #007fd4); }
    .card:focus-visible { outline: 2px solid var(--vscode-focusBorder, #007fd4); outline-offset: 1px; }
    .card img { width: 100%; height: auto; border-radius: 3px; border: 1px solid var(--vscode-widget-border, #454545); display: block; }
    .card-label { font-weight: 600; }
    .card-blurb { font-size: 0.9em; color: var(--vscode-descriptionForeground, #999); min-height: 2.6em; }
    .card-cta {
      align-self: flex-start; margin-top: 2px; padding: 4px 12px; border-radius: 2px; font-size: 0.9em;
      background: var(--vscode-button-background, #0e639c); color: var(--vscode-button-foreground, #fff);
    }
    p.note { margin: 22px 0 0; font-size: 0.9em; color: var(--vscode-descriptionForeground, #999); max-width: 70ch; }
  </style>
</head>
<body>
  <div class="wrap">
    <h1>Choose your Construct look</h1>
    <p class="lead">Every design is the same control panel — identical features, buttons and behavior. Only the styling changes, and you can switch anytime.</p>
    <div class="cards">
${cardHtml}
    </div>
    <p class="note">Change it later in Settings &rarr; Extensions &rarr; The Construct (<code>construct.uiTheme</code>), or run <b>The Construct: Choose UI Design</b> from the command palette. Closing this tab keeps things as they are and asks again next time.</p>
  </div>
  <script nonce="${nonce}">
    (function () {
      var vscode = acquireVsCodeApi();
      document.querySelectorAll(".card").forEach(function (b) {
        b.addEventListener("click", function () {
          vscode.postMessage({ type: "pickTheme", id: b.getAttribute("data-theme") });
        });
      });
    })();
  </script>
</body>
</html>`;
}

module.exports = { THEMES, DEFAULT_THEME, normalizeThemeId, cssFileFor, previewFileFor, buildPickerHtml };
