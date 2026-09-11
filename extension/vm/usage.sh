set -u
REPORT="{{report}}"

# daily -> today's window; monthly -> from the 1st of this month; total -> no window
# (all-time). Window dates come from the VM's own clock (data, not injectable).
TODAY="$(date +%Y%m%d)"
case "$REPORT" in
  total)   ARGS=(monthly) ;;
  monthly) ARGS=(monthly --since "$(date +%Y%m01)" --until "$TODAY") ;;
  *)       ARGS=(daily --since "$TODAY" --until "$TODAY") ;;
esac

CC=()
ensure_ccusage() {
  if command -v ccusage >/dev/null 2>&1; then CC=(ccusage); return; fi
  if command -v bunx    >/dev/null 2>&1; then CC=(bunx ccusage@latest); return; fi
  if command -v npx     >/dev/null 2>&1; then CC=(npx -y ccusage@latest); return; fi

  echo "ccusage not found on the VM; attempting to install it..." >&2

  # Preferred: a global npm install when Node is present.
  if command -v npm >/dev/null 2>&1; then
    npm i -g ccusage >&2 2>&1 || true
    if command -v ccusage >/dev/null 2>&1; then CC=(ccusage); return; fi
  fi

  # Otherwise install the self-contained bun runtime and run ccusage via bunx.
  if ! command -v bun >/dev/null 2>&1; then
    command -v unzip >/dev/null 2>&1 || { (apt-get update && apt-get install -y unzip) >&2 2>&1 || true; }
    curl -fsSL https://bun.sh/install | bash >&2 2>&1 || true
  fi
  export BUN_INSTALL="${BUN_INSTALL:-$HOME/.bun}"
  export PATH="$BUN_INSTALL/bin:$PATH"
  if command -v bunx >/dev/null 2>&1; then CC=(bunx ccusage@latest); return; fi

  CC=()
}

# Run ccusage for one tool, returning valid JSON either way: the real report on
# success, or a small {error,...} object describing what went wrong.
capture() {
  local tool="$1" out rc errfile
  if [ "${#CC[@]}" -eq 0 ]; then
    jq -n --arg t "$tool" '{error:"no JavaScript runtime available to run ccusage", tool:$t}'
    return
  fi
  errfile="$(mktemp)"
  out="$("${CC[@]}" "$tool" "${ARGS[@]}" --json 2>"$errfile")"; rc=$?
  if [ "$rc" -ne 0 ] || ! printf '%s' "$out" | jq -e . >/dev/null 2>&1; then
    local detail; detail="$(tr '\n' ' ' <"$errfile" | head -c 500)"
    jq -n --arg t "$tool" --arg d "$detail" \
      '{error:("ccusage failed for "+$t), detail:$d}'
  else
    printf '%s' "$out"
  fi
  rm -f "$errfile"
}

ensure_ccusage

claude_json="$(capture claude)"
codex_json="$(capture codex)"
opencode_json="$(capture opencode)"

jq -n \
  --arg host "$(hostname)" \
  --arg report "$REPORT" \
  --arg window "${ARGS[*]}" \
  --argjson claude "$claude_json" \
  --argjson codex "$codex_json" \
  --argjson opencode "$opencode_json" \
  '{
     generatedAt: (now | todate),
     vmHost: $host,
     report: $report,
     window: $window,
     tools: { claude: $claude, codex: $codex, opencode: $opencode }
   }'
