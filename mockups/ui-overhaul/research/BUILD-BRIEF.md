# Mockup build brief (common to all builders)

Product: **The Construct**. It runs disposable Ubuntu VMs for unattended AI coding agents (Claude Code, Codex, OpenCode, T3 Code), locally on Hyper-V or on a shared host. Read in this folder:
- `operator-ux.md` (daily-operator research)
- `host-admin-ux.md` (host admin research)
- `installer-ux.md` (installer research)
- `sample-data.md` (**use this data everywhere**)

The current UI is in `/root/repos/construct/extension/media/` (launcher.html is the tray popup, panel.html is the control panel, hostadmin.html is the host panel; themes/*.css). It is for reference only. **Do not copy its layout.** Every existing feature still needs a home: lifecycle (reprovision/reinstall/redownload), power, instance switching, forwards, mic passthrough, agents & updates, projects/profile editor, config sync, token usage, child VMs, idle policy, VM resources, access & services settings, identity & credentials, UI settings, custom reinstall, remove instance, "share this PC as a host"; and for the host: overview/capacity, jobs, usage by user/VM, all VMs + overrides, users/allowances/tokens, ISO catalog, child media, Windows licensing, audit, host config, backend capabilities, service update. Rare or dangerous items may sit deep, but they must exist.

## Deliverable
Static, self-contained HTML/CSS/vanilla JS. No network dependencies: no CDN, no web fonts, only system font stacks. Inline SVG icons are fine. Write only inside your assigned folder.

- `index.html` is the design's landing page. It has a top bar with the design name, a one-paragraph concept statement, and a switcher between the three surfaces:
  1. **Tray popup**. The Companion popup is anchored bottom-right above the Windows taskbar, about 400–440 px wide and at most 680 px tall. Render it inside a faux Windows desktop and taskbar corner so the context reads. Also show the **tray right-click menu** beside it.
  2. **Control panel**. The full per-VM operator window, as a Companion desktop window about 1280×800, with a faux window chrome.
  3. **Host panel**. The shared-host admin window, with the same chrome.
- Make it clickable enough to explore:
  - tabs and sections switch
  - drawers and dialogs open
  - dropdowns open
  - the dangerous-action confirm flow is shown
  - hover and focus states work
  - at least one state variation per surface (for example popup with VM running vs. VM saved/stopped, or an attention item resolved)

  Fake the data with JS; nothing has to be real.
- Put short "design notes" (why this layout, and what is glanceable / one click deep / deep) in a collapsible side note that doesn't cover the mockup.
- Quality bar: polished and production-plausible, with a coherent type scale, spacing and color system. Use accessible contrast. Support light/dark if the concept calls for it. Where it's truthful, show data staleness ("sampled 20 s ago").
- Verify visually. Screenshot each surface with headless Chrome, for example
  `google-chrome --headless=new --no-sandbox --hide-scrollbars --window-size=1440,900 --screenshot=/tmp/<you>-x.png "file://<path>/index.html#panel"`
  (support `#popup` / `#panel` / `#host` hash routing, plus hashes for sub-tabs where useful). Look at the PNGs with the Read tool and fix layout problems. Delete your /tmp screenshots when done.
- Final reply: 5–8 lines covering the concept, the list of files, and anything you left unfinished.
