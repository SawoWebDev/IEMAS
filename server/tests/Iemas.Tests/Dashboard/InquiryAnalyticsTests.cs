using Iemas.Application.Inquiries;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Dashboard;

public class InquiryAnalyticsTests
{
    private static readonly DateTimeOffset To = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = To.AddDays(-10);

    private static Case NewCase(Guid? owner, DateTimeOffset received, InquiryStage stage = InquiryStage.AwaitingInitialResponse,
        int? responseMinutes = null, int? resolveMinutes = null) => new()
    {
        CaseNumber = $"C-{Guid.NewGuid():N}"[..10],
        EmailAccountId = Guid.NewGuid(),
        CustomerEmailAddress = "c@example.com",
        Subject = "s", NormalizedSubject = "s",
        OwnerEmployeeId = owner,
        Stage = stage,
        FirstEmailReceivedAt = received,
        LastActivityAt = received,
        StageChangedAt = received,
        FirstAgentResponseAt = responseMinutes is null ? null : received.AddMinutes(responseMinutes.Value),
        CompletedAt = resolveMinutes is null ? null : received.AddMinutes(resolveMinutes.Value),
    };

    private static async Task<(TestDbContext Db, Employee A, Employee B, Department Dep)> SeedAsync()
    {
        var db = TestDbContext.CreateNew();
        var dep = new Department { Name = "Support" };
        db.Departments.Add(dep);
        await db.SaveChangesAsync();
        var a = new Employee { FullName = "Alice", Email = "a@sawo.com", DepartmentId = dep.Id };
        var b = new Employee { FullName = "Bob", Email = "b@sawo.com" };
        db.Employees.AddRange(a, b);
        await db.SaveChangesAsync();
        return (db, a, b, dep);
    }

    [Fact]
    public void Percentiles_AreInterpolated()
    {
        var stats = InquiryAnalyticsService.Stats(new double[] { 10, 20, 30, 40, 100 });
        Assert.Equal(5, stats.Count);
        Assert.Equal(40, stats.AverageMinutes);
        Assert.Equal(30, stats.MedianMinutes);
        Assert.Equal(76, stats.P90Minutes);
        Assert.Null(InquiryAnalyticsService.Stats(Array.Empty<double>()).AverageMinutes);
    }

    [Fact]
    public async Task CountsResponseTimesAndPerEmployee()
    {
        var (db, a, b, _) = await SeedAsync();
        using var _d = db;
        db.Cases.Add(NewCase(a.Id, To.AddDays(-2), InquiryStage.InitialResponseReceived, responseMinutes: 10));
        db.Cases.Add(NewCase(a.Id, To.AddDays(-2), InquiryStage.Resolved, responseMinutes: 30, resolveMinutes: 600));
        db.Cases.Add(NewCase(b.Id, To.AddDays(-1), InquiryStage.AwaitingInitialResponse));
        db.Cases.Add(NewCase(a.Id, To.AddDays(-30), InquiryStage.AwaitingInitialResponse)); // outside range
        await db.SaveChangesAsync();

        var r = await new InquiryAnalyticsService(db).GetAsync(new InquiryAnalyticsFilter(From, To, null, null, "day"), default);

        Assert.Equal(3, r.Total);
        Assert.Equal(2, r.Open);
        Assert.Equal(1, r.Resolved);
        Assert.Equal(2, r.FirstResponse.Count);
        Assert.Equal(20, r.FirstResponse.AverageMinutes);
        Assert.Equal(600, r.Resolution.AverageMinutes);
        var alice = r.Employees.Single(e => e.AgentName == "Alice");
        Assert.Equal(2, alice.Inquiries);
        Assert.Equal(2, alice.Responded);
        Assert.Equal(0, r.Employees.Single(e => e.AgentName == "Bob").Responded);
        Assert.Equal("wall-clock minutes", r.TimeBasis);
    }

    [Fact]
    public async Task Filters_ByEmployeeAndDepartment()
    {
        var (db, a, b, dep) = await SeedAsync();
        using var _d = db;
        db.Cases.Add(NewCase(a.Id, To.AddDays(-1)));
        db.Cases.Add(NewCase(b.Id, To.AddDays(-1)));
        db.Cases.Add(NewCase(b.Id, To.AddDays(-2)));
        await db.SaveChangesAsync();
        var svc = new InquiryAnalyticsService(db);

        Assert.Equal(2, (await svc.GetAsync(new(From, To, b.Id, null, null), default)).Total);
        Assert.Equal(1, (await svc.GetAsync(new(From, To, null, dep.Id, null), default)).Total);
    }

