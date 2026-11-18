using Iemas.Api.Jobs;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iemas.Tests.Jobs;

public class JobMonitoringTests
{
    private sealed class FakeSource : IHangfireSnapshotSource
    {
        public List<RecurringJobInfo> Recurring { get; } = new();
        public Dictionary<string, int> Enqueued { get; } = new();
        public Dictionary<string, int> Scheduled { get; } = new();
        public List<(string, DateTimeOffset?, string?)> Failed { get; } = new();

        public HangfireSnapshot Capture() => new(Recurring, Enqueued, Scheduled, new Dictionary<string, int>(), Failed);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private const string IntakeId = "email-intake-poll-all-accounts";

    private static (JobMonitoringService Svc, FakeSource Source, JobExecutionTracker Tracker) Create()
    {
        var source = new FakeSource();
        var tracker = new JobExecutionTracker();
        var clock = new FakeTimeProvider(Now);
        return (new JobMonitoringService(source, tracker, clock), source, tracker);
    }

    private static RecurringJobInfo Registered(string id, string cron = "* * * * *", string? state = null, DateTimeOffset? last = null) =>
        new(id, cron, last, null, state, null, Now.AddDays(-1));

    private static Iemas.Api.Jobs.JobStatusDto Intake(JobMonitoringService svc) =>
        svc.GetReport().Jobs.Single(j => j.Id == IntakeId);

    [Fact]
    public void UnregisteredJob_IsNotRegistered_AndDoesNotAffectOverall()
    {
        var (svc, _, _) = Create();
        var report = svc.GetReport();
        Assert.All(report.Jobs, j => Assert.Equal("NotRegistered", j.Status));
        Assert.Equal("Healthy", report.OverallStatus);
    }

    [Fact]
    public void RecentSuccess_IsHealthy_WithDurations()
    {
        var (svc, src, tracker) = Create();
        src.Recurring.Add(Registered(IntakeId));
        tracker.Record("email-intake", Now.AddSeconds(-90), TimeSpan.FromMilliseconds(200), true, null);
        tracker.Record("email-intake", Now.AddSeconds(-30), TimeSpan.FromMilliseconds(400), true, null);

        var job = Intake(svc);
        Assert.Equal("Healthy", job.Status);
        Assert.Equal(400, job.LastDurationMs);
        Assert.Equal(300, job.AverageDurationMs);
        Assert.Equal(0, job.FailedLast24h);
    }

    [Fact]
    public void NoSuccessFor3xInterval_IsStale()
    {
        var (svc, src, tracker) = Create();
        src.Recurring.Add(Registered(IntakeId, "* * * * *"));
        tracker.Record("email-intake", Now.AddMinutes(-4), TimeSpan.FromSeconds(1), true, null);

        Assert.Equal("Stale", Intake(svc).Status);
    }

    [Fact]
    public void JustUnder3xInterval_IsStillHealthy()
    {
        var (svc, src, tracker) = Create();
        src.Recurring.Add(Registered(IntakeId, "* * * * *"));
        tracker.Record("email-intake", Now.AddMinutes(-2), TimeSpan.FromSeconds(1), true, null);

        Assert.Equal("Healthy", Intake(svc).Status);
    }

    [Fact]
    public void LatestRunFailed_IsFailing_WithErrorAndCounts()
    {
        var (svc, src, tracker) = Create();
        src.Recurring.Add(Registered(IntakeId));
        src.Scheduled["RunEmailIntakeAsync"] = 1;
        tracker.Record("email-intake", Now.AddSeconds(-50), TimeSpan.FromSeconds(1), true, null);
        tracker.Record("email-intake", Now.AddSeconds(-20), TimeSpan.FromSeconds(1), false, "IMAP down");

        var job = Intake(svc);
        Assert.Equal("Failing", job.Status);
        Assert.Equal("IMAP down", job.LastError);
        Assert.Equal(1, job.FailedLast24h);
        Assert.Equal(1, job.ConsecutiveFailures);
        Assert.Equal(1, job.RetryingCount);
        Assert.NotNull(job.LastSuccessAt);
        Assert.Equal("Failing", svc.GetReport().OverallStatus);
    }

    [Fact]
    public void FailureThenSuccess_RecoversToHealthy_ButKeepsLastFailure()
    {
        var (svc, src, tracker) = Create();
        src.Recurring.Add(Registered(IntakeId));
        tracker.Record("email-intake", Now.AddSeconds(-50), TimeSpan.FromSeconds(1), false, "boom");
        tracker.Record("email-intake", Now.AddSeconds(-10), TimeSpan.FromSeconds(1), true, null);

        var job = Intake(svc);
        Assert.Equal("Healthy", job.Status);
        Assert.Equal("boom", job.LastError);
        Assert.Equal(1, job.FailedLast24h);
    }

    [Fact]
    public void AfterRestart_FallsBackToHangfireLastState()
    {
        var (svc, src, _) = Create();
        src.Recurring.Add(Registered(IntakeId, state: "Succeeded", last: Now.AddSeconds(-30)));
        var healthy = Intake(svc);
        Assert.Equal("Healthy", healthy.Status);
        Assert.Equal(Now.AddSeconds(-30), healthy.LastSuccessAt);

        src.Recurring.Clear();
        src.Recurring.Add(Registered(IntakeId, state: "Failed", last: Now.AddSeconds(-30)));
        Assert.Equal("Failing", Intake(svc).Status);
    }

    [Fact]
    public void Tracker_KeepsOnlyRecentRuns()
    {
        var tracker = new JobExecutionTracker();
        for (var i = 0; i < 350; i++) tracker.Record("x", Now, TimeSpan.Zero, true, null);
        Assert.Equal(200, tracker.Runs("x").Count);
    }

    [Theory]
    [InlineData("* * * * *", 1)]
    [InlineData("*/5 * * * *", 5)]
    [InlineData("*/10 * * * *", 10)]
    [InlineData("0 * * * *", 60)]
    public void CronInterval_IsParsed(string cron, double minutes) =>
        Assert.Equal(minutes, JobMonitoringService.ParseCronInterval(cron)!.Value.TotalMinutes);

    [Fact]
    public void CronInterval_UnknownFormat_IsNull() =>
        Assert.Null(JobMonitoringService.ParseCronInterval("0 8 * * 1-5"));
}
