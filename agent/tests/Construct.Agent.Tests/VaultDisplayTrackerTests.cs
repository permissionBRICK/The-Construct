namespace Construct.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="VaultDisplayTracker"/> using a manual <see cref="TimeProvider"/> (no sleeps):
/// verifies the ReporterWindow (10 s), DisplayGrace (5 s), DisplayedFor (8 s) and validation semantics.
/// </summary>
public sealed class VaultDisplayTrackerTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;

        public void Set(DateTimeOffset value) => _now = value;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Without_a_reporter_new_approvals_and_entry_requests_pop_at_once_and_only_once()
    {
        var clock = new FakeClock(T0);
        var tracker = new VaultDisplayTracker(clock);
        var approval = DisplayedVaultItem.Approval("eng1", "req-1");
        var entry = DisplayedVaultItem.EntryRequest("eng1", "req-1");

        var first = tracker.Evaluate([approval, entry]);
        Assert.Equal([approval, entry], first.NewlyEligible);
        Assert.Empty(first.Deferred);

        // Subsequent polls do not pop the same items again.
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = tracker.Evaluate([approval, entry]);
        Assert.Empty(second.NewlyEligible);
        Assert.Empty(second.Deferred);
    }

    [Fact]
    public void With_a_reporter_present_an_unreported_approval_waits_for_display_grace_then_pops()
    {
        var clock = new FakeClock(T0);
        var tracker = new VaultDisplayTracker(clock);
        var approval = DisplayedVaultItem.Approval("eng1", "req-1");

        // Heartbeat (empty list) establishes an active reporter.
        tracker.Report(new DisplayedVaultRequest([]));
        Assert.True(tracker.HasActiveReporter());

        var at0 = tracker.Evaluate([approval]);
        Assert.Empty(at0.NewlyEligible);
        Assert.Equal([approval], at0.Deferred);

        // Just before DisplayGrace (5 s) expires: still held back, NOT marked popped.
        clock.Advance(TimeSpan.FromSeconds(4.9));
        var at49 = tracker.Evaluate([approval]);
        Assert.Empty(at49.NewlyEligible);
        Assert.Equal([approval], at49.Deferred);

        // At 5 s: grace elapsed and reporter did not list this item -> pops now.
        clock.Advance(TimeSpan.FromSeconds(0.1));
        var at50 = tracker.Evaluate([approval]);
        Assert.Equal([approval], at50.NewlyEligible);
        Assert.Empty(at50.Deferred);

        // Does not pop a second time.
        clock.Advance(TimeSpan.FromSeconds(2));
        var at70 = tracker.Evaluate([approval]);
        Assert.Empty(at70.NewlyEligible);
        Assert.Empty(at70.Deferred);
    }

    [Fact]
    public void An_approval_or_entry_request_reported_as_displayed_stays_quiet_until_reports_stop()
    {
        var clock = new FakeClock(T0);
        var tracker = new VaultDisplayTracker(clock);
        var approval = DisplayedVaultItem.Approval("eng1", "req-1");
        var entry = DisplayedVaultItem.EntryRequest("eng1", "ask-1");

        tracker.Report(new DisplayedVaultRequest([approval, entry]));
        Assert.True(tracker.IsDisplayedElsewhere(VaultDisplayTracker.KindApproval, "eng1", "req-1"));
        Assert.True(tracker.IsDisplayedElsewhere(VaultDisplayTracker.KindEntryRequest, "eng1", "ask-1"));

        // Polling across multiple 3 s renewals well past DisplayGrace keeps both quiet.
        for (var i = 0; i < 5; i++)
        {
            var eval = tracker.Evaluate([approval, entry]);
            Assert.Empty(eval.NewlyEligible);
            Assert.Equal([approval, entry], eval.Deferred);
            clock.Advance(TimeSpan.FromSeconds(3));
            tracker.Report(new DisplayedVaultRequest([approval, entry]));
        }

        // Reporter stops (e.g., T3 window hidden or closed). Still suppressed while DisplayedFor (8 s) holds.
        clock.Advance(TimeSpan.FromSeconds(7.9));
        var stillSuppressed = tracker.Evaluate([approval, entry]);
        Assert.Empty(stillSuppressed.NewlyEligible);
        Assert.Equal([approval, entry], stillSuppressed.Deferred);
        Assert.True(tracker.IsDisplayedElsewhere(VaultDisplayTracker.KindApproval, "eng1", "req-1"));

        // At 8 s since last report: DisplayedFor expires -> both become newly eligible and pop.
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.False(tracker.IsDisplayedElsewhere(VaultDisplayTracker.KindApproval, "eng1", "req-1"));
        var popped = tracker.Evaluate([approval, entry]);
        Assert.Equal([approval, entry], popped.NewlyEligible);
        Assert.Empty(popped.Deferred);
    }

    [Fact]
    public void If_the_reporter_drops_an_item_from_its_report_it_pops_once_displayed_for_expires()
    {
        var clock = new FakeClock(T0);
        var tracker = new VaultDisplayTracker(clock);
        var a1 = DisplayedVaultItem.Approval("eng1", "req-1");
        var a2 = DisplayedVaultItem.Approval("eng1", "req-2");

        tracker.Report(new DisplayedVaultRequest([a1, a2]));
        Assert.Empty(tracker.Evaluate([a1, a2]).NewlyEligible);

        // Reporter keeps sending heartbeats with only a1; a2 is no longer displayed inline.
        clock.Advance(TimeSpan.FromSeconds(6));
        tracker.Report(new DisplayedVaultRequest([a1]));
        Assert.Empty(tracker.Evaluate([a1, a2]).NewlyEligible);

        // At t=8s: a2's mark from t=0 expires while a1's mark from t=6s is still fresh.
        clock.Advance(TimeSpan.FromSeconds(2));
        var eval = tracker.Evaluate([a1, a2]);
        Assert.Equal([a2], eval.NewlyEligible);
        Assert.Equal([a1], eval.Deferred);
    }

    [Fact]
    public void Heartbeat_keeps_reporter_active_and_disappearing_reporter_ends_grace_at_once()
    {
        var clock = new FakeClock(T0);
        var tracker = new VaultDisplayTracker(clock);
        var approval = DisplayedVaultItem.Approval("eng1", "req-1");

        // Heartbeat every 6 s keeps reporter active past ReporterWindow (10 s).
        tracker.Report(new DisplayedVaultRequest([]));
        clock.Advance(TimeSpan.FromSeconds(6));
        tracker.Report(new DisplayedVaultRequest([]));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(tracker.HasActiveReporter());

        // Approval arrives at t=12s (6 s after last heartbeat at t=6s): enters grace.
        Assert.Empty(tracker.Evaluate([approval]).NewlyEligible);

        // At t=16s (10 s after last heartbeat at t=6s): ReporterWindow expires before 5 s grace finishes -> pops at once.
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(tracker.HasActiveReporter());
        var eval = tracker.Evaluate([approval]);
        Assert.Equal([approval], eval.NewlyEligible);
        Assert.Empty(eval.Deferred);
    }

    [Fact]
    public void Temporarily_unreachable_engine_preserves_grace_and_does_not_repop_already_popped_items()
    {
        var clock = new FakeClock(T0);
        var tracker = new VaultDisplayTracker(clock);
        var waiting = DisplayedVaultItem.Approval("eng1", "req-wait");
        var popped = DisplayedVaultItem.Approval("eng2", "req-pop");

        // Pop eng2 item with no reporter.
        Assert.Equal([popped], tracker.Evaluate([popped]).NewlyEligible);

        // Reporter becomes active and eng1 item arrives.
        tracker.Report(new DisplayedVaultRequest([]));
        Assert.Equal([waiting], tracker.Evaluate([waiting, popped]).Deferred);

        // Next poll at +3 s: eng1 and eng2 are briefly unreachable.
        clock.Advance(TimeSpan.FromSeconds(3));
        var duringOutage = tracker.Evaluate([], unreachableEngines: ["eng1", "eng2"]);
        Assert.Empty(duringOutage.NewlyEligible);

        // At +5 s from original arrival: eng1 comes back and grace has elapsed -> waiting pops, popped does NOT re-pop.
        clock.Advance(TimeSpan.FromSeconds(2));
        var recovered = tracker.Evaluate([waiting, popped]);
        Assert.Equal([waiting], recovered.NewlyEligible);
        Assert.Empty(recovered.Deferred);
    }

    [Fact]
    public void Wall_clock_step_backward_does_not_keep_items_suppressed()
    {
        var clock = new FakeClock(T0);
        var tracker = new VaultDisplayTracker(clock);
        var approval = DisplayedVaultItem.Approval("eng1", "req-1");

        tracker.Report(new DisplayedVaultRequest([approval]));
        Assert.Empty(tracker.Evaluate([approval]).NewlyEligible);

        clock.Set(T0 - TimeSpan.FromSeconds(5));
        Assert.False(tracker.HasActiveReporter());
        Assert.False(tracker.IsDisplayedElsewhere(VaultDisplayTracker.KindApproval, "eng1", "req-1"));
        Assert.Equal([approval], tracker.Evaluate([approval]).NewlyEligible);
    }

    [Fact]
    public void Report_validates_item_count_and_fields()
    {
        var tracker = new VaultDisplayTracker(new FakeClock(T0));

        // 50 items is accepted; 51 is refused.
        var fifty = Enumerable.Range(0, VaultDisplayTracker.MaxDisplayed)
            .Select(i => DisplayedVaultItem.Approval("eng1", $"req-{i}")).ToArray();
        tracker.Report(new DisplayedVaultRequest(fifty));
        var fiftyOne = fifty.Append(DisplayedVaultItem.Approval("eng1", "req-50")).ToArray();
        Assert.Equal("too-many-items", Assert.Throws<AgentFailure>(() => tracker.Report(new DisplayedVaultRequest(fiftyOne))).Code);

        // Missing items, bad kind, bad engine, bad id are all refused with 400.
        Assert.Equal(400, Assert.Throws<AgentFailure>(() => tracker.Report(null)).Status);
        Assert.Equal(400, Assert.Throws<AgentFailure>(() => tracker.Report(new DisplayedVaultRequest(null!))).Status);
        Assert.Equal("invalid-kind", Assert.Throws<AgentFailure>(() => tracker.Report(new DisplayedVaultRequest([new("file", "eng1", "f1")]))).Code);
        Assert.Equal("invalid-engine", Assert.Throws<AgentFailure>(() => tracker.Report(new DisplayedVaultRequest([new("approval", "../eng", "r1")]))).Code);
        Assert.Equal("invalid-engine", Assert.Throws<AgentFailure>(() => tracker.Report(new DisplayedVaultRequest([new("approval", "", "r1")]))).Code);
        Assert.Equal("invalid-id", Assert.Throws<AgentFailure>(() => tracker.Report(new DisplayedVaultRequest([new("approval", "eng1", "bad id")]))).Code);
        Assert.Equal("invalid-id", Assert.Throws<AgentFailure>(() => tracker.Report(new DisplayedVaultRequest([new("approval", "eng1", "..")]))).Code);
    }
}
