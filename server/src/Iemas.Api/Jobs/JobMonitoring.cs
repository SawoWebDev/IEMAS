using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace Iemas.Api.Jobs;

public enum JobHealthStatus
{
    Healthy,
    Stale,
    Failing,
    /// <summary>Not registered on this server (switched off in configuration). Ignored by health checks.</summary>
    NotRegistered,
}

/// <summary>One monitored recurring job: its Hangfire id, the name <see cref="RecurringJobGuards"/> records runs under, and how often it should run.</summary>
public sealed record MonitoredJobDefinition(
    string RecurringJobId, string TrackerName, string MethodName, string Label, TimeSpan DefaultInterval, bool Core);

public static class MonitoredJobs
{
    public static readonly IReadOnlyList<MonitoredJobDefinition> All = new[]
    {
        new MonitoredJobDefinition("email-intake-poll-all-accounts", "email-intake", nameof(RecurringJobGuards.RunEmailIntakeAsync), "Email intake", TimeSpan.FromMinutes(1), true),
        new MonitoredJobDefinition("ai-classification-poll-pending-messages", "email-classification", nameof(RecurringJobGuards.RunEmailClassificationAsync), "AI classification", TimeSpan.FromMinutes(2), true),
        new MonitoredJobDefinition("case-workflow-poll-important-messages", "case-workflow", nameof(RecurringJobGuards.RunCaseWorkflowAsync), "Case workflow", TimeSpan.FromMinutes(2), true),
        new MonitoredJobDefinition("reply-verification-poll-awaiting-reply-cases", "conversation-sync", nameof(RecurringJobGuards.RunConversationSyncAsync), "Conversation sync (Sent folders)", TimeSpan.FromMinutes(2), true),
        new MonitoredJobDefinition("inquiry-supervision", "inquiry-supervision", nameof(RecurringJobGuards.RunInquirySupervisionAsync), "Inquiry supervision", TimeSpan.FromMinutes(1), true),
        new MonitoredJobDefinition("reminder-engine-execute-due-reminders", "reminders", nameof(RecurringJobGuards.RunRemindersAsync), "Reminders", TimeSpan.FromMinutes(5), false),
        new MonitoredJobDefinition("escalation-engine-evaluate-cases", "escalations", nameof(RecurringJobGuards.RunEscalationsAsync), "Escalations", TimeSpan.FromMinutes(10), false),
    };
}

public sealed record JobRunRecord(DateTimeOffset StartedAt, TimeSpan Duration, bool Succeeded, string? Error);

/// <summary>
/// In-process record of recent job runs (per job, bounded). Complements Hangfire's own storage, which
/// only keeps the latest state per recurring job: this gives durations, averages and a 24 h failure count
/// without any schema change. It is empty after a restart; the monitor then falls back to Hangfire's data.
/// </summary>
public sealed class JobExecutionTracker
{
    private const int MaxRunsPerJob = 200;
    private readonly ConcurrentDictionary<string, List<JobRunRecord>> _runs = new();

    public void Record(string trackerName, DateTimeOffset startedAt, TimeSpan duration, bool succeeded, string? error)
    {
        var list = _runs.GetOrAdd(trackerName, _ => new List<JobRunRecord>());
        lock (list)
        {
            list.Add(new JobRunRecord(startedAt, duration, succeeded, Truncate(error)));
            if (list.Count > MaxRunsPerJob) list.RemoveRange(0, list.Count - MaxRunsPerJob);
        }
    }

    public IReadOnlyList<JobRunRecord> Runs(string trackerName)
    {
        if (!_runs.TryGetValue(trackerName, out var list)) return Array.Empty<JobRunRecord>();
        lock (list) return list.ToList();
    }

    // Error text is shown in the CMS and may echo input; keep it short.
    private static string? Truncate(string? s) => s is { Length: > 500 } ? s[..500] : s;
}

/// <summary>What Hangfire storage says about one recurring job right now.</summary>
public sealed record RecurringJobInfo(
    string Id, string? Cron, DateTimeOffset? LastExecution, DateTimeOffset? NextExecution, string? LastJobState, string? Error, DateTimeOffset? CreatedAt);

/// <summary>Counts of queued/scheduled/failed Hangfire jobs, keyed by the guard method that would run.</summary>
public sealed record HangfireSnapshot(
    IReadOnlyList<RecurringJobInfo> Recurring,
    IReadOnlyDictionary<string, int> EnqueuedByMethod,
    IReadOnlyDictionary<string, int> ScheduledByMethod,
    IReadOnlyDictionary<string, int> ProcessingByMethod,
    IReadOnlyList<(string Method, DateTimeOffset? FailedAt, string? Message)> Failed);

