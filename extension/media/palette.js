// Native form popups must follow the displayed palette in VS Code and WebView2.
(() => {
  "use strict";
  const root = document.documentElement;
  let scheduled = false;
  function update() {
    scheduled = false;
    const native = getComputedStyle(root).getPropertyValue("--construct-native-palette").trim() === "1";
    const rgb = getComputedStyle(document.body).backgroundColor.match(/[\d.]+/g)?.map(Number);
    const light = native && rgb?.length >= 3 && (rgb[0] * .2126 + rgb[1] * .7152 + rgb[2] * .0722) > 128;
    const scheme = light ? "light" : "dark";
    if (root.style.colorScheme !== scheme) root.style.colorScheme = scheme;
  }
  function schedule() { if (!scheduled) { scheduled = true; requestAnimationFrame(update); } }
  function start() {
    update();
    const observer = new MutationObserver(schedule);
    observer.observe(root, { attributes: true, attributeFilter: ["style", "class"] });
    observer.observe(document.body, { attributes: true, attributeFilter: ["style", "class"] });
    observer.observe(document.head, { childList: true, subtree: true, characterData: true });
    document.querySelectorAll('link[rel="stylesheet"]').forEach(link => link.addEventListener("load", schedule));
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
