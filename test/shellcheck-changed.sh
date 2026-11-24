#!/usr/bin/env bash
# The shell scripts changed since the merge base with <base> (default: the first of origin/nightly,
# origin/rework, origin/main that exists), committed or not, get no new shellcheck diagnostics. Diagnostics
# are counted, not only their codes: 64 more instances of a code already there are 64 new findings to read
# past. Each script is checked once, here, instead of in every test that runs it.
#
#   bash test/shellcheck-changed.sh [<base>]
set -euo pipefail
cd "$(dirname "$0")/.."
command -v shellcheck >/dev/null 2>&1 || { echo 'shellcheck is not installed; skipped'; exit 0; }
base="${1:-}"
if [[ -z "$base" ]]; then
  for candidate in origin/nightly origin/rework origin/main; do
    if git rev-parse --verify --quiet "$candidate" >/dev/null; then base="$candidate"; break; fi
  done
fi
[[ -n "$base" ]] || { echo 'No base branch found; pass one.' >&2; exit 2; }
fork="$(git merge-base "$base" HEAD)"
diff_lines="$(git diff --name-status --find-renames --diff-filter=AMR "$fork")"
untracked_lines="$(git ls-files --others --exclude-standard)"

declare -A base_path=()
files=()
while IFS=$'\t' read -r status first second; do
  [[ -n "$status" ]] || continue
  if [[ "$status" == R* ]]; then
    files+=("$second")
    base_path[$second]="$first"
  else
    files+=("$first")
    base_path[$first]="$first"
  fi
done <<< "$diff_lines"
while IFS= read -r file; do
  [[ -n "$file" ]] || continue
  files+=("$file")
done <<< "$untracked_lines"

run_shellcheck() { # <target-file-or-dash> [stdin] -> prints count; non-0/1 exit returns 2
  local out rc=0
  set +e
  out="$(shellcheck -f gcc "$1" 2>&1)"
  rc=$?
  set -e
  if [[ "$rc" -ne 0 && "$rc" -ne 1 ]]; then
    printf '%s\n' "$out" >&2
    return 2
  fi
  if [[ -z "$out" ]]; then echo 0; else printf '%s\n' "$out" | wc -l; fi
}

pass=0
fail=0
declare -A seen=()
for file in "${files[@]}"; do
  [[ -n "$file" && -z "${seen[$file]:-}" ]] || continue
  seen[$file]=1
  [[ -f "$file" ]] || continue
  # Shell scripts by name or by their first line (bin/construct has no extension).
  [[ "$file" == *.sh ]] || head -n1 "$file" | grep -qE '^#!.*\b(ba)?sh\b' || continue
  if ! now="$(run_shellcheck "$file")"; then
    fail=$((fail + 1)); printf '  FAIL  %s: shellcheck failed to run\n' "$file"
    continue
  fi
  before=0
  orig="${base_path[$file]:-$file}"
  if git cat-file -e "$fork:$orig" 2>/dev/null; then
    if ! before="$(git show "$fork:$orig" | run_shellcheck -)"; then
      fail=$((fail + 1)); printf '  FAIL  %s: shellcheck failed on base revision\n' "$file"
      continue
    fi
  fi
  if [[ "$now" -le "$before" ]]; then
    pass=$((pass + 1)); printf '  PASS  %s: %s diagnostics (%s at the base)\n' "$file" "$now" "$before"
  else
    fail=$((fail + 1)); printf '  FAIL  %s: %s diagnostics, %s at the base\n' "$file" "$now" "$before"
    shellcheck -f gcc "$file" || true
  fi
done
printf '\n%d passed, %d failed\n' "$pass" "$fail"
[[ "$fail" -eq 0 ]]
