using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Escalations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

public sealed record InquiryAnalyticsFilter(DateTimeOffset? From, DateTimeOffset? To, Guid? EmployeeId, Guid? DepartmentId, string? Granularity);

public sealed record VolumePointDto(DateTimeOffset BucketStart, int Received, int Resolved);

/// <summary>Minutes are wall-clock (calendar) minutes, not business minutes.</summary>
public sealed record DurationStatsDto(int Count, double? AverageMinutes, double? MedianMinutes, double? P90Minutes);

public sealed record EmployeeResponseDto(Guid? EmployeeId, string AgentName, int Inquiries, int Responded, DurationStatsDto FirstResponse, DurationStatsDto Resolution, int Open, int Overdue);

public sealed record EscalationLevelCountDto(int Level, int Count);

/// <param name="ConfirmedCases">AI-suggested resolutions the agent confirmed (never rejected first).</param>
/// <param name="RejectedCases">AI-suggested resolutions the agent rejected at least once.</param>
/// <param name="AcceptanceRate">Confirmed / (confirmed + rejected); null until a human has decided one.</param>
public sealed record ResolutionAiAccuracyDto(int Suggested, int ConfirmedCases, int RejectedCases, int PendingCases, double? AcceptanceRate);

/// <param name="ConfidentDecisions">Classifications decided Important or NotImportant in the range.</param>
/// <param name="ManuallyCorrected">Of those, how many an administrator corrected afterwards.</param>
/// <param name="FalsePositiveCases">Cases created from an Important classification that were closed as "Incorrect classification".</param>
public sealed record ClassificationAccuracyDto(int ConfidentDecisions, int ManuallyCorrected, int ImportantCases, int FalsePositiveCases, double? Accuracy);

public sealed record InquiryAnalyticsDto(
    DateTimeOffset From, DateTimeOffset To, string Granularity, string TimeBasis,
    int Total, int Open, int Resolved, int Closed, int Overdue, int EscalatedCases,
    int EscalationsTotal, IReadOnlyList<EscalationLevelCountDto> EscalationsByLevel,
    DurationStatsDto FirstResponse, DurationStatsDto Resolution,
    int ReopenedCases, int TotalReopens, int ResolutionRejections,
    IReadOnlyList<VolumePointDto> Volume, IReadOnlyList<EmployeeResponseDto> Employees,
    ResolutionAiAccuracyDto ResolutionAi, ClassificationAccuracyDto Classification);

/// <summary>
/// Management analytics over inquiries, computed from existing tables only (Cases, CaseEvents,
/// EscalationEvents, EmailClassifications). The cohort is inquiries whose first email arrived in the
/// range, optionally narrowed to one agent or one department. All durations are wall-clock minutes.
/// </summary>
public class InquiryAnalyticsService
{
    private const string RejectionMarker = "not resolved yet";
    private const string SuggestionMarker = "→ Potentially resolved";

    private readonly IAppDbContext _db;

    public InquiryAnalyticsService(IAppDbContext db) => _db = db;

