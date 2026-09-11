namespace Construct.Companion.Host.Dispatch;

public sealed partial class MessageDispatcher
{
    public static IReadOnlySet<string> KnownMessages { get; } = new HashSet<string>(["command","customRebuild","openPanel","ready","saveIdlePolicy","saveProject","saveSettings","setAudio","setInstance","setUsagePeriod"]);
    public static IReadOnlySet<string> KnownCommands { get; } = new HashSet<string>(["addConfigRemote","addProject","addRemoteAndPublish","childDelete","childShutdown","chooseMicDevice","chooseTheme","closeForward","connect","convertToHost","createFirstVm","deleteProject","editProject","exportConfig","exportUsage","importRemoteConfigs","installGit","openAgentWeb","openConfigRepo","openForward","openHostAdmin","openProject","openProjectFolder","publishConfigProfiles","pushConfigUpstream","redownload","refresh","registerThisVm","reinstall","removeConfigRemote","removeInstance","reprovision","selectProfiles","shareConfigs","showLogs","shutdown","startConnect","syncConfigNow","updateAgent","updateAgents","updateConstruct"]);
}