    [Fact]
    public async Task VolumeSeries_DayAndWeek()
    {
        var (db, a, _, _) = await SeedAsync();
        using var _d = db;
        db.Cases.Add(NewCase(a.Id, To.AddDays(-1)));
        db.Cases.Add(NewCase(a.Id, To.AddDays(-1)));
        db.Cases.Add(NewCase(a.Id, To.AddDays(-3), InquiryStage.Resolved, resolveMinutes: 60));
        await db.SaveChangesAsync();
        var svc = new InquiryAnalyticsService(db);

        var day = await svc.GetAsync(new(From, To, null, null, "day"), default);
        Assert.Equal(2, day.Volume.Single(v => v.BucketStart.Date == To.AddDays(-1).Date).Received);
        Assert.Equal(1, day.Volume.Sum(v => v.Resolved));
        Assert.Equal(3, day.Volume.Sum(v => v.Received));

        var week = await svc.GetAsync(new(From, To, null, null, "week"), default);
        Assert.Equal("week", week.Granularity);
        Assert.Equal(3, week.Volume.Sum(v => v.Received));
        Assert.All(week.Volume, v => Assert.Equal(DayOfWeek.Monday, v.BucketStart.DayOfWeek));
    }

    [Fact]
    public async Task EscalationsByLevel_Overdue_Reopens_AndAiAcceptance()
    {
        var (db, a, _, _) = await SeedAsync();
        using var _d = db;
        var overdue = NewCase(a.Id, To.AddDays(-3), InquiryStage.Inactive);
        overdue.OverdueSince = To.AddDays(-1);
        var confirmed = NewCase(a.Id, To.AddDays(-4), InquiryStage.Resolved, resolveMinutes: 100);
        var rejected = NewCase(a.Id, To.AddDays(-4), InquiryStage.Inactive);
        rejected.ReopenCount = 2;
        db.Cases.AddRange(overdue, confirmed, rejected);
        await db.SaveChangesAsync();

        void Stage(Case c, string detail) => db.CaseEvents.Add(new CaseEvent { CaseId = c.Id, EventType = CaseEventType.StageChanged, Detail = detail });
        Stage(confirmed, "Stage: Initial response sent → Potentially resolved. AI.");
        Stage(rejected, "Stage: Initial response sent → Potentially resolved. AI.");
        Stage(rejected, "Stage: Potentially resolved → Conversation inactive. The agent said the inquiry is not resolved yet; supervision continues.");
        db.EscalationEvents.Add(new EscalationEvent { CaseId = overdue.Id, Level = 1, Outcome = EscalationOutcome.Executed });
        db.EscalationEvents.Add(new EscalationEvent { CaseId = overdue.Id, Level = 2, Outcome = EscalationOutcome.Executed });
        db.EscalationEvents.Add(new EscalationEvent { CaseId = rejected.Id, Level = 1, Outcome = EscalationOutcome.Executed });
        db.EscalationEvents.Add(new EscalationEvent { CaseId = rejected.Id, Level = 3, Outcome = EscalationOutcome.Skipped });
        await db.SaveChangesAsync();

        var r = await new InquiryAnalyticsService(db).GetAsync(new(From, To, null, null, null), default);

        Assert.Equal(1, r.Overdue);
        Assert.Equal(3, r.EscalationsTotal);
        Assert.Equal(2, r.EscalatedCases);
        Assert.Equal(2, r.EscalationsByLevel.Single(l => l.Level == 1).Count);
        Assert.Equal(1, r.EscalationsByLevel.Single(l => l.Level == 2).Count);
        Assert.DoesNotContain(r.EscalationsByLevel, l => l.Level == 3);
        Assert.Equal(1, r.ReopenedCases);
        Assert.Equal(2, r.TotalReopens);
        Assert.Equal(1, r.ResolutionRejections);
        Assert.Equal(2, r.ResolutionAi.Suggested);
        Assert.Equal(1, r.ResolutionAi.ConfirmedCases);
        Assert.Equal(1, r.ResolutionAi.RejectedCases);
        Assert.Equal(0.5, r.ResolutionAi.AcceptanceRate);
    }

    [Fact]
    public async Task ClassificationAccuracy_UsesCorrectionsAndFalsePositives()
    {
        var (db, a, _, _) = await SeedAsync();
        using var _d = db;
        var account = new EmailAccount { DisplayName = "x", EmailAddress = "x@sawo.com", OwnerEmployeeId = a.Id };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        for (var i = 0; i < 4; i++)
        {
            var m = new EmailMessage { EmailAccountId = account.Id, FromAddress = "c@example.com", Subject = "s", ReceivedAt = To.AddDays(-1), ProviderMessageId = $"p{i}" };
            db.EmailMessages.Add(m);
            await db.SaveChangesAsync();
            db.EmailClassifications.Add(new Iemas.Domain.Ai.EmailClassification
            {
                EmailMessageId = m.Id, Decision = i == 3 ? ImportanceDecision.NotImportant : ImportanceDecision.Important,
                IsManuallyCorrected = i == 0, ClassifiedAt = To.AddDays(-1),
            });
        }
        var fp = NewCase(a.Id, To.AddDays(-1), InquiryStage.Closed);
        fp.CompletionReason = CaseCompletionReason.IncorrectClassification;
        db.Cases.Add(fp);
        await db.SaveChangesAsync();

        var r = await new InquiryAnalyticsService(db).GetAsync(new(From, To, null, null, null), default);

        Assert.Equal(4, r.Classification.ConfidentDecisions);
        Assert.Equal(1, r.Classification.ManuallyCorrected);
        Assert.Equal(1, r.Classification.FalsePositiveCases);
        Assert.Equal(0.5, r.Classification.Accuracy);
    }
}
