namespace Construct.Agent.Setup;

/// <summary>Where VS Code's command line is on Windows, found the way install.ps1 and the agent find it.</summary>
public static class VsCode
{
    /// <summary><c>code.cmd</c> (or <c>code.exe</c>) on the search path, else in a user or a machine install of VS Code; null when there is none.</summary>
    public static string? Find(string? searchPath, string? localAppData, string? programFiles)
    {
        foreach (var directory in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in new[] { "code.cmd", "code.exe" })
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim('"'), name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var (root, rest) in new[] { (localAppData, @"Programs\Microsoft VS Code\bin\code.cmd"), (programFiles, @"Microsoft VS Code\bin\code.cmd") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine([root, .. rest.Split('\\')]);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