    public async Task<InquiryAnalyticsDto> GetAsync(InquiryAnalyticsFilter filter, CancellationToken ct)
    {
        var to = filter.To ?? DateTimeOffset.UtcNow;
        var from = filter.From ?? to.AddDays(-30);
        if (from > to) (from, to) = (to, from);
        var weekly = string.Equals(filter.Granularity, "week", StringComparison.OrdinalIgnoreCase);

        var q = _db.Cases.AsNoTracking().Where(c => !c.IsPractice && c.FirstEmailReceivedAt >= from && c.FirstEmailReceivedAt <= to);
        if (filter.EmployeeId is Guid emp) q = q.Where(c => c.OwnerEmployeeId == emp);
        if (filter.DepartmentId is Guid dep) q = q.Where(c => c.OwnerEmployee != null && c.OwnerEmployee.DepartmentId == dep);

        var cases = await q.Select(c => new CaseRow(
            c.Id, c.OwnerEmployeeId, c.OwnerEmployee != null ? c.OwnerEmployee.FullName : null,
            c.Stage, c.FirstEmailReceivedAt, c.FirstAgentResponseAt, c.CompletedAt, c.StageChangedAt,
            c.OverdueSince != null, c.ReopenCount, c.CompletionReason)).ToListAsync(ct);
        var ids = cases.Select(c => c.Id).ToList();

        // Case history events (rejections and AI suggestions are recorded as stage changes).
        var stageEvents = ids.Count == 0 ? new List<(Guid CaseId, string Detail)>()
            : (await _db.CaseEvents.AsNoTracking()
                .Where(e => e.EventType == CaseEventType.StageChanged && ids.Contains(e.CaseId))
                .Select(e => new { e.CaseId, e.Detail }).ToListAsync(ct)).Select(e => (e.CaseId, e.Detail)).ToList();
        var rejectedIds = stageEvents.Where(e => e.Detail.Contains(RejectionMarker, StringComparison.OrdinalIgnoreCase)).Select(e => e.CaseId).ToHashSet();
        var suggestedIds = stageEvents.Where(e => e.Detail.Contains(SuggestionMarker, StringComparison.OrdinalIgnoreCase)).Select(e => e.CaseId).ToHashSet();
        var rejectionEvents = stageEvents.Count(e => e.Detail.Contains(RejectionMarker, StringComparison.OrdinalIgnoreCase));

        // Escalations actually executed for those inquiries.
        var escalations = ids.Count == 0 ? new List<(Guid CaseId, int Level)>()
            : (await _db.EscalationEvents.AsNoTracking()
                .Where(e => e.Outcome == EscalationOutcome.Executed && ids.Contains(e.CaseId))
                .Select(e => new { e.CaseId, e.Level }).ToListAsync(ct)).Select(e => (e.CaseId, e.Level)).ToList();
        var byLevel = escalations.GroupBy(e => e.Level).OrderBy(g => g.Key).Select(g => new EscalationLevelCountDto(g.Key, g.Count())).ToList();

        var open = cases.Where(c => !InquiryLifecycle.IsTerminal(c.Stage)).ToList();

        var volume = BuildVolume(cases, from, to, weekly);
        var employees = cases.GroupBy(c => new { c.OwnerId, c.OwnerName })
            .Select(g => new EmployeeResponseDto(g.Key.OwnerId, g.Key.OwnerName ?? "(unassigned)", g.Count(), g.Count(c => c.FirstResponse != null),
                Stats(FirstResponseMinutes(g)), Stats(ResolutionMinutes(g)),
                g.Count(c => !InquiryLifecycle.IsTerminal(c.Stage)), g.Count(c => !InquiryLifecycle.IsTerminal(c.Stage) && c.Overdue)))
            .OrderByDescending(e => e.Inquiries).ThenBy(e => e.AgentName).ToList();

        var confirmed = suggestedIds.Count(id => !rejectedIds.Contains(id) && cases.First(c => c.Id == id).Stage == InquiryStage.Resolved);
        var rejected = suggestedIds.Count(rejectedIds.Contains);
        var pending = suggestedIds.Count(id => !rejectedIds.Contains(id) && cases.First(c => c.Id == id).Stage == InquiryStage.PotentiallyResolved);
        var ai = new ResolutionAiAccuracyDto(suggestedIds.Count, confirmed, rejected, pending,
            confirmed + rejected > 0 ? Math.Round(confirmed / (double)(confirmed + rejected), 3) : null);

        return new InquiryAnalyticsDto(from, to, weekly ? "week" : "day", "wall-clock minutes",
            cases.Count, open.Count, cases.Count(c => c.Stage == InquiryStage.Resolved), cases.Count(c => c.Stage == InquiryStage.Closed),
            open.Count(c => c.Overdue), escalations.Select(e => e.CaseId).Distinct().Count(),
            escalations.Count, byLevel,
            Stats(FirstResponseMinutes(cases)), Stats(ResolutionMinutes(cases)),
            cases.Count(c => c.ReopenCount > 0), cases.Sum(c => c.ReopenCount), rejectionEvents,
            volume, employees, ai, await ClassificationAsync(filter, from, to, cases, ct));
    }

