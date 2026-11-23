using System.Collections.Concurrent;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.SignIn;

/// <summary>
/// One coding agent's sign-in on a VM (design 11.8, S34): the agent's own sign-in command in a terminal on the VM.
/// What its terminal shows lives only here, in memory, while the sign-in runs: never in the database, a log or the
/// audit log, since it can carry one-time codes and sign-in links. When it ends, only its outcome and the signed-in
/// state stay. An engine restart ends it.
/// </summary>
public sealed class AgentSignInSession
{
    internal AgentSignInSession(string id, Vm vm, string agent, DateTimeOffset started)
    {
        Id = id; Vm = vm.Name; Owner = vm.Owner; VmCreated = vm.Created; Agent = agent; Started = started;
    }

    internal readonly Lock Gate = new();
    internal IGuestSession? Guest;
    internal SshTarget? Target;
    internal string? EndRequested;
    internal DateTimeOffset LastAnnounced;
    internal bool AnnouncePending;
    internal readonly List<string> Notes = [];
    internal string Screen = "";
    internal AgentSignInLine? End;
    internal AgentSignInLine? Link;

    public string Id { get; }
    public string Vm { get; }
    public string Owner { get; }
    public DateTimeOffset VmCreated { get; }
    /// <summary>The agent, by its <see cref="CodingAgents.Known"/> id.</summary>
    public string Agent { get; }
    public DateTimeOffset Started { get; }
    public DateTimeOffset? Ended { get; internal set; }
    public bool Running => Ended is null;

    /// <summary>
    /// <c>done</c> (the sign-in ended well), <c>failed</c> (it ended otherwise), <c>stopped</c> (the user ended it,
    /// another sign-in took its place, or it took too long), <c>error</c> (the connection or the script failed);
    /// null while it runs.
    /// </summary>
    public string? Outcome { get; internal set; }

    /// <summary>Counts every change of <see cref="Text"/>, so a reader sees whether it has the newest.</summary>
    public long Revision { get; internal set; }

    /// <summary>Whether the agent is signed in after the session, as the VM checked it at the end; null before.</summary>
    public bool? SignedIn { get; internal set; }

    /// <summary>The client said it forwards a loopback redirect (design 11.8, "No pasted code").</summary>
    public bool Browser { get; internal init; }

    /// <summary>The last link the CLI opened while the sign-in runs, with its loopback port on the VM; null otherwise.</summary>
    public (string Url, int? Port)? Opened
    {
        get
        {
            lock (Gate) return Link is { Url: { } url } ? (url, Link.Port) : null;
        }
    }

    /// <summary>
    /// What the terminal shows, followed by what the script or the connection said besides; empty once the sign-in
    /// ended.
    /// </summary>
    public string Text
    {
        get
        {
            lock (Gate)
            {
                var extra = Notes.Concat(End?.Text is { Length: > 0 } t ? [t] : []).ToList();
                return extra.Count == 0 ? Screen : (Screen.Length > 0 ? Screen + "\n\n" : "") + string.Join('\n', extra);
            }
        }
    }
}

