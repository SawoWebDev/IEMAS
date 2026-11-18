using Hangfire;
using Iemas.Api.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iemas.Api.HealthChecks;

// Observability (Phase 10 hardening): checks Hangfire storage connectivity and server/worker
// presence via IMonitoringApi — a lightweight read against Hangfire's own PostgreSQL-backed
// storage tables, not a real job dispatch. Never enqueues a job merely to prove the system works;
// that would itself be production work triggered by a health poll.
public sealed class HangfireHealthCheck : IHealthCheck
{
    private readonly JobMonitoringService? _jobMonitoring;

    public HangfireHealthCheck(JobMonitoringService? jobMonitoring = null)
    {
        _jobMonitoring = jobMonitoring;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var monitoringApi = JobStorage.Current.GetMonitoringApi();
            var servers = monitoringApi.Servers();

            var data = new Dictionary<string, object>
            {
                ["serverCount"] = servers.Count,
                ["totalWorkerCount"] = servers.Sum(s => s.WorkersCount),
                ["enqueuedCount"] = monitoringApi.EnqueuedCount("default"),
                ["failedCount"] = monitoringApi.FailedCount(),
            };

            if (servers.Count == 0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Hangfire storage is reachable, but no server is currently registered — recurring jobs will not run.",
                    data: data));
            }

            if (servers.All(s => s.WorkersCount == 0))
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    "Hangfire server(s) registered, but none report any available workers.",
                    data: data));
            }

            // Core pipeline jobs that stopped succeeding make the system Degraded (or Unhealthy when most of
            // them are failing) even though the Hangfire server itself is up.
            if (_jobMonitoring is not null)
            {
                var report = _jobMonitoring.GetReport();
                var core = report.Jobs.Where(j => j.Core && j.Status != nameof(JobHealthStatus.NotRegistered)).ToList();
                var failing = core.Where(j => j.Status == nameof(JobHealthStatus.Failing)).ToList();
                var stale = core.Where(j => j.Status == nameof(JobHealthStatus.Stale)).ToList();
                data["failingCoreJobs"] = string.Join(", ", failing.Select(j => j.Id));
                data["staleCoreJobs"] = string.Join(", ", stale.Select(j => j.Id));
                if (core.Count > 0 && failing.Count * 2 >= core.Count)
                {
                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        $"{failing.Count} of {core.Count} core background jobs are failing: {string.Join(", ", failing.Select(j => j.Label))}.", data: data));
                }
                if (failing.Count > 0 || stale.Count > 0)
                {
                    return Task.FromResult(HealthCheckResult.Degraded(
                        $"Core background jobs need attention: {string.Join(", ", failing.Concat(stale).Select(j => $"{j.Label} ({j.Status})"))}.", data: data));
                }
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"{servers.Count} Hangfire server(s) registered with {data["totalWorkerCount"]} total worker(s).",
                data));
        }
        catch (Exception ex)
        {
            // Storage (PostgreSQL) unreachable, or Hangfire not yet initialized — either way, a
            // genuine failure to report, not a value to guess at.
            return Task.FromResult(HealthCheckResult.Unhealthy("Failed to query Hangfire storage.", ex));
        }
    }
}
