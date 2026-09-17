# Focused ccusage reports use daily/monthly arrays, or the type/data envelope.
def tokens: .totalTokens // ((.inputTokens // 0) + (.outputTokens // 0) +
  (.cacheCreationTokens // .cacheCreateTokens // 0) + (.cacheReadTokens // .cachedInputTokens // 0));
def cost: .totalCost // .costUSD // .costUsd // .cost // 0;
def model: {totalTokens: tokens, costUsd: cost};
def models:
  if (.models | type) == "object" then .models | with_entries(.value |= model)
  elif (.breakdown | type) == "object" then .breakdown | with_entries(.value |= model)
  else [.modelBreakdowns[]? | {key: .modelName, value: model}] | from_entries end
  | to_entries | .[:64] | from_entries;
if .error then error("collector failed")
elif (.[$report] | type) == "array" then .[$report]
elif .type == $report and (.data | type) == "array" then .data
else error("unrecognized collector report") end
| map({day: (.date // .month // .period), tool: $tool,
    inputTokens: (.inputTokens // 0), outputTokens: (.outputTokens // 0),
    cacheCreateTokens: (.cacheCreationTokens // .cacheCreateTokens // 0),
    cacheReadTokens: (.cacheReadTokens // .cachedInputTokens // 0), totalTokens: tokens,
    costUsd: cost, models: models})
| . as $rows
| if all($rows[]; (.day | type) == "string" and
    (.day | test(if $report == "daily" then "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" else "^[0-9]{4}-[0-9]{2}$" end)))
  then . else error("invalid collector period") end
| $periods | map(. as $day | ($rows | map(select(.day == $day))) as $matches |
    if ($matches | length) > 1 then error("duplicate collector period")
    elif ($matches | length) == 1 then $matches[0]
    else {day: $day, tool: $tool, inputTokens: 0, outputTokens: 0, cacheCreateTokens: 0,
      cacheReadTokens: 0, totalTokens: 0, costUsd: 0, models: {}} end)
