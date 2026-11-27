#!/usr/bin/env bash
# Regression tests for the Remote-SSH extension pre-install in bin/install-vscode.sh
# (preinstall_extensions). Run: bash test/vscode-extensions.test.sh
#
# A reprovision reuses what is installed: a missing extension is installed, an
# installed one is replaced only when the marketplace has another version, the older
# versions VS Code leaves behind are removed, and Construct's Claude Code patches hold
# in each case. A fake code-server stands in for VS Code's CLI (it behaves like the
# 1.147 build: without --force an installed extension is left alone, with --force it is
# replaced only by another version, and an update marks the old folder in .obsolete);
# the mic passthrough scripts are stubs, since the real ones write /usr/local/bin. No
# network, and never this machine's own ~/.vscode-server.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-vscode.sh"
tmp="$(mktemp -d)"
holder=""
cleanup() {
  [[ -n "${holder}" ]] && kill "${holder}" 2>/dev/null
  rm -rf "${tmp}"
}
trap cleanup EXIT

pass=0
fail=0
ok() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1)); printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1)); printf '  FAIL  %s\n' "${name}"
  fi
}

# ── the fake VS Code server (bin/code-server + product.json) ──────────────────
mkdir -p "${tmp}/server/bin"
printf '{"version": "1.147.0"}\n' >"${tmp}/server/product.json"
cat >"${tmp}/server/bin/code-server" <<'EOF'
#!/usr/bin/env python3
# Marketplace: FAKE_GALLERY lines "<id> <version> <engine> <release|pre>", newest last.
import json, os, sys

args = sys.argv[1:]
value = lambda flag: args[args.index(flag) + 1] if flag in args else None
ext_dir, spec = value("--extensions-dir"), value("--install-extension")
with open(os.environ["FAKE_LOG"], "a") as log:
    log.write(" ".join(a for a in args[:args.index("--extensions-dir")]) + "\n")
if os.environ.get("FAKE_FAIL"):
    print("Failed Installing Extensions: " + spec); sys.exit(1)

ident, _, pinned = spec.partition("@")
ident = ident.lower()
force, pre = "--force" in args, "--pre-release" in args
registry = os.path.join(ext_dir, "extensions.json")
entries = json.load(open(registry)) if os.path.exists(registry) else []
current = next((e for e in entries if e["identifier"]["id"] == ident), None)
if current and not force and not pinned:
    print(f"Extension '{ident}' v{current['version']} is already installed. Use '--force' option to update to latest version.")
    sys.exit(0)

channel = "pre" if pre else "release"
offers = [line.split() for line in open(os.environ["FAKE_GALLERY"]) if line.split()[:1] == [ident]]
offer = next((o for o in offers if o[1] == pinned), None) if pinned else ([o for o in offers if o[3] == channel] or [None])[-1]
if offer is None:
    print(f"Extension '{spec}' not found."); sys.exit(1)
if current and current["version"] == offer[1]:
    print(f"Extension '{ident}' is already installed."); sys.exit(0)

publisher, name = ident.split(".", 1)
folder = f"{ident}-{offer[1]}-linux-x64"
os.makedirs(os.path.join(ext_dir, folder))
json.dump({"publisher": publisher, "name": name, "version": offer[1], "engines": {"vscode": offer[2]}},
          open(os.path.join(ext_dir, folder, "package.json"), "w"))
with open(os.path.join(ext_dir, folder, "extension.js"), "w") as f:
    f.write("q({includePartialMessages:!le.env.remoteName});if(!x.env.remoteName||this.isConfined())return!1;")
entries = [e for e in entries if e["identifier"]["id"] != ident]
entries.append({"identifier": {"id": ident}, "version": offer[1], "relativeLocation": folder,
                "location": {"$mid": 1, "path": os.path.join(ext_dir, folder), "scheme": "file"},
                "metadata": {"preRelease": offer[3] == "pre" or None, "pinned": bool(pinned)}})
json.dump(entries, open(registry, "w"))
if current:
    marks_file = os.path.join(ext_dir, ".obsolete")
    marks = json.load(open(marks_file)) if os.path.exists(marks_file) else {}
    marks[current["relativeLocation"]] = True
    json.dump(marks, open(marks_file, "w"))
print(f"Extension '{ident}' v{offer[1]} was successfully installed.")
EOF
chmod +x "${tmp}/server/bin/code-server"
FAKE="${tmp}/server/bin/code-server"

