namespace Construct.Companion.Host.Dispatch;

// Kept out of MessageDispatcher.cs on purpose: ProtocolMatrixTests scans that file's `case` labels
// against these sets and against extension.js, so the tables must not live in the scanned file.
public sealed partial class MessageDispatcher
{
    public static IReadOnlySet<string> KnownMessages { get; } = new HashSet<string>(["command","customRebuild","openPanel","ready","saveIdlePolicy","saveProject","saveSettings","setAudio","setInstance","setUsagePeriod"]);
    public static IReadOnlySet<string> KnownCommands { get; } = new HashSet<string>(["addConfigRemote","addProject","addRemoteAndPublish","childDelete","childShutdown","chooseMicDevice","chooseTheme","closeForward","connect","convertToHost","createFirstVm","deleteProject","editProject","exportConfig","exportUsage","importRemoteConfigs","installGit","openAgentWeb","openConfigRepo","openForward","openHostAdmin","openProject","openProjectFolder","publishConfigProfiles","pushConfigUpstream","redownload","refresh","registerThisVm","reinstall","removeConfigRemote","removeInstance","reprovision","selectProfiles","shareConfigs","showLogs","shutdown","startConnect","syncConfigNow","updateAgent","updateAgents","updateConstruct"]);
}
