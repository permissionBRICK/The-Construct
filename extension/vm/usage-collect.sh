# shellcheck shell=bash
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
  rm -- "$errfile"
}