public interface IHangfireSnapshotSource
{
    HangfireSnapshot Capture();
}

public sealed class HangfireSnapshotSource : IHangfireSnapshotSource
{
    private const int PageSize = 500;

    public HangfireSnapshot Capture()
    {
        var storage = JobStorage.Current;
        var recurring = new List<RecurringJobInfo>();
        using (var connection = storage.GetConnection())
        {
            foreach (var r in connection.GetRecurringJobs())
            {
                recurring.Add(new RecurringJobInfo(r.Id, r.Cron,
                    r.LastExecution is { } le ? new DateTimeOffset(DateTime.SpecifyKind(le, DateTimeKind.Utc)) : null,
                    r.NextExecution is { } ne ? new DateTimeOffset(DateTime.SpecifyKind(ne, DateTimeKind.Utc)) : null,
                    r.LastJobState, r.Error,
                    r.CreatedAt is { } ca ? new DateTimeOffset(DateTime.SpecifyKind(ca, DateTimeKind.Utc)) : null));
            }
        }

        var api = storage.GetMonitoringApi();
        var enqueued = new Dictionary<string, int>();
        foreach (var queue in api.Queues())
        {
            foreach (var j in api.EnqueuedJobs(queue.Name, 0, PageSize))
                Bump(enqueued, j.Value?.Job?.Method.Name);
        }
        var scheduled = new Dictionary<string, int>();
        foreach (var j in api.ScheduledJobs(0, PageSize)) Bump(scheduled, j.Value?.Job?.Method.Name);
        var processing = new Dictionary<string, int>();
        foreach (var j in api.ProcessingJobs(0, PageSize)) Bump(processing, j.Value?.Job?.Method.Name);
        var failed = new List<(string, DateTimeOffset?, string?)>();
        foreach (var j in api.FailedJobs(0, PageSize))
        {
            var v = j.Value;
            if (v?.Job is null) continue;
            failed.Add((v.Job.Method.Name, v.FailedAt is { } f ? new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Utc)) : null, v.ExceptionMessage));
        }
        return new HangfireSnapshot(recurring, enqueued, scheduled, processing, failed);
    }

    private static void Bump(Dictionary<string, int> map, string? method)
    {
        if (method is null) return;
        map[method] = map.GetValueOrDefault(method) + 1;
    }
}

public sealed record JobStatusDto(
    string Id, string Label, bool Core, string Status, string StatusReason, string? Cron, double ExpectedIntervalMinutes,
    DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, string? LastError,
    double? LastDurationMs, double? AverageDurationMs, int RunsTracked, int ConsecutiveFailures,
    int FailedLast24h, int RetryingCount, int EnqueuedCount, int ScheduledCount, int ProcessingCount, int FailedInStorage,
    DateTimeOffset? NextExecution);

public sealed record JobMonitoringReport(string OverallStatus, DateTimeOffset GeneratedAt, IReadOnlyList<JobStatusDto> Jobs);

/// <summary>
/// Combines Hangfire storage (what is registered, last state, queue depths) with the in-process run
/// tracker (durations, failures) into a per-job status: Healthy, Stale (no success in 3x the expected
/// interval) or Failing (latest run failed, or 3+ failures in a row).
/// </summary>
public sealed class JobMonitoringService
{
    public const double StaleMultiplier = 3.0;
    public const int FailingStreak = 3;

    private readonly IHangfireSnapshotSource _source;
    private readonly JobExecutionTracker _tracker;
    private readonly TimeProvider _clock;

    public JobMonitoringService(IHangfireSnapshotSource source, JobExecutionTracker tracker, TimeProvider? clock = null)
    {
        _source = source;
        _tracker = tracker;
        _clock = clock ?? TimeProvider.System;
    }

    public JobMonitoringReport GetReport()
    {
        var now = _clock.GetUtcNow();
        var snapshot = _source.Capture();
        var jobs = MonitoredJobs.All.Select(def => Evaluate(def, snapshot, now)).ToList();

        var registered = jobs.Where(j => j.Status != nameof(JobHealthStatus.NotRegistered)).ToList();
        var overall = registered.Any(j => j.Core && j.Status == nameof(JobHealthStatus.Failing)) ? nameof(JobHealthStatus.Failing)
            : registered.Any(j => j.Status != nameof(JobHealthStatus.Healthy)) ? nameof(JobHealthStatus.Stale)
            : nameof(JobHealthStatus.Healthy);
        return new JobMonitoringReport(overall, now, jobs);
    }

