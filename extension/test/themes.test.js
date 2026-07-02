// Plain-node units for src/themes.js — the UI-design registry + picker HTML.
// The invariant under test: a theme is ONLY a stylesheet (+ preview image);
// every registry entry must resolve to real files so no design can 404 at
// runtime, and the picker document must be self-contained + injection-safe.
"use strict";
const assert = require("assert");
const fs = require("fs");
const path = require("path");
const themes = require("../src/themes");

let n = 0;
const ok = (cond, msg) => { n++; assert.ok(cond, msg); };
const eq = (a, b, msg) => { n++; assert.strictEqual(a, b, msg); };

const MEDIA = path.join(__dirname, "..", "media");

// ── registry shape ───────────────────────────────────────────────────────────
ok(Array.isArray(themes.THEMES) && themes.THEMES.length >= 3, "registry has the 3 launch designs");
const ids = themes.THEMES.map((t) => t.id);
eq(new Set(ids).size, ids.length, "ids are unique");
ids.forEach((id) => ok(/^[a-z]+$/.test(id), `id "${id}" is a plain lowercase slug (used in file paths + settings enum)`));
themes.THEMES.forEach((t) => {
  ok(t.label && t.blurb, `${t.id} has label + blurb`);
});
ok(ids.includes(themes.DEFAULT_THEME), "default theme is a registered design");
eq(themes.DEFAULT_THEME, "classic", "undecided users keep the original look");

// ── every design's files actually exist (a typo here = a broken skin at runtime)
themes.THEMES.forEach((t) => {
  const css = path.join(MEDIA, themes.cssFileFor(t.id));
  ok(fs.existsSync(css), `stylesheet exists for ${t.id}: ${css}`);
  const png = path.join(MEDIA, themes.previewFileFor(t.id));
  ok(fs.existsSync(png), `preview thumbnail exists for ${t.id}: ${png}`);
});

// ── the settings enum in package.json stays in sync with the registry ───────
const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf8"));
const prop = pkg.contributes.configuration.properties["construct.uiTheme"];
ok(prop, "construct.uiTheme is contributed");
eq(prop.default, "", "default is unset (= ask via the picker)");
eq(JSON.stringify(prop.enum), JSON.stringify(["", ...ids]), "settings enum = '' + registry ids, in order");
eq(prop.enumDescriptions.length, prop.enum.length, "every enum value has a description");

// ── normalizeThemeId ─────────────────────────────────────────────────────────
eq(themes.normalizeThemeId("terminal"), "terminal");
eq(themes.normalizeThemeId("  Native "), "native", "trims + case-folds");
eq(themes.normalizeThemeId(""), null, "empty = not chosen");
eq(themes.normalizeThemeId(null), null);
eq(themes.normalizeThemeId(undefined), null);
eq(themes.normalizeThemeId("mission"), null, "unknown ids are not chosen (future-proof: an old setting from a newer install degrades gracefully)");
eq(themes.normalizeThemeId({}), null, "non-strings are not chosen");

// ── cssFileFor / previewFileFor ──────────────────────────────────────────────
eq(themes.cssFileFor("native"), "themes/native.css");
eq(themes.cssFileFor(null), "themes/classic.css", "undecided renders the default design");
eq(themes.cssFileFor("../../etc/passwd"), "themes/classic.css", "hostile value falls back to default (never a path)");
eq(themes.previewFileFor("terminal"), "theme-previews/terminal.png");

// ── buildPickerHtml ──────────────────────────────────────────────────────────
const html = themes.buildPickerHtml({
  cspSource: "vscode-resource://x",
  nonce: "N0NCE",
  cards: themes.THEMES.map((t) => ({ ...t, previewUri: "https://mock/" + t.id + ".png" })),
});
ok(html.includes("nonce-N0NCE") && html.includes('nonce="N0NCE"'), "CSP nonce gates the script");
ok(html.includes("img-src vscode-resource://x"), "images restricted to the webview csp source");
themes.THEMES.forEach((t) => {
  ok(html.includes('data-theme="' + t.id + '"'), `card for ${t.id}`);
  ok(html.includes("https://mock/" + t.id + ".png"), `preview img for ${t.id}`);
});
ok(!/{{\w+}}/.test(html), "no unresolved template placeholders");
// Injection-proofing: card fields are attacker-ish data if a future registry
// entry ever carries user text — they must be escaped into inert HTML.
const evil = themes.buildPickerHtml({
  cspSource: "s",
  nonce: "n",
  cards: [{ id: 'x" onclick="alert(1)', label: "<script>bad()</script>", blurb: "a&b", previewUri: '"><img src=x>' }],
});
ok(!evil.includes('<script>bad()'), "label is escaped");
ok(!evil.includes('"><img src=x>'), "previewUri is escaped");
ok(evil.includes("&lt;script&gt;"), "escaping is entity-based, not stripping");

console.log(`themes.test.js: ${n} checks passed`);
