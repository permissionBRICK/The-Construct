using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Lifecycle;

public sealed record HostLaunch(string File, string[] SpawnArgs, string Command)
{
    public ProcessInvocation Invocation(string? directory = null) => new(File, SpawnArgs, directory);
    public override string ToString() => File;
}
public static class PowerShellLaunch
{
    public static string SingleQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    public static string WinQuoteArg(string arg)
    {
        if (arg.Length > 0 && !Regex.IsMatch(arg, "[ \\t\\n\\v\"]")) return arg;
        var output = new StringBuilder("\""); var bs = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { output.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            output.Append('\\', bs).Append(c); bs = 0;
        }
        return output.Append('\\', bs * 2).Append('"').ToString();
    }
    public static string BuildChildCommandLine(string scriptPath, IEnumerable<string> args, bool keepOpen = false)
    {
        var argv = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass" }; if (keepOpen) argv.Add("-NoExit"); argv.AddRange(["-File", scriptPath]); argv.AddRange(args); return string.Join(" ", argv.Select(WinQuoteArg));
    }
    public static string BuildOuterCommand(string childCommandLine, bool elevate = false) => "Start-Process -FilePath 'powershell.exe'" + (elevate ? " -Verb RunAs" : "") + " -WindowStyle Normal -ArgumentList " + SingleQuote(childCommandLine);
    public static string BuildCallCommand(string scriptPath, IEnumerable<string> args, JsonArray? argSpec = null)
    {
        var tokens = new List<string>();
        if (argSpec is not null) foreach (var node in argSpec.OfType<JsonObject>()) { tokens.Add(StateJson.String(node["flag"])); if (node.ContainsKey("value")) tokens.Add(SingleQuote(StateJson.String(node["value"]))); }
        else tokens.AddRange(args.Select(a => a.StartsWith('-') ? a : SingleQuote(a)));
        return "& " + SingleQuote(scriptPath) + (tokens.Count == 0 ? "" : " " + string.Join(" ", tokens));
    }
    public static HostLaunch BuildHostLaunch(string scriptPath, IEnumerable<string> args, bool elevate = false, bool keepOpen = false, JsonArray? argSpec = null)
    {
        var command = elevate ? BuildOuterCommand(BuildChildCommandLine(scriptPath, args, keepOpen), true) : BuildCallCommand(scriptPath, args, argSpec);
        var ps = new List<string> { "-NoProfile" }; if (elevate) ps.Add("-NonInteractive"); ps.AddRange(["-ExecutionPolicy", "Bypass"]); if (keepOpen && !elevate) ps.Add("-NoExit"); ps.AddRange(["-EncodedCommand", Encode(command)]);
        return new HostLaunch("cmd.exe", ["/c", "start", "", "powershell.exe", .. ps], command);
    }
    public static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    public static HostLaunch Probe(string command) => new("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(command)], command);
}