/// <summary>Why a sign-in could not start.</summary>
public sealed class AgentSignInRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The sign-in sessions, one at a time per VM: a new one ends the one before. A session runs
/// <c>bin/construct-agent-sign-in.py</c> on the VM over SSH, keeps the terminal's latest text, sends the user's
/// input to its stdin, and announces itself on the change stream, to the VM's owner only
/// (<c>agent-sign-in.started</c>, <c>agent-sign-in.output</c> at most a few times a second,
/// <c>agent-sign-in.ended</c>). When it ends, the sign-in state the VM checked goes into the VM's agent report.
/// It takes no lock on the VM: a job that reinstalls or restarts the VM ends it by ending its connection.
/// </summary>
public sealed class AgentSignIns(IGuestSessionRunner guests, ISshRunner ssh, IProvisionTargets targets, VmAgents agents,
    IVmRepository vms, IClock clock, ChangeFeed changes, ILogger<AgentSignIns> logger)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The shortest gap between two output announcements of a session.</summary>
    public static TimeSpan AnnounceInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    // VM name -> its latest session (an ended one stays readable until the next starts).
    private readonly ConcurrentDictionary<string, AgentSignInSession> _byVm = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _starting = new(1, 1);

    public AgentSignInSession? Latest(string vm) => _byVm.TryGetValue(vm, out var session) ? session : null;

    /// <summary>Starts <paramref name="agent"/>'s sign-in on <paramref name="vm"/>, ending the VM's running one first.</summary>
    public async Task<AgentSignInSession> StartAsync(Vm vm, string agent, CancellationToken ct) => await StartAsync(vm, agent, false, null, ct);

    /// <summary>
    /// Starts <paramref name="agent"/>'s sign-in on <paramref name="vm"/>, ending the VM's running one first.
    /// <paramref name="browser"/>: the client forwards a loopback redirect, so Codex uses its browser sign-in;
    /// <paramref name="provider"/>: the provider OpenCode's sign-in goes straight to (<c>openrouter</c>).
    /// </summary>
    public async Task<AgentSignInSession> StartAsync(Vm vm, string agent, bool browser, string? provider, CancellationToken ct)
    {
        var target = await targets.ResolveAsync(vm, ct)
            ?? throw new AgentSignInRefusedException("vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial.");
        await _starting.WaitAsync(ct);
        try
        {
            if (Latest(vm.Name) is { Running: true } running) await EndAsync(running, "stopped");
            var session = new AgentSignInSession(Guid.NewGuid().ToString("n"), vm, agent, clock.UtcNow) { Target = target, Browser = browser };
            _byVm[vm.Name] = session;
            changes.Publish("agent-sign-in.started", session.Id, session.Vm, session.Owner, ownerOnly: true);
            string? failed = null;
            lock (session.Gate)
            {
                try
                {
                    session.Guest = guests.Start(target, AgentSignInScript.StartCommand(session.Id, agent, target.IsRoot, browser, provider), line => OnLine(session, line));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed = SafeError.Describe(ex);
                }
            }

            if (session.Guest is { } guest) _ = Task.Run(() => FollowAsync(session, guest), CancellationToken.None);
            else
            {
                Finish(session, "error");
                throw new AgentSignInRefusedException("sign-in-not-started", "The sign-in could not be started: " + failed);
            }

            return session;
        }
        finally { _starting.Release(); }
    }

    /// <summary>
    /// Types a line, a secret or a key in the session's terminal. False when the session is over. Nothing of it is kept
    /// here: it goes to the VM's stdin and nowhere else.
    /// </summary>
    public async Task<bool> InputAsync(AgentSignInSession session, string? text, string? key, CancellationToken ct, string? secret = null)
    {
        IGuestSession? guest;
        lock (session.Gate)
        {
            if (!session.Running || session.EndRequested is not null) return false;
            guest = session.Guest;
        }

        if (guest is null) return false;
        try { await guest.WriteLineAsync(AgentSignInScript.InputLine(text, key, secret), ct); }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>Ends a session (the user's cancel, or a new sign-in on the VM) and waits until it is gone on the VM.</summary>
    public async Task EndAsync(AgentSignInSession session, string outcome)
    {
        IGuestSession? guest;
        lock (session.Gate)
        {
            if (!session.Running) return;
            session.EndRequested ??= outcome;
            guest = session.Guest;
        }

        if (guest is not null)
        {
            // The end of stdin stops the sign-in on the VM; the stop command makes sure of it.
            await guest.DisposeAsync();
            await guest.Exited.ContinueWith(_ => { }, TaskScheduler.Default);
            await StopOnGuestAsync(session);
        }

        Finish(session, session.EndRequested ?? outcome);
    }

    private void OnLine(AgentSignInSession session, string raw)
    {
        var line = AgentSignInScript.Parse(raw);
        lock (session.Gate)
        {
            if (!session.Running) return;
            switch (line.Type)
            {
                case "screen":
                    session.Screen = line.Text ?? "";
                    break;
                case "browser" when line.Url is not null:
                    // Kept in the log after the session ends, without the link itself: whether the CLI opened one.
                    if (session.Link?.Url != line.Url)
                        logger.LogInformation("The sign-in {Session} of {Agent} on {Vm} opened a link that returns to port {Port}.",
                            session.Id, session.Agent, session.Vm, line.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
                    session.Link = line;
                    break;
                case "end":
                    session.End = line;
                    session.SignedIn = line.SignedIn;
                    break;
                case "log" when !string.IsNullOrWhiteSpace(line.Text):
                    if (session.Notes.Count < 20) session.Notes.Add(line.Text!);
                    break;
                default:
                    return;
            }

            session.Revision++;
        }

        Announce(session);
    }

    private async Task FollowAsync(AgentSignInSession session, IGuestSession guest)
    {
        try { await guest.Exited; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("The sign-in connection to {Vm} ended: {Error}", session.Vm, SafeError.Describe(ex));
        }

        string outcome;
        AgentSignInLine? end;
        lock (session.Gate)
        {
            // Ended on request: EndAsync stops it on the VM and then marks it over.
            if (session.EndRequested is not null) return;
            end = session.End;
            outcome = end?.Outcome is "done" or "failed" or "stopped" ? end.Outcome : "error";
        }

        await guest.DisposeAsync();
        if (end is null) await StopOnGuestAsync(session);
        if (end?.SignedIn is { } signedIn) await RecordAsync(session, signedIn, end.Version);
        Finish(session, outcome);
    }

    /// <summary>The VM's agent report gets what the VM checked at the end, so the pages show it at once.</summary>
    private async Task RecordAsync(AgentSignInSession session, bool signedIn, string? version)
    {
        try
        {
            if (await vms.GetAsync(session.Vm, CancellationToken.None) is { } vm && vm.Created == session.VmCreated)
                await agents.MarkSignedInAsync(vm, AgentSignInScript.Tool(session.Agent), signedIn, version, clock.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Recording the sign-in on {Vm} failed: {Error}", session.Vm, SafeError.Describe(ex));
        }
    }

    private async Task StopOnGuestAsync(AgentSignInSession session)
    {
        if (session.Target is not { } target) return;
        try
        {
            await ssh.RunAsync(target, AgentSignInScript.StopCommand(session.Id, target.IsRoot), null, null, StopTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Stopping the sign-in on {Vm} failed: {Error}", session.Vm, SafeError.Describe(ex));
        }
    }

    private void Finish(AgentSignInSession session, string outcome)
    {
        lock (session.Gate)
        {
            if (!session.Running) return;
            session.Outcome = outcome;
            session.Ended = clock.UtcNow;
            session.Guest = null;
            // What the terminal showed goes with the sign-in: codes and links are not kept for later readers.
            session.Screen = "";
            session.Notes.Clear();
            session.End = null;
            session.Link = null;
            session.Revision++;
        }

        changes.Publish("agent-sign-in.ended", session.Id, session.Vm, session.Owner, ownerOnly: true);
    }

    private void Announce(AgentSignInSession session)
    {
        bool now;
        lock (session.Gate)
        {
            var at = clock.UtcNow;
            now = at - session.LastAnnounced >= AnnounceInterval;
            if (now) session.LastAnnounced = at;
            else if (!session.AnnouncePending)
            {
                session.AnnouncePending = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(AnnounceInterval);
                    lock (session.Gate) { session.AnnouncePending = false; session.LastAnnounced = clock.UtcNow; }
                    changes.Publish("agent-sign-in.output", session.Id, session.Vm, session.Owner, ownerOnly: true);
                });
            }
        }

        if (now) changes.Publish("agent-sign-in.output", session.Id, session.Vm, session.Owner, ownerOnly: true);
    }
}