# ── a repo whose patch scripts are the real partial-streaming ones and mic stubs ──
mkdir -p "${tmp}/repo/extension/vm"
ln -s "${ROOT}/extension/vm/construct-partial-streaming-enable.sh" "${tmp}/repo/extension/vm/"
ln -s "${ROOT}/extension/vm/construct-partial-streaming-disable.sh" "${tmp}/repo/extension/vm/"
echo '# construct-rec-shim (stub)' >"${tmp}/repo/extension/vm/construct-rec-shim.sh"
for action in enable disable; do
  printf '#!/usr/bin/env bash\nprintf "%%s HOME=%%s shim=%%s\\n" %s "$HOME" "${CONSTRUCT_SHIM_B64:+yes}" >>"%s/audio.log"\n' \
    "${action}" "${tmp}" >"${tmp}/repo/extension/vm/construct-audio-${action}.sh"
done

gallery() { printf '%s\n' "$@" >"${tmp}/gallery"; }

# preinstall_extensions for the user whose home is $1, with the fake server and any
# further VAR=value settings. Its output goes to ${tmp}/run.out, the CLI calls to
# ${tmp}/cli.log (both fresh per run).
run() {
  local home="$1"
  shift
  : >"${tmp}/cli.log"; : >"${tmp}/audio.log"
  mkdir -p "${home}"
  env CONSTRUCT_VSCODE_FUNCS_ONLY=true CONFIG_FILE="${tmp}/no-config.env" REPO_DIR="${tmp}/repo" \
      CLAUDE_USER=root AI_TOOLS=claude-code,codex VSCODE_EXTENSIONS= CLAUDE_PARTIAL_STREAMING=true MIC_PASSTHROUGH=false \
      TEST_HOME="${home}" FAKE_SERVER="${FAKE}" FAKE_LOG="${tmp}/cli.log" FAKE_GALLERY="${tmp}/gallery" NO_SERVER= "$@" \
    bash -c 'source "$1"
             connection_home() { printf "%s" "${TEST_HOME}"; }
             find_or_fetch_code_server() { CODE_SERVER_BIN="${FAKE_SERVER}"; PREINSTALL_TMP=""; [[ -z "${NO_SERVER}" ]]; }
             preinstall_extensions' _ "${SCRIPT}" >"${tmp}/run.out" 2>&1
}

ext() { printf '%s' "$1/.vscode-server/extensions"; }
inode() { stat -c %i "$1" 2>/dev/null; }
logged() { grep -qxF -- "$1" "${tmp}/cli.log"; }
not_logged() { ! grep -qF -- "$1" "${tmp}/cli.log"; }
said() { grep -qF -- "$1" "${tmp}/run.out"; }
patched() { grep -qF 'includePartialMessages:!le.env.remoteName||!0' "$1"; }
once() { [[ "$(grep -oF '||!0' "$1" | wc -l)" -eq 1 ]]; }
listed() { python3 -c 'import json,sys; sys.exit(sys.argv[2] not in [e["relativeLocation"] for e in json.load(open(sys.argv[1]))])' "$1/extensions.json" "$2"; }
obsolete_has() { [[ -f "$1/.obsolete" ]] && python3 -c 'import json,sys; sys.exit(sys.argv[2] not in json.load(open(sys.argv[1])))' "$1/.obsolete" "$2"; }

claude1=anthropic.claude-code-2.0.1-linux-x64
claude2=anthropic.claude-code-2.0.2-linux-x64
chatgpt=openai.chatgpt-0.5.1-linux-x64

# ── a missing extension is installed (no --force), and patched ────────────────
home="${tmp}/a"; dir="$(ext "${home}")"
gallery "anthropic.claude-code 2.0.1 ^1.100.0 release" "openai.chatgpt 0.5.1 ^1.100.0 release"
run "${home}"
ok "missing: claude-code installed without --force" logged "--install-extension anthropic.claude-code"
ok "missing: chatgpt installed without --force" logged "--install-extension openai.chatgpt"
ok "missing: both are listed" listed "${dir}" "${claude1}"
ok "missing: says what it installed" said "installed anthropic.claude-code 2.0.1"
ok "missing: partial streaming patched" patched "${dir}/${claude1}/extension.js"
ok "missing: the stock build is backed up" test -f "${dir}/${claude1}/extension.js.construct-partial.bak"
ok "missing: mic passthrough off reverts it, as the user" grep -qxF "disable HOME=${home} shim=" "${tmp}/audio.log"
stock="$(cat "${dir}/${claude1}/extension.js.construct-partial.bak")"
claude_inode="$(inode "${dir}/${claude1}/package.json")"
chatgpt_inode="$(inode "${dir}/${chatgpt}/package.json")"

