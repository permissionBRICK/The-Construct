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

# construct:usage-collect
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
