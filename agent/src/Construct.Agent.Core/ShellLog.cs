namespace Construct.Agent;

/// <summary>
/// The agent's own log (<c>shell.log</c> in its directory): every start and stop with its reason, the moves into <c>app/</c>,
/// self-test results and unhandled exceptions, one line each with a UTC time. It starts over above 1 MB. A start
/// without a stop line after it means the process was ended from outside (or crashed).
/// </summary>
public static class ShellLog
{
    public const int Limit = 1_000_000;

    public static void Note(string path, string line)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > Limit) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