# ── a current one is not reinstalled; its patch holds, its backups go ────────
run "${home}"
ok "current: the marketplace is asked (--force)" logged "--install-extension anthropic.claude-code --force"
ok "current: claude-code folder untouched" test "$(inode "${dir}/${claude1}/package.json")" = "${claude_inode}"
ok "current: chatgpt folder untouched" test "$(inode "${dir}/${chatgpt}/package.json")" = "${chatgpt_inode}"
ok "current: says it is current" said "anthropic.claude-code 2.0.1 is current"
# A backup holds the bytes of the moment its patch went on (the other patch possibly on): restoring one could bring
# the other back, so every backup goes and each disable step reverts its own transform in place.
ok "current: backups dropped" test ! -e "${dir}/${claude1}/extension.js.construct-partial.bak"
ok "current: patched once" once "${dir}/${claude1}/extension.js"

# The revert in place gives the stock bytes back.
run "${home}" CLAUDE_PARTIAL_STREAMING=false
ok "current, streaming off: stock restored in place" test "$(cat "${dir}/${claude1}/extension.js")" = "${stock}"
run "${home}"
ok "current, streaming on again: patched once" once "${dir}/${claude1}/extension.js"

# ── a newer one is updated, patched, and the old version goes ─────────────────
gallery "anthropic.claude-code 2.0.1 ^1.100.0 release" "anthropic.claude-code 2.0.2 ^1.100.0 release" \
  "openai.chatgpt 0.5.1 ^1.100.0 release"
run "${home}"
ok "newer: updated" listed "${dir}" "${claude2}"
ok "newer: says so" said "updated anthropic.claude-code 2.0.1 -> 2.0.2"
ok "newer: the new build is patched" patched "${dir}/${claude2}/extension.js"
ok "newer: the new build is backed up" test -f "${dir}/${claude2}/extension.js.construct-partial.bak"
ok "newer: the old version is removed" test ! -e "${dir}/${claude1}"
ok "newer: and no longer marked obsolete" sh -c "! grep -qF '${claude1}' '${dir}/.obsolete' 2>/dev/null"
ok "newer: chatgpt untouched" test "$(inode "${dir}/${chatgpt}/package.json")" = "${chatgpt_inode}"

# ── older versions left by earlier provisions go; others stay ─────────────────
make_folder() { # <dir> <folder> <publisher> <name> <version>
  mkdir -p "$1/$2"
  printf '{"publisher":"%s","name":"%s","version":"%s"}' "$3" "$4" "$5" >"$1/$2/package.json"
}
make_folder "${dir}" anthropic.claude-code-1.9.0-linux-x64 anthropic claude-code 1.9.0
make_folder "${dir}" anthropic.claude-code-1.8.0-linux-x64 anthropic claude-code 1.8.0
make_folder "${dir}" openai.chatgpt-0.4.0-linux-x64 openai chatgpt 0.4.0
make_folder "${dir}" openai.chatgpt-2-1.0.0 openai chatgpt-2 1.0.0
make_folder "${dir}" editorconfig.editorconfig-0.17.4 editorconfig editorconfig 0.17.4
mkdir -p "${dir}/anthropic.claude-code-1.7.0-linux-x64"   # a half-written copy: no package.json
printf '{"openai.chatgpt-0.4.0-linux-x64":true,"editorconfig.editorconfig-0.16.4":true}' >"${dir}/.obsolete"
# A CLI still running from one of them keeps that folder.
cp "$(command -v sleep)" "${dir}/anthropic.claude-code-1.8.0-linux-x64/claude"
"${dir}/anthropic.claude-code-1.8.0-linux-x64/claude" 120 &
holder=$!
run "${home}"
kill "${holder}" 2>/dev/null; wait "${holder}" 2>/dev/null; holder=""
ok "old versions: nothing reinstalled" not_logged "--install-extension anthropic.claude-code@"
ok "old versions: claude-code 1.9.0 removed" test ! -e "${dir}/anthropic.claude-code-1.9.0-linux-x64"
ok "old versions: a half-written copy removed" test ! -e "${dir}/anthropic.claude-code-1.7.0-linux-x64"
ok "old versions: chatgpt 0.4.0 removed" test ! -e "${dir}/openai.chatgpt-0.4.0-linux-x64"
ok "old versions: its .obsolete mark removed" sh -c "! grep -qF 'openai.chatgpt-0.4.0' '${dir}/.obsolete'"
ok "old versions: other .obsolete marks stay" obsolete_has "${dir}" editorconfig.editorconfig-0.16.4
ok "old versions: one a process runs from is kept" test -d "${dir}/anthropic.claude-code-1.8.0-linux-x64"
ok "old versions: and named" said "kept anthropic.claude-code-1.8.0-linux-x64: a running process uses it"
ok "old versions: the one in use stays" test -f "${dir}/${claude2}/package.json"
ok "old versions: an extension with a longer name stays" test -d "${dir}/openai.chatgpt-2-1.0.0"
ok "old versions: other extensions stay" test -d "${dir}/editorconfig.editorconfig-0.17.4"
ok "old versions: still patched once" once "${dir}/${claude2}/extension.js"
run "${home}"
ok "old versions: the kept one goes once nothing uses it" test ! -e "${dir}/anthropic.claude-code-1.8.0-linux-x64"

