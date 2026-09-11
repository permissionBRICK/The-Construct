using System.Reflection;
using System.Text.Json;
using Construct.Companion.Core;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Windows;

namespace Construct.Companion;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CommandLine command;
        try { command = CommandLine.Parse(args); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        if (command.Version)
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
            return 0;
        }
        if (command.SelfTest)
        {
            Console.WriteLine(JsonSerializer.Serialize(SelfTestReport.Stub(), IpcJson.Options));
            return 1;
        }
        if (!OperatingSystem.IsWindows()) return 1;
        using var instance = new SingleInstance();
        if (command.Quit)
        {
            if (!instance.IsPrimary) instance.RequestQuit();
            return 0;
        }
        if (!instance.IsPrimary) return 0;
        ApplicationConfiguration.Initialize();
        using var tray = new TrayContext(instance);
        Application.Run(tray);
        return 0;
    }
}
