using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Construct.Agent.Setup;

/// <summary>How a setup run ended.</summary>
/// <param name="Version">The agent version installed and started; null when a running agent took over.</param>
/// <param name="HandedOver">True when an agent was running already: it opened its window and updates itself.</param>
public sealed record SetupOutcome(string? Version, bool HandedOver);

/// <summary>
/// What <c>ConstructSetup.exe</c> does: the steps of install.ps1, for this user and without elevation. It reads the
/// channel manifest, downloads the agent package, checks its SHA-256 against the manifest, unpacks it into
/// <c>versions\&lt;version&gt;\</c> of the agent's folder, installs the VS Code extension the package carries where
/// VS Code is installed (VS Code refusing it fails the setup), writes <c>install.json</c> and, when the agent has no
/// settings yet, <c>device.json</c> with the channel, lets the agent start with Windows (the Run value), starts it
/// and opens its window. The window then runs the first-run setup or the one-time upgrade of an old install; it
/// asks for administrator rights only for the engine on this PC (design 11.6).
/// <para>
/// An old Construct Companion is asked to quit first, the way its own updates do; when anything fails after that,
/// it is started again and the error stands, so a second run starts over. With an agent already running, the
/// setup hands over to it: the agent opens its window and updates itself.
/// </para>
/// </summary>
public sealed class AgentSetup(ISetupPlatform platform, HttpClient web, HttpClient local, TextWriter output)
{
    public const string AgentExecutable = "construct-agent.exe";
    public const string AgentRunValue = "ConstructAgent";
    public const string CompanionRunValue = "ConstructCompanion";
    public const string CompanionProcess = "ConstructCompanion";
    public const string VsixName = "construct.vsix";

    private const string OpenWindow = """{"surface":"win"}""";

    public string Repository { get; init; } = SetupChannel.DefaultRepository;

