namespace Construct.Companion.Core;

public sealed record CommandLine(bool Background = false, bool Panel = false, bool Settings = false,
    bool HostAdmin = false, bool Popup = false, string? Instance = null, string? Host = null,
    string? Uri = null, bool Quit = false, bool SelfTest = false, bool Json = false, bool Version = false)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var result = new CommandLine();
        for (var i = 0; i < args.Count; i++)
        {
            string Value()
            {
                if (++i >= args.Count || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Missing command-line value.");
                return args[i];
            }
            result = args[i] switch
            {
                "--background" => result with { Background = true },
                "--panel" => result with { Panel = true },
                "--settings" => result with { Settings = true },
                "--hostadmin" => result with { HostAdmin = true },
                "--popup" => result with { Popup = true },
                "--instance" => result with { Instance = Value() },
                "--host" => result with { Host = Value() },
                "--uri" => result with { Uri = Value() },
                "--quit" => result with { Quit = true },
                "--selftest" => result with { SelfTest = true },
                "--json" => result with { Json = true },
                "--version" => result with { Version = true },
                _ => throw new ArgumentException("Unknown command-line option.")
            };
        }
        if (result.Uri is not null && (!System.Uri.TryCreate(result.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "construct"))
            throw new ArgumentException("Expected a construct URI.");
        return result;
    }
}

public sealed record SelfTestCheck(string Name, string Status, bool Required = true);
public sealed record SelfTestReport(bool Ok, IReadOnlyList<SelfTestCheck> Checks)
{
    public int ExitCode => Ok ? 0 : 1;
    public static SelfTestReport Create(IReadOnlyList<SelfTestCheck> checks) => new(
        checks.All(c => !c.Required || c.Status is "passed" or "none" or "muted"), checks);
}
