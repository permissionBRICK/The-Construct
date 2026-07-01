"use strict";
// Hidden-webview mic capture controller (host side of Construct mic passthrough).
//
// This runs in the off-screen audio.html webview. A webview is the only surface a
// UI extension has that can call getUserMedia to open the LOCAL microphone. It is
// driven entirely by the extension over postMessage and does NOTHING on its own:
//   ext → webview  {type:'arm'}      open the mic + worklet; start posting PCM
//                  {type:'disarm'}   stop the mic (release the device) — ON-DEMAND
//   webview → ext  {type:'ready'}         loaded
//                  {type:'pcm', data}      one chunk of S16LE 16 kHz mono bytes
//                  {type:'armed'}          mic opened, capturing
//                  {type:'error', reason}  couldn't open the mic (blocked/no device)
//                  {type:'disarmed'}       mic released
//
// ON-DEMAND is enforced here: the mic is opened only between arm and disarm, so
// while passthrough is "enabled" but nothing is recording, no MediaStream is live
// and the OS mic indicator stays off. The extension arms us when the VM shim
// connects (Claude started recording) and disarms on disconnect.

(function () {
  const vscode = acquireVsCodeApi();
  const post = (m) => { try { vscode.postMessage(m); } catch (_) {} };

  let stream = null;   // the live MediaStream while armed
  let ctx = null;      // AudioContext
  let node = null;     // the AudioWorkletNode
  let src = null;      // MediaStreamAudioSourceNode
  let arming = false;  // guard against overlapping arm() calls

  // The worklet module URI is injected by the extension into a global before this
  // script loads (audio.html sets window.__workletUri via a nonce'd inline value).
  function workletUri() {
    return (typeof window !== "undefined" && window.__workletUri) || "audio-worklet.js";
  }

  async function arm() {
    if (arming || stream) return; // already armed / arming
    arming = true;
    try {
      // Mono; let the browser pick the device rate (the worklet resamples to 16 kHz).
      // Disable processing that would distort the raw capture the recorder expects.
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          channelCount: 1,
          echoCancellation: false,
          noiseSuppression: false,
          autoGainControl: false,
        },
        video: false,
      });
      ctx = new (window.AudioContext || window.webkitAudioContext)();
      await ctx.audioWorklet.addModule(workletUri());
      src = ctx.createMediaStreamSource(stream);
      node = new AudioWorkletNode(ctx, "construct-downsampler");
      node.port.onmessage = (e) => {
        // e.data is a transferred ArrayBuffer of S16LE bytes. Relay to the extension;
        // it forwards the bytes to the VM over the tunnel. Post as-is (structured
        // clone) — the extension reads it back into a Buffer.
        post({ type: "pcm", data: e.data });
      };
      src.connect(node);
      // Do NOT connect the node to ctx.destination — we don't want to play the mic
      // back through the speakers; the worklet's postMessage is the only sink.
      arming = false;
      post({ type: "armed" });
    } catch (err) {
      arming = false;
      await disarm(); // clean up any partial state
      post({ type: "error", reason: (err && err.name) || String(err) });
    }
  }

  async function disarm() {
    try { if (node) { node.port.postMessage("stop"); node.disconnect(); } } catch (_) {}
    try { if (src) src.disconnect(); } catch (_) {}
    try {
      if (stream) stream.getTracks().forEach((t) => { try { t.stop(); } catch (_) {} });
    } catch (_) {}
    try { if (ctx && ctx.state !== "closed") await ctx.close(); } catch (_) {}
    node = null; src = null; stream = null; ctx = null;
    post({ type: "disarmed" });
  }

  window.addEventListener("message", (ev) => {
    const m = ev.data;
    if (!m || typeof m.type !== "string") return;
    if (m.type === "arm") arm();
    else if (m.type === "disarm") disarm();
  });

  post({ type: "ready" });
})();
