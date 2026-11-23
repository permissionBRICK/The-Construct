namespace Construct.Agent.Setup;

/// <summary>What the setup needs from Windows: the folders, the current user's Run key and processes. The tests fake it.</summary>
public interface ISetupPlatform
{
    /// <summary>The agent's folder, as the agent reads it: <c>CONSTRUCT_AGENT_HOME</c>, else <c>%LOCALAPPDATA%\Construct\agent</c>.</summary>
    string AgentHome { get; }

    /// <summary><c>%LOCALAPPDATA%</c>: the old Companion's state (<c>The-Construct\companion</c>) and program (<c>Programs\ConstructCompanion</c>).</summary>
    string LocalAppData { get; }

    /// <summary>The user's profile folder: the old client's VM keys are in its <c>.ssh</c> folder.</summary>
    string Home { get; }

    /// <summary>Writes a value under the current user's Run key; null deletes it (a missing value is no error).</summary>
    void SetRunValue(string name, string? command);

    /// <summary>Starts a program that outlives the setup.</summary>
    void Start(string executable, string argument);

    /// <summary>Whether the process with this id still runs.</summary>
    bool IsRunning(int pid);

    /// <summary>The ids of the processes with this name in this Windows session; another user's are not counted.</summary>
    IReadOnlyList<int> SessionProcesses(string name);

    /// <summary>VS Code's command line, or null where VS Code is not installed.</summary>
    string? FindVsCode();

    /// <summary>Runs a program without a window, its output discarded, and returns its exit code; -1 when it ran past <paramref name="limit"/> (it is then ended).</summary>
    Task<int> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken ct);
}