    private async Task<ClassificationAccuracyDto> ClassificationAsync(InquiryAnalyticsFilter filter, DateTimeOffset from, DateTimeOffset to, List<CaseRow> cases, CancellationToken ct)
    {
        var cq = _db.EmailClassifications.AsNoTracking()
            .Where(c => c.ClassifiedAt >= from && c.ClassifiedAt <= to
                && (c.Decision == ImportanceDecision.Important || c.Decision == ImportanceDecision.NotImportant));
        if (filter.EmployeeId is Guid emp)
            cq = cq.Where(c => _db.EmailAccounts.Any(a => a.Id == c.EmailMessage.EmailAccountId && a.OwnerEmployeeId == emp));
        if (filter.DepartmentId is Guid dep)
            cq = cq.Where(c => _db.EmailAccounts.Any(a => a.Id == c.EmailMessage.EmailAccountId && a.OwnerEmployee != null && a.OwnerEmployee.DepartmentId == dep));

        var rows = await cq.Select(c => new { c.IsManuallyCorrected, c.Decision }).ToListAsync(ct);
        var corrected = rows.Count(r => r.IsManuallyCorrected);
        var falsePositives = cases.Count(c => c.CompletionReason == CaseCompletionReason.IncorrectClassification);
        // Accuracy = decisions nobody had to correct, with false-positive cases counted as errors too.
        var errors = corrected + falsePositives;
        double? accuracy = rows.Count > 0 ? Math.Round(Math.Max(0, 1 - errors / (double)rows.Count), 3) : null;
        return new ClassificationAccuracyDto(rows.Count, corrected, rows.Count(r => r.Decision == ImportanceDecision.Important), falsePositives, accuracy);
    }

    private sealed record CaseRow(
        Guid Id, Guid? OwnerId, string? OwnerName, InquiryStage Stage, DateTimeOffset Received, DateTimeOffset? FirstResponse,
        DateTimeOffset? CompletedAt, DateTimeOffset StageChangedAt, bool Overdue, int ReopenCount, CaseCompletionReason? CompletionReason);

    private static IEnumerable<double> FirstResponseMinutes(IEnumerable<CaseRow> rows) =>
        rows.Where(c => c.FirstResponse != null).Select(c => Math.Max(0, (c.FirstResponse!.Value - c.Received).TotalMinutes));

    private static IEnumerable<double> ResolutionMinutes(IEnumerable<CaseRow> rows) =>
        rows.Where(c => c.Stage == InquiryStage.Resolved)
            .Select(c => Math.Max(0, ((c.CompletedAt ?? c.StageChangedAt) - c.Received).TotalMinutes));

    public static DurationStatsDto Stats(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return new DurationStatsDto(0, null, null, null);
        return new DurationStatsDto(v.Count, Math.Round(v.Average(), 1), Math.Round(Percentile(v, 0.5), 1), Math.Round(Percentile(v, 0.9), 1));
    }

    /// <summary>Linear-interpolated percentile of an ascending list.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static List<VolumePointDto> BuildVolume(List<CaseRow> cases, DateTimeOffset from, DateTimeOffset to, bool weekly)
    {
        DateTimeOffset Bucket(DateTimeOffset t)
        {
            var d = new DateTimeOffset(t.UtcDateTime.Date, TimeSpan.Zero);
            if (!weekly) return d;
            var back = ((int)d.DayOfWeek + 6) % 7; // Monday start
            return d.AddDays(-back);
        }

        var received = cases.GroupBy(c => Bucket(c.Received)).ToDictionary(g => g.Key, g => g.Count());
        var resolved = cases.Where(c => c.Stage == InquiryStage.Resolved && c.CompletedAt != null)
            .GroupBy(c => Bucket(c.CompletedAt!.Value)).ToDictionary(g => g.Key, g => g.Count());

        var points = new List<VolumePointDto>();
        var step = weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        for (var b = Bucket(from); b <= to && points.Count < 400; b += step)
            points.Add(new VolumePointDto(b, received.GetValueOrDefault(b), resolved.GetValueOrDefault(b)));
        return points;
    }
}
