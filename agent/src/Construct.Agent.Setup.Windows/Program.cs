using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Construct.Agent.Setup.Windows;

/// <summary>
/// <c>ConstructSetup.exe [--channel stable|nightly|&lt;https manifest URL&gt;] [--repository owner/name]</c>: installs the
/// Construct device agent for this user (<see cref="AgentSetup"/>), opens its window and ends. The channel is the one the
/// release built in. A failure stays on screen until a key is pressed when the setup has a console window of its own
/// (started from Explorer).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return Fail("Construct needs Windows 10 version 1809 or later.");
        try { Console.Title = "Construct setup"; }
        catch (IOException) { /* no console window */ }

        var channel = Option(args, "--channel") ?? Metadata("ConstructChannel") ?? "stable";
        var repository = Option(args, "--repository") ?? Metadata("ConstructRepository") ?? SetupChannel.DefaultRepository;
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";
        using var single = new Mutex(false, @"Local\ConstructSetup");
        bool owner;
        try { owner = single.WaitOne(0); }
        catch (AbandonedMutexException) { owner = true; }
        if (!owner) return Fail("Construct setup is already running in another window.");
        try
        {
            return Run(channel, repository, version, args);
        }
        finally
        {
            single.ReleaseMutex();
        }
    }

    private static int Run(string channel, string repository, string version, string[] args)
    {
        var console = new SetupConsole();
        console.WriteLine($"Construct setup {version.Split('+')[0]}, {SetupChannel.Name(channel)} channel");
        console.WriteLine("It installs Construct for your user; that needs no administrator rights.");
        var ignored = args.Where((a, i) => !(a is "--channel" or "--repository") && !(i > 0 && args[i - 1] is "--channel" or "--repository")).ToArray();
        if (ignored.Length > 0) console.WriteLine("Ignored: " + string.Join(' ', ignored));

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        // Timeouts are per step in AgentSetup. The loopback client follows no redirect and takes no proxy.
        using var web = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        web.DefaultRequestHeaders.UserAgent.ParseAdd("ConstructSetup/" + version.Split('+')[0]);
        using var local = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var setup = new AgentSetup(new WindowsSetupPlatform(), web, local, console) { Repository = repository, Progress = console.Progress };
        try
        {
            setup.RunAsync(channel, cancel.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (SetupFailure failure)
        {
            return Fail(failure.Message, console);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return Fail("The setup was cancelled.", console);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or Win32Exception)
        {
            return Fail(ex.Message, console);
        }
    }

    private static int Fail(string message, SetupConsole? console = null)
    {
        console?.EndProgress();
        Console.Error.WriteLine();
        Console.Error.WriteLine("Construct could not be set up: " + message);
        // A console window this setup opened itself closes with it: the message stays until a key is pressed.
        var attached = new uint[2];
        if (GetConsoleProcessList(attached, (uint)attached.Length) == 1)
        {
            Console.Error.WriteLine("Press any key to close this window.");
            try { Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { /* input redirected */ }
        }

        return 1;
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length && args[at + 1].Length > 0 ? args[at + 1] : null;
    }

    /// <summary>A value the build put in (<c>AssemblyMetadata</c> in the project file).</summary>
    private static string? Metadata(string key) =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value ? value : null;

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    /// <summary>The setup's console: its lines, and the download as one line that counts up.</summary>
    private sealed class SetupConsole : TextWriter
    {
        private const long MiB = 1024 * 1024;
        private bool _progress;
        private long _shown = -1;

        public override Encoding Encoding => Console.OutputEncoding;

        public override void Write(char value)
        {
            EndProgress();
            Console.Out.Write(value);
        }

        public override void Write(string? value)
        {
            EndProgress();
            Console.Out.Write(value);
        }

        public override void WriteLine(string? value)
        {
            EndProgress();
            Console.Out.WriteLine(value);
        }

        public void Progress(long done, long? total)
        {
            var shown = done / MiB;
            if (shown == _shown) return;
            _shown = shown;
            Console.Out.Write(total is > 0 ? $"\r    {shown} of {(total.Value + MiB - 1) / MiB} MB" : $"\r    {shown} MB");
            _progress = true;
        }

        public void EndProgress()
        {
            if (!_progress) return;
            _progress = false;
            Console.Out.WriteLine();
        }
    }
}
