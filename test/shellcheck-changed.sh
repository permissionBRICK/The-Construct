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
pass=0
fail=0
while IFS= read -r file; do
  [[ -f "$file" ]] || continue
  # Shell scripts by name or by their first line (bin/construct has no extension).
  [[ "$file" == *.sh ]] || head -n1 "$file" | grep -qE '^#!.*\b(ba)?sh\b' || continue
  # The checker exits 1 when it finds anything; the count is what matters.
  now="$( { shellcheck -f gcc "$file" 2>/dev/null || true; } | wc -l)"
  before=0
  if git cat-file -e "$fork:$file" 2>/dev/null; then
    before="$(git show "$fork:$file" | { shellcheck -f gcc - 2>/dev/null || true; } | wc -l)"
  fi
  if [[ "$now" -le "$before" ]]; then
    pass=$((pass + 1)); printf '  PASS  %s: %s diagnostics (%s at the base)\n' "$file" "$now" "$before"
  else
    fail=$((fail + 1)); printf '  FAIL  %s: %s diagnostics, %s at the base\n' "$file" "$now" "$before"
    shellcheck -f gcc "$file" || true
  fi
done < <({ git diff --name-only --diff-filter=AMR "$fork"; git ls-files --others --exclude-standard; } | sort -u)
printf '\n%d passed, %d failed\n' "$pass" "$fail"
[[ "$fail" -eq 0 ]]
