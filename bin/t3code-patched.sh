#!/usr/bin/env bash
# Resolve the renamed preference before a caller sources config.env.
resolve_t3code_patched() {
  local saved="" legacy=""
  if [[ -r "$1" ]]; then
    saved="$(sed -n 's/^T3CODE_PATCHED=//p' "$1" | head -1 | tr -d "\"'")"
    legacy="$(sed -n 's/^T3CODE_LIMIT_RESUME=//p' "$1" | head -1 | tr -d "\"'")"
  fi
  printf '%s\n' "${T3CODE_PATCHED:-${saved:-${T3CODE_LIMIT_RESUME:-${legacy:-false}}}}"
}