# ── a pinned version (id@version) ──────────────────────────────────────────────
run "${home}" VSCODE_EXTENSIONS=anthropic.claude-code@2.0.1
ok "pinned: the pinned version is installed" logged "--install-extension anthropic.claude-code@2.0.1 --force"
ok "pinned: listed" listed "${dir}" "${claude1}"
ok "pinned: patched" patched "${dir}/${claude1}/extension.js"
ok "pinned: the newer one goes" test ! -e "${dir}/${claude2}"
run "${home}" VSCODE_EXTENSIONS=anthropic.claude-code@2.0.1
ok "pinned and installed: the CLI is not run" test ! -s "${tmp}/cli.log"
ok "pinned and installed: still patched once" once "${dir}/${claude1}/extension.js"

# ── a pre-release the user chose stays on that channel ─────────────────────────
home="${tmp}/pre"; dir="$(ext "${home}")"
gallery "anthropic.claude-code 2.0.1 ^1.100.0 release" "anthropic.claude-code 2.1.5 ^1.100.0 pre"
mkdir -p "${dir}"
FAKE_LOG="${tmp}/setup.log" FAKE_GALLERY="${tmp}/gallery" "${FAKE}" --install-extension anthropic.claude-code --pre-release \
  --extensions-dir "${dir}" --server-data-dir "${home}/.vscode-server" >/dev/null
run "${home}" AI_TOOLS=claude-code
ok "pre-release: asked on its channel" logged "--install-extension anthropic.claude-code --force --pre-release"
ok "pre-release: not replaced by the release" listed "${dir}" anthropic.claude-code-2.1.5-linux-x64

# ── a version that needs a newer VS Code than the server is left alone ─────────
home="${tmp}/newer-engine"; dir="$(ext "${home}")"
gallery "anthropic.claude-code 2.0.9 ^1.150.0 release"
mkdir -p "${dir}"
FAKE_LOG="${tmp}/setup.log" FAKE_GALLERY="${tmp}/gallery" "${FAKE}" --install-extension anthropic.claude-code \
  --extensions-dir "${dir}" --server-data-dir "${home}/.vscode-server" >/dev/null
run "${home}" AI_TOOLS=claude-code
ok "newer engine: the CLI is not run" test ! -s "${tmp}/cli.log"
ok "newer engine: says why" said "needs a newer VS Code than this server (1.147.0)"
ok "newer engine: still patched" patched "${dir}/anthropic.claude-code-2.0.9-linux-x64/extension.js"

# ── no server build available: nothing installed, what is installed is patched ──
run "${home}" AI_TOOLS=claude-code NO_SERVER=1 CLAUDE_PARTIAL_STREAMING=false
ok "no server: the CLI is not run" test ! -s "${tmp}/cli.log"
ok "no server: patches still follow the settings" sh -c "! grep -qF '||!0' '${dir}/anthropic.claude-code-2.0.9-linux-x64/extension.js'"

# ── mic passthrough on, and a failed install ───────────────────────────────────
home="${tmp}/mic"; dir="$(ext "${home}")"
gallery "anthropic.claude-code 2.0.1 ^1.100.0 release"
run "${home}" AI_TOOLS=claude-code MIC_PASSTHROUGH=true FAKE_FAIL=1
ok "failed install: warned" said "WARNING: failed to install anthropic.claude-code"
run "${home}" AI_TOOLS=claude-code MIC_PASSTHROUGH=true
ok "mic on: enabled with the shim, as the user" grep -qxF "enable HOME=${home} shim=yes" "${tmp}/audio.log"
ok "mic on: partial streaming patched too" patched "${dir}/${claude1}/extension.js"

# ── no extension list: nothing at all ──────────────────────────────────────────
home="${tmp}/none"
run "${home}" VSCODE_EXTENSIONS=none
ok "none: the CLI is not run" test ! -s "${tmp}/cli.log"
ok "none: no patch script runs" test ! -s "${tmp}/audio.log"
ok "none: says so" said "nothing to do"
run "${home}" AI_TOOLS=opencode
ok "no AI tools with extensions: the CLI is not run" test ! -s "${tmp}/cli.log"
ok "no AI tools with extensions: no patch script runs" test ! -s "${tmp}/audio.log"

ok "script parses" bash -n "${SCRIPT}"

printf '\n  VS Code extension tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[[ "${fail}" -eq 0 ]]