    public JobStatusDto Evaluate(MonitoredJobDefinition def, HangfireSnapshot snapshot, DateTimeOffset now)
    {
        var info = snapshot.Recurring.FirstOrDefault(r => r.Id == def.RecurringJobId);
        var runs = _tracker.Runs(def.TrackerName);
        var interval = ParseCronInterval(info?.Cron) ?? def.DefaultInterval;

        var lastRun = runs.Count > 0 ? runs[^1] : null;
        var lastSuccess = runs.LastOrDefault(r => r.Succeeded);
        var lastFailure = runs.LastOrDefault(r => !r.Succeeded);

        DateTimeOffset? lastSuccessAt = lastSuccess is null ? null : lastSuccess.StartedAt + lastSuccess.Duration;
        // After a restart the tracker is empty; Hangfire's last state for the recurring job stands in.
        if (lastSuccessAt is null && info is { LastJobState: "Succeeded", LastExecution: { } le }) lastSuccessAt = le;
        DateTimeOffset? lastFailureAt = lastFailure is null ? null : lastFailure.StartedAt + lastFailure.Duration;
        var lastError = lastFailure?.Error ?? (info?.LastJobState == "Failed" ? info.Error : null);
        if (lastFailureAt is null && info is { LastJobState: "Failed", LastExecution: { } lf }) lastFailureAt = lf;

        var streak = 0;
        for (var i = runs.Count - 1; i >= 0 && !runs[i].Succeeded; i--) streak++;
        var failed24 = runs.Count(r => !r.Succeeded && r.StartedAt >= now.AddHours(-24));
        var failedInStorage = snapshot.Failed.Count(f => f.Method == def.MethodName);
        var retrying = snapshot.ScheduledByMethod.GetValueOrDefault(def.MethodName);

        var durations = runs.Select(r => r.Duration.TotalMilliseconds).ToList();

        string status, reason;
        if (info is null)
        {
            status = nameof(JobHealthStatus.NotRegistered);
            reason = "Not registered on this server (switched off in configuration).";
        }
        else
        {
            var staleAfter = TimeSpan.FromTicks((long)(interval.Ticks * StaleMultiplier));
            var latestIsFailure = lastRun is { Succeeded: false } || (lastRun is null && info.LastJobState == "Failed");
            if (latestIsFailure || streak >= FailingStreak)
            {
                status = nameof(JobHealthStatus.Failing);
                reason = streak >= FailingStreak ? $"{streak} runs failed in a row." : "The latest run failed.";
            }
            else
            {
                var reference = lastSuccessAt ?? info.CreatedAt;
                if (reference is { } r && now - r > staleAfter)
                {
                    status = nameof(JobHealthStatus.Stale);
                    reason = lastSuccessAt is null
                        ? $"Never succeeded; registered over {Fmt(staleAfter)} ago (expected every {Fmt(interval)})."
                        : $"No success for {Fmt(now - r)} (expected every {Fmt(interval)}; stale after {Fmt(staleAfter)}).";
                }
                else
                {
                    status = nameof(JobHealthStatus.Healthy);
                    reason = lastSuccessAt is null ? "Registered; waiting for the first run." : "Running on schedule.";
                }
            }
        }

        return new JobStatusDto(def.RecurringJobId, def.Label, def.Core, status, reason, info?.Cron, interval.TotalMinutes,
            lastSuccessAt, lastFailureAt, lastError,
            lastRun?.Duration.TotalMilliseconds, durations.Count > 0 ? durations.Average() : null, runs.Count, streak,
            failed24, retrying, snapshot.EnqueuedByMethod.GetValueOrDefault(def.MethodName), retrying,
            snapshot.ProcessingByMethod.GetValueOrDefault(def.MethodName), failedInStorage, info?.NextExecution);
    }

    private static string Fmt(TimeSpan t) => t.TotalMinutes < 120 ? $"{t.TotalMinutes:0.#} min" : $"{t.TotalHours:0.#} h";

    /// <summary>Interval of the simple cron forms this app uses ("* * * * *", "*/N * * * *", "M * * * *"); null when not recognised.</summary>
    public static TimeSpan? ParseCronInterval(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;
        var minute = parts[0];
        var restWild = parts.Skip(1).All(p => p == "*");
        if (!restWild) return null;
        if (minute == "*") return TimeSpan.FromMinutes(1);
        if (minute.StartsWith("*/") && int.TryParse(minute[2..], out var n) && n is > 0 and <= 60) return TimeSpan.FromMinutes(n);
        if (int.TryParse(minute, out _)) return TimeSpan.FromHours(1);
        return null;
    }
}
