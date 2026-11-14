"use strict";
// The webview documents. A surface is either a short status page (connecting, the agent is not
// installed, an install failed) or the frame that shows the agent's own page. No vscode here.
//
// The frame: the document's CSP lets frames load only the agent's origin, and the frame is
// sandboxed without top navigation or popups. It starts without a src; the extension posts the
// address with the token in the fragment once the document says it is ready, the script
// navigates the frame there and drops the value. So the token is in no attribute, no document
// text and no webview state (the script never calls setState), and the agent's page takes it
// out of its own URL at once (ui/src/main.tsx).

const SURFACES = new Set(["pop", "win"]);
const ROUTE_RE = /^#\/[A-Za-z0-9/_.~%-]{0,200}$/;
const ENGINE_RE = /^[A-Za-z0-9_-]{1,64}$/;
const VM_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$/;
const LOCAL_URL_RE = /^https?:\/\/(localhost|127\.0\.0\.1):\d{1,5}(\/[A-Za-z0-9/_.~%?&=+-]{0,500})?$/;

function esc(s) {
  return String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function origin(port) {
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Not a port");
  return "http://127.0.0.1:" + port;
}

/** The address the frame is sent to: the agent's page on that surface, the token in the fragment. */
function frameUrl(port, token, surface) {
  if (!SURFACES.has(surface)) throw new Error("Not a surface");
  return origin(port) + "/?surf=" + surface + "#agent-token=" + encodeURIComponent(token);
}

/** The address of a route on the page already loaded (same document: the token stays where the page keeps it). */
function routeUrl(port, surface, route) {
  if (!SURFACES.has(surface) || !ROUTE_RE.test(route)) throw new Error("Not a route");
  return origin(port) + "/?surf=" + surface + route;
}

const STYLE = `
  html, body { margin: 0; padding: 0; height: 100%; background: var(--vscode-sideBar-background, var(--vscode-editor-background)); color: var(--vscode-foreground); font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); }
  iframe { border: 0; width: 100%; height: 100%; display: block; }
  .status { padding: 16px; line-height: 1.5; }
  .status h2 { font-size: 1.1em; margin: 0 0 8px; }
  .status pre { white-space: pre-wrap; word-break: break-word; background: var(--vscode-textCodeBlock-background); padding: 8px; }
  button { color: var(--vscode-button-foreground); background: var(--vscode-button-background); border: 0; padding: 6px 12px; cursor: pointer; }
  button:hover { background: var(--vscode-button-hoverBackground); }
  button:disabled { opacity: .6; cursor: default; }`;

/**
 * A status page: a title, a text, an optional detail block (an error's output) and an optional
 * button that posts {type: "action", id}.
 */
function statusHtml({ nonce, title, text, detail, button }) {
  const csp = `default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';`;
  return `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${csp}">
<meta name="viewport" content="width=device-width, initial-scale=1"><style nonce="${nonce}">${STYLE}</style></head>
<body><div class="status"><h2>${esc(title)}</h2>${text ? `<p>${esc(text)}</p>` : ""}${detail ? `<pre>${esc(detail)}</pre>` : ""}${
  button ? `<button id="action" data-id="${esc(button.id)}">${esc(button.label)}</button>` : ""}</div>
<script nonce="${nonce}">
  const vscode = acquireVsCodeApi();
  const b = document.getElementById("action");
  if (b) b.addEventListener("click", () => { b.disabled = true; vscode.postMessage({ type: "action", id: b.dataset.id }); });
</script></body></html>`;
}

/**
 * The frame page for the agent on `port`. Messages the agent's page posts to its parent are
 * passed on only when they come from that frame and that origin.
 */
function frameHtml({ nonce, port }) {
  const agent = origin(port);
  const csp = `default-src 'none'; frame-src ${agent}; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';`;
  return `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${csp}">
<meta name="viewport" content="width=device-width, initial-scale=1"><style nonce="${nonce}">${STYLE}</style></head>
<body><iframe id="agent" title="The Construct" sandbox="allow-scripts allow-same-origin allow-forms" referrerpolicy="no-referrer" allow="clipboard-write"></iframe>
<script nonce="${nonce}">
  const vscode = acquireVsCodeApi();
  const AGENT = ${JSON.stringify(agent)};
  const frame = document.getElementById("agent");
  window.addEventListener("message", event => {
    if (event.source === frame.contentWindow) {
      if (event.origin === AGENT) vscode.postMessage({ type: "page", message: event.data });
      return;
    }
    const m = event.data;
    if (!m || typeof m !== "object" || typeof m.url !== "string" || m.url.indexOf(AGENT + "/") !== 0) return;
    if (m.type !== "load" && m.type !== "route") return;
    frame.contentWindow.location.replace(m.url);
    if (m.type === "load" && typeof m.then === "string" && m.then.indexOf(AGENT + "/") === 0) {
      // A route waits for the page: the first load of the agent's origin (its location is not readable from here).
      const then = m.then;
      const onLoad = () => {
        try { void frame.contentWindow.location.href; return; } catch (_) { /* the agent's page */ }
        frame.removeEventListener("load", onLoad);
        frame.contentWindow.location.replace(then);
      };
      frame.addEventListener("load", onLoad);
    }
  });
  vscode.postMessage({ type: "ready" });
</script></body></html>`;
}

/**
 * What the extension accepts from the agent's page, or null. `openWindow` (the pop-out asks for
 * the window at a route), `close`, `openVsCode` for one of the user's VMs, and `openExternal` for a
 * forwarded port on this machine.
 */
function pageMessage(m) {
  if (!m || typeof m !== "object" || Array.isArray(m)) return null;
  switch (m.type) {
    case "openWindow": {
      const route = m.route === undefined || m.route === null ? "#/" : m.route;
      return typeof route === "string" && ROUTE_RE.test(route) ? { type: "openWindow", route } : null;
    }
    case "close": return { type: "close" };
    case "openVsCode":
      return typeof m.engine === "string" && ENGINE_RE.test(m.engine) && typeof m.vm === "string" && VM_RE.test(m.vm)
        ? { type: "openVsCode", engine: m.engine, vm: m.vm } : null;
    case "openExternal":
      // A forward's link: a port on this machine, nothing else.
      return typeof m.url === "string" && LOCAL_URL_RE.test(m.url) ? { type: "openExternal", url: m.url } : null;
    default: return null;
  }
}

module.exports = { SURFACES, ROUTE_RE, origin, frameUrl, routeUrl, statusHtml, frameHtml, pageMessage, esc };
