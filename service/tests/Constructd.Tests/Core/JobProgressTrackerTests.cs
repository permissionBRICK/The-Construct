using Constructd.Core.Services;

namespace Constructd.Tests.Core;

/// <summary>One overall percentage per job (F24/F32): weights from the previous run, equal ones on the first, never back.</summary>
public sealed class JobProgressTrackerTests
{
    private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private JobProgressTracker Tracker() => new(() => _now);

    private void Wait(double seconds) => _now = _now.AddSeconds(seconds);

    [Fact]
    public void A_first_run_weighs_every_expected_sub_step_the_same()
    {
        var t = Tracker();
        t.Expect(["a", "b", "c", "d"]);
        Assert.Null(t.Snapshot());

        t.Begin("a", "Doing A");
        Assert.Equal((0, "Doing A"), t.Snapshot());
        t.Begin("b", "Doing B");
        Assert.Equal((25, "Doing B"), t.Snapshot());
        t.Fraction(0.5);
        Assert.Equal(37, t.Snapshot()!.Value.Percent);
        t.Begin("d", "Doing D");
        // c did not run: it stays in the rest until the end.
        Assert.Equal(50, t.Snapshot()!.Value.Percent);
    }

    [Fact]
    public void A_sub_step_only_some_runs_have_shows_but_stays_out_of_the_next_plan()
    {
        var t = Tracker();
        t.Expect(["start", "source", "provision"]);
        t.Begin("start", "Starting the VM", planned: false);
        Assert.Equal((0, "Starting the VM"), t.Snapshot());
        Wait(60);
        t.Begin("source", "Preparing source");
        Assert.Equal(33, t.Snapshot()!.Value.Percent);
        Wait(10);
        t.Begin("provision", "Provisioning");
        Wait(30);

        // A next run of a VM that runs already does not wait for a start it will not have.
        var plan = t.Measured()!;
        Assert.Equal(["source", "provision"], plan.Steps.Select(s => s.Key));
    }

    [Fact]
    public void Open_sub_steps_stand_in_for_sections_named_only_as_they_come()
    {
        var t = Tracker();
        t.Expect(["provision", "finish"], "guest:", 2);
        t.Begin("provision", "Provisioning");
        t.Begin("guest:one", "One");
        // provision done; guest:one current; guest:two (open) and finish to come: 1 of 4.
        Assert.Equal(25, t.Snapshot()!.Value.Percent);
        t.Begin("guest:two", "Two");
        Assert.Equal(50, t.Snapshot()!.Value.Percent);
        // More sections than expected: each weighs one more, the bar does not go back.
        t.Begin("guest:three", "Three");
        Assert.Equal(60, t.Snapshot()!.Value.Percent);
        t.Begin("finish", "Finishing");
        Assert.Equal(80, t.Snapshot()!.Value.Percent);
    }

    [Fact]
    public void A_plan_weighs_each_sub_step_by_its_duration_in_the_previous_run()
    {
        var t = Tracker();
        t.UsePlan(new ProgressPlan([new("download", "Downloading ISO", 90), new("build", "Building image", 9), new("finish", "Finishing", 1)]));
        t.Expect(["ignored", "because", "a", "plan", "is", "set"]);

        t.Begin("download", "Downloading ISO");
        t.Fraction(0.5);
        Assert.Equal((45, "Downloading ISO"), t.Snapshot());
        t.Fraction(0.25); // a retry does not move it back
        Assert.Equal(45, t.Snapshot()!.Value.Percent);
        t.Begin("build", "Building image");
        Assert.Equal(90, t.Snapshot()!.Value.Percent);
        t.Begin("finish", "Finishing");
        Assert.Equal(99, t.Snapshot()!.Value.Percent);
    }

    [Fact]
    public void The_percentage_never_goes_back_and_stays_below_100_while_the_job_runs()
    {
        var t = Tracker();
        t.UsePlan(new ProgressPlan([new("a", "A", 10), new("b", "B", 10)]));
        t.Begin("a", "A");
        t.Fraction(0.9);
        Assert.Equal(45, t.Snapshot()!.Value.Percent);
        // A sub-step the previous run did not have makes the rest bigger (33 %); the bar holds where it was.
        t.Begin("x", "X");
        Assert.Equal((45, "X"), t.Snapshot());
        t.Begin("y", "Y");
        Assert.Equal(50, t.Snapshot()!.Value.Percent);
        t.Begin("b", "B");
        t.Fraction(1);
        Assert.Equal(99, t.Snapshot()!.Value.Percent);
    }

    [Fact]
    public void The_measured_run_is_the_next_plan_in_order_with_repeated_sub_steps_added_up()
    {
        var t = Tracker();
        t.Begin("a", "A");
        Wait(5);
        t.Begin("b", "B");
        Wait(2);
        t.Begin("a", "A");
        Wait(1);
        var plan = t.Measured()!;
        Assert.Equal([("a", "A", 6d), ("b", "B", 2d)], plan.Steps.Select(s => (s.Key, s.Label, s.Seconds)));
        Assert.Null(Tracker().Measured());
    }

    [Fact]
    public void Current_follows_the_job_flow_only()
    {
        var t = Tracker();
        Assert.Null(JobProgressTracker.Current);
        using (JobProgressTracker.Use(t))
        {
            Assert.Same(t, JobProgressTracker.Current);
            Assert.Same(t, Task.Run(() => JobProgressTracker.Current).Result);
        }

        Assert.Null(JobProgressTracker.Current);
    }
}