    /// <summary>How often the setup looks whether the agent answers, or the Companion has quit.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the started agent has to answer its health route.</summary>
    public TimeSpan AgentStartLimit { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the old Companion has to quit once asked.</summary>
    public TimeSpan CompanionQuitLimit { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the package download may take in all.</summary>
    public TimeSpan DownloadLimit { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The download's bytes so far and its length when the server names it.</summary>
    public Action<long, long?>? Progress { get; init; }

    private string InstallPath => Path.Combine(platform.AgentHome, "install.json");

    public async Task<SetupOutcome> RunAsync(string channel, CancellationToken ct = default)
    {
        if (!SetupChannel.IsRepository(Repository)) throw new SetupFailure("Invalid Construct repository.");
        var manifestUrl = SetupChannel.ManifestUrl(channel, Repository) ?? throw new SetupFailure("A channel is stable, nightly or an https manifest URL.");
        if (await RunningAgentAsync(ct).ConfigureAwait(false) is { } running)
        {
            await HandOverAsync(running, ct).ConfigureAwait(false);
            return new(null, HandedOver: true);
        }

        var stoppedCompanion = await StopOldCompanionAsync(ct).ConfigureAwait(false);
        string version;
        Endpoint agent;
        try
        {
            version = await InstallAsync(channel, manifestUrl, ct).ConfigureAwait(false);
            agent = await WaitForAgentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (stoppedCompanion)
        {
            // Leave the user with a running Companion: the setup can simply be run again.
            StartOldCompanion();
            throw;
        }

        // The agent holds the Companion's single-instance mutex; the Companion no longer starts with Windows.
        platform.SetRunValue(CompanionRunValue, null);
        output.WriteLine($"==> The Construct agent {version} is running. Its window finishes the setup.");
        return new(version, HandedOver: false);
    }

    // ---- a running agent ----

    private sealed record Endpoint(int Port, string Token);

    /// <summary>The agent's token file and its health route; null when no agent answers.</summary>
    private async Task<Endpoint?> RunningAgentAsync(CancellationToken ct)
    {
        if (ReadJson(Path.Combine(platform.AgentHome, "endpoint.json")) is not { } file
            || Number(file, "port") is not ({ } port and > 0 and < 65536) || Text(file, "token") is not { Length: > 0 } token) return null;
        try
        {
            using var limit = Limit(ct, TimeSpan.FromSeconds(5));
            using var response = await local.GetAsync($"http://127.0.0.1:{port}/api/health", limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            return Text(health.RootElement, "status") == "ok" ? new(port, token) : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>install.ps1's update path: the running agent opens its window and installs what its channel offers.</summary>
    private async Task HandOverAsync(Endpoint running, CancellationToken ct)
    {
        output.WriteLine("==> The Construct agent is running; it updates itself.");
        // The extension of the installed version goes into VS Code first, so an old control panel left in VS Code is replaced here too.
        if (InstalledVersion() is { } current && SetupChannel.IsVersion(current))
        {
            var folder = Path.Combine(platform.AgentHome, "versions", current);
            if (File.Exists(Path.Combine(folder, VsixName))) await InstallVsCodeExtensionAsync(folder, ct).ConfigureAwait(false);
        }

        // The window first: an update restarts the agent, and the new version opens again what was open.
        await TryPostAsync(running, "/api/shell/open", OpenWindow, ct).ConfigureAwait(false);
        try
        {
            using var offer = await PostAsync(running, "/api/update/check", "{}", ct).ConfigureAwait(false);
            if (Text(offer.RootElement, "available") is { Length: > 0 } available)
            {
                (await PostAsync(running, "/api/update/install", "{}", ct).ConfigureAwait(false)).Dispose();
                output.WriteLine($"    it installs {available} and starts again.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            output.WriteLine("    The agent could not check for an update now; it checks by itself every six hours.");
        }
    }

    private async Task<Endpoint> WaitForAgentAsync(CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            if (await RunningAgentAsync(ct).ConfigureAwait(false) is { } running) return running;
            if (started.Elapsed >= AgentStartLimit) throw new SetupFailure("The Construct agent did not start; run construct-agent.exe repair from its folder for details.");
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }
    }

    // ---- the old Companion ----

    /// <summary>
    /// The old Companion's own way: <c>POST /v1/quit</c> with its endpoint token, then wait for the process. True when
    /// one was running and has quit; a Companion that does not quit fails the setup. It is never killed.
    /// </summary>
    private async Task<bool> StopOldCompanionAsync(CancellationToken ct)
    {
        var file = ReadJson(Path.Combine(platform.LocalAppData, "The-Construct", "companion", "endpoint.json"));
        var pid = file is { } f ? Number(f, "pid") : null;
        // Checked before anything quits: a Companion of this Windows session that its endpoint does not name.
        // (Another user's Companion in another session is left alone and does not count.)
        if (platform.SessionProcesses(CompanionProcess).Any(id => id != pid)) throw new SetupFailure("The Construct Companion is still running; close it from its tray menu and try again.");
        if (pid is not { } companion || !platform.IsRunning(companion)) return false;
        output.WriteLine("==> Asking the Construct Companion to quit ...");
        // From here on the quit runs to its end, bounded, even when the setup is cancelled (Ctrl+C): a Companion that
        // quit is then started again, never left stopped.
        if (Number(file!.Value, "port") is { } port && Text(file.Value, "token") is { } token)
        {
            try
            {
                using var limit = Limit(CancellationToken.None, TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/quit")
                {
                    Content = new StringContent("""{"reason":"update"}""", Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                (await local.SendAsync(request, limit.Token).ConfigureAwait(false)).Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException)
            {
                // It may already be on its way out; the wait below decides.
            }
        }

        var asked = Stopwatch.StartNew();
        while (platform.IsRunning(companion))
        {
            if (asked.Elapsed >= CompanionQuitLimit) throw new SetupFailure("The Construct Companion did not quit; close it from its tray menu and try again.");
            await Task.Delay(Poll, CancellationToken.None).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            StartOldCompanion();
            ct.ThrowIfCancellationRequested();
        }

        return true;
    }

    private void StartOldCompanion()
    {
        var exe = Path.Combine(platform.LocalAppData, "Programs", "ConstructCompanion", "ConstructCompanion.exe");
        if (!File.Exists(exe)) return;
        try
        {
            platform.Start(exe, "--background");
            output.WriteLine("    the Construct Companion was started again.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            output.WriteLine("    Start the Construct Companion again by hand.");
        }
    }

    // ---- the install ----

    private async Task<string> InstallAsync(string channel, Uri manifestUrl, CancellationToken ct)
    {
        output.WriteLine($"==> Reading the {SetupChannel.Name(channel)} channel ...");
        string manifest;
        try
        {
            using var limit = Limit(ct, TimeSpan.FromSeconds(30));
            manifest = await web.GetStringAsync(manifestUrl, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            throw new SetupFailure($"The {SetupChannel.Name(channel)} channel could not be read ({Reason(ex)}). Check the internet connection and try again.");
        }

        var package = SetupChannel.Package(manifest, manifestUrl, channel, Repository);
        Directory.CreateDirectory(platform.AgentHome);
        var target = Path.Combine(platform.AgentHome, "versions", package.Version);
        if (!File.Exists(Path.Combine(target, AgentExecutable)))
        {
            var work = Path.Combine(Path.GetTempPath(), "construct-agent-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(work);
            try
            {
                var zip = Path.Combine(work, "agent.zip");
                output.WriteLine($"==> Downloading the Construct agent {package.Version} ...");
                await DownloadAsync(package, zip, ct).ConfigureAwait(false);
                var partial = target + ".partial";
                if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
                try { ZipFile.ExtractToDirectory(zip, partial); }
                catch (InvalidDataException) { throw new SetupFailure("The agent package could not be unpacked."); }
                if (!File.Exists(Path.Combine(partial, AgentExecutable))) throw new SetupFailure("The agent package has no construct-agent.exe.");
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(partial, target);
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the temp folder's own cleanup */ }
            }
        }

        await InstallVsCodeExtensionAsync(target, ct).ConfigureAwait(false);

        // With a previous version the new one is on trial: its first start self-tests and goes back when it fails (12.2).
        var previous = InstalledVersion();
        if (previous == package.Version) previous = null;
        WriteJson(InstallPath, json =>
        {
            json.WriteString("current", package.Version);
            if (previous is null) json.WriteNull("previous");
            else json.WriteString("previous", previous);
            json.WriteBoolean("pending", previous is not null);
        });
        // The channel this install came from, unless the agent has its own settings already.
        var device = Path.Combine(platform.AgentHome, "device.json");
        if (!File.Exists(device)) WriteJson(device, json => json.WriteString("channel", channel));

        var exe = Path.Combine(target, AgentExecutable);
        platform.SetRunValue(AgentRunValue, $"\"{exe}\" --background");
        // Started with its window: asked over the API instead, the agent answers before its window exists and refuses.
        platform.Start(exe, "--background --open win");
        return package.Version;
    }

    /// <summary>Downloads the package and compares its SHA-256 with the manifest's.</summary>
    private async Task DownloadAsync(SetupPackage package, string path, CancellationToken ct)
    {
        string actual;
        try
        {
            using var limit = Limit(ct, DownloadLimit);
            using var response = await web.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new SetupFailure($"The agent download answered {(int)response.StatusCode}.");
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), limit.Token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                done += read;
                Progress?.Invoke(done, total);
            }

            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new SetupFailure($"The agent package could not be downloaded ({Reason(ex)}). Check the internet connection and try again.");
        }

        if (actual != package.Sha256) throw new SetupFailure("The agent package does not match the SHA-256 in the channel manifest.");
    }

    /// <summary>
    /// The thin VS Code extension from the agent package (<c>construct.vsix</c>), over the old one: same id, higher
    /// version. VS Code not installed is not a failure; VS Code refusing the extension is (13.2).
    /// </summary>
    private async Task InstallVsCodeExtensionAsync(string folder, CancellationToken ct)
    {
        if (platform.FindVsCode() is not { } code)
        {
            output.WriteLine("    VS Code is not installed; no extension to update.");
            return;
        }

        var vsix = Path.Combine(folder, VsixName);
        if (!File.Exists(vsix)) throw new SetupFailure("The agent package has no construct.vsix.");
        // code writes warnings to stderr even when it succeeds: only its exit code counts.
        var exit = await platform.RunAsync(code, ["--install-extension", vsix, "--force"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (exit != 0) throw new SetupFailure($"VS Code did not install the Construct extension (code --install-extension exit {exit}).");
        output.WriteLine("    the Construct extension is installed in VS Code (it starts with the next VS Code window).");
    }

    // ---- helpers ----

    /// <summary><c>current</c> of <c>install.json</c>, or null.</summary>
    private string? InstalledVersion() => ReadJson(InstallPath) is { } state ? Text(state, "current") : null;

    private async Task<bool> TryPostAsync(Endpoint endpoint, string path, string body, CancellationToken ct)
    {
        try
        {
            (await PostAsync(endpoint, path, body, ct).ConfigureAwait(false)).Dispose();
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<JsonDocument> PostAsync(Endpoint endpoint, string path, string body, CancellationToken ct)
    {
        using var limit = Limit(ct, TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        using var response = await local.SendAsync(request, limit.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static CancellationTokenSource Limit(CancellationToken ct, TimeSpan after)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(after);
        return limit;
    }

    private static string Reason(Exception ex) => ex is OperationCanceledException ? "no answer in time" : ex.Message.TrimEnd('.');

    /// <summary>A JSON file's root, or null when it is missing or unreadable (read as text: a BOM is no problem).</summary>
    private static JsonElement? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Written to a file beside it and moved over, UTF-8 without a BOM, as install.ps1 writes it.</summary>
    private static void WriteJson(string path, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }

        var temporary = path + "." + Guid.NewGuid().ToString("n")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>A property by name, ignoring case as PowerShell's ConvertFrom-Json does.</summary>
    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;
}
