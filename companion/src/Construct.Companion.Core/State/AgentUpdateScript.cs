namespace Construct.Companion.Core.State;

public static class AgentUpdateScript
{
    private static readonly (string Id, string Script)[] Updates =
    [
        ("claude-code", "if command -v claude >/dev/null 2>&1; then echo \"== updating Claude Code ==\"; claude update || rc=1; fi"),
        ("codex", "if command -v codex >/dev/null 2>&1; then echo \"== updating Codex ==\"; target=$(readlink -f \"$(command -v codex)\" 2>/dev/null || true); case \"$target\" in */node_modules/*) npm install -g @openai/codex@latest || rc=1 ;; *) t=$(mktemp); if curl -fsSL https://chatgpt.com/codex/install.sh -o \"$t\" && printf \"n\\n\" | CI=1 sh \"$t\"; then :; elif command -v npm >/dev/null 2>&1 && npm install -g @openai/codex@latest; then echo \"official installer failed; updated via npm instead\"; else rc=1; fi; rm -f \"$t\"; if [ -L /usr/local/bin/codex ]; then case \"$(readlink /usr/local/bin/codex)\" in */.codex/*releases/*) for s in \"$HOME/.local/bin/codex\" \"$HOME/.codex/bin/codex\"; do if [ -x \"$s\" ]; then ln -sf \"$s\" /usr/local/bin/codex; echo \"relinked /usr/local/bin/codex -> $s\"; break; fi; done ;; esac; fi ;; esac; fi"),
        ("opencode", "if command -v opencode >/dev/null 2>&1; then echo \"== updating opencode ==\"; oc_ok=1; for oc_i in 1 2 3; do if curl -fsSL https://opencode.ai/install | bash; then oc_ok=0; break; fi; echo \"opencode installer failed (attempt $oc_i/3)\"; if [ \"$oc_i\" -lt 3 ]; then sleep $((oc_i * 5)); fi; done; [ \"$oc_ok\" -eq 0 ] || rc=1; fi"),
        ("t3code", "if command -v t3 >/dev/null 2>&1; then echo \"== updating T3 Code ==\"; _t3ch=\"$(sed -n 's/^T3CODE_CHANNEL=//p' /etc/construct/config.env 2>/dev/null | head -1)\"; case \"$_t3ch\" in nightly) _t3tag=nightly ;; *) _t3tag=latest ;; esac; if npm install -g \"t3@${_t3tag}\" --allow-scripts=node-pty,msgpackr-extract; then systemctl try-restart t3code-serve 2>/dev/null || true; else rc=1; fi; fi"),
    ];
    public static string Build(IEnumerable<string>? ids = null)
    {
        var wanted = ids?.ToArray(); if (wanted is null || wanted.Length == 0) wanted = Updates.Select(u => u.Id).ToArray();
        return "set -uo pipefail\nrc=0\n" + string.Join("", Updates.Where(u => wanted.Contains(u.Id, StringComparer.Ordinal)).Select(u => u.Script + "\n")) + "exit $rc\n";
    }
}
