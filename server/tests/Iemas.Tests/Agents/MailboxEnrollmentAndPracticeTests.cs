using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailAccounts;
using Iemas.Application.EmailAccounts.Dtos;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Domain.Operations;
using Iemas.Tests.Inquiries;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Agents;

/// <summary>An employee connects their PC with only their email address and password.</summary>
public class MailboxEnrollmentTests
{
    private readonly TestDbContext _db = TestDbContext.CreateNew();
    private readonly List<EmailProviderConnectionSettings> _logins = new();
    private readonly CapturingEscalationEmailSender _mail = new();
    private readonly NoOpAuditService _audit = new();
    private bool _passwordAccepted = true;

    private MailboxEnrollmentService Service(EnrollmentThrottle? throttle = null)
    {
        var adapter = new FakeEmailProviderAdapter
        {
            TestConnectionBehavior = (settings, _) =>
            {
                _logins.Add(settings);
                return Task.FromResult(_passwordAccepted
                    ? new ProviderConnectionTestResult(true, null, TimeSpan.Zero)
                    : new ProviderConnectionTestResult(false, "bad login", TimeSpan.Zero, AuthenticationFailed: true));
            },
        };
        var encryption = new PassThroughEncryptionService();
        var registration = new AgentRegistrationService(_db, new FakeAgentTokenService(), encryption, _audit);
        return new MailboxEnrollmentService(_db, encryption, new FakeEmailProviderAdapterResolver(adapter), registration,
            throttle ?? new EnrollmentThrottle(), _mail, _audit);
    }

    private static EnrollMailboxRequest Request(string email, string password = "secret") =>
        new(email, password, "MARIA-PC", "https://server:8443", "1.3.0", "PC");

    [Fact]
    public async Task EmailAndPassword_AddTheMailboxWithTheCompanyDefaults_TheEmployee_AndAnApprovedPc()
    {
        var result = await Service().EnrollAsync(Request("Maria.Santos@sawo.com"), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var login = Assert.Single(_logins);
        Assert.Equal(("mail.sawo.com", 993, "SSL/TLS", "maria.santos@sawo.com"), (login.Host, login.Port, login.Encryption, login.Username));
        var account = await _db.EmailAccounts.Include(a => a.Credential).Include(a => a.OwnerEmployee).SingleAsync();
        Assert.Equal("maria.santos@sawo.com", account.EmailAddress);
        Assert.True(account.MonitoringEnabled && account.IsActive && account.Credential is not null);
        Assert.NotNull(account.ProcessEmailsReceivedAfter);   // old email never becomes work
        Assert.Equal("Maria Santos", account.OwnerEmployee!.FullName);
        var agent = await _db.Agents.SingleAsync();
        Assert.Equal(AgentRegistrationStatus.Approved, agent.RegistrationStatus);
        Assert.Equal(account.OwnerEmployeeId, agent.EmployeeId);
        Assert.Equal("maria.santos@sawo.com", Assert.Single(_mail.Sent).To.Single().Address);
        Assert.DoesNotContain(_audit.Entries, e => (e.Details ?? "").Contains("secret"));
    }

    [Fact]
    public async Task Testing_ChangesNothing()
    {
        var result = await Service().TestAsync(new MailboxLoginRequest("maria.santos@sawo.com", "secret"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(await _db.EmailAccounts.ToListAsync());
        Assert.Empty(await _db.Agents.ToListAsync());
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task AWrongPassword_LeavesNoTrace_AndRepeatedGuessesAreStopped()
    {
        _passwordAccepted = false;
        var throttle = new EnrollmentThrottle();
        for (var i = 0; i < EnrollmentThrottle.MaxFailures; i++)
        {
            Assert.False((await Service(throttle).EnrollAsync(Request("maria.santos@sawo.com", "guess"), null, CancellationToken.None)).Succeeded);
        }

        _passwordAccepted = true;
        var blocked = await Service(throttle).EnrollAsync(Request("maria.santos@sawo.com"), null, CancellationToken.None);

        Assert.False(blocked.Succeeded);
        Assert.Contains("Wait 15 minutes", blocked.Error);
        Assert.Equal(EnrollmentThrottle.MaxFailures, _logins.Count);
        Assert.Empty(await _db.Employees.ToListAsync());
        Assert.Empty(await _db.Agents.ToListAsync());
    }

    [Theory]
    [InlineData("someone@gmail.com")]
    [InlineData("someone@sawo.com.attacker.example")]
    [InlineData("not-an-address")]
    public async Task OnlyCompanyAddresses_AreEverSentToAMailServer(string email)
    {
        Assert.False((await Service().EnrollAsync(Request(email), null, CancellationToken.None)).Succeeded);
        Assert.Empty(_logins);
    }

    [Fact]
    public async Task CanBeSwitchedOff()
    {
        _db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.AllowSelfEnrollment, Value = "false" });
        await _db.SaveChangesAsync();

        Assert.False((await Service().EnrollAsync(Request("maria.santos@sawo.com"), null, CancellationToken.None)).Succeeded);
        Assert.Empty(_logins);
    }

    [Fact]
    public async Task ASharedMailboxPassword_DoesNotOpenTheOwnersOtherMail_WithoutApproval()
    {
        var boss = new Employee { FullName = "Bea Boss", Email = "bea.boss@sawo.com" };
        _db.Employees.Add(boss);
        foreach (var address in new[] { "bea.boss@sawo.com", "info@sawo.com" })
        {
            _db.EmailAccounts.Add(new EmailAccount
            {
                EmailAddress = address, Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "mail.sawo.com", Port = 993,
                Username = address, OwnerEmployeeId = boss.Id,
            });
        }
        await _db.SaveChangesAsync();

        var result = await Service().EnrollAsync(Request("info@sawo.com"), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(AgentRegistrationStatus.Pending, result.Value!.Status);
        Assert.Null((await _db.Agents.SingleAsync()).EmployeeId);
    }

    [Fact]
    public async Task ADeactivatedMailbox_StaysDeactivated()
    {
        _db.EmailAccounts.Add(new EmailAccount
        {
            EmailAddress = "gone@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "mail.sawo.com", Port = 993,
            IsActive = false,
        });
        await _db.SaveChangesAsync();

        Assert.False((await Service().EnrollAsync(Request("gone@sawo.com"), null, CancellationToken.None)).Succeeded);
        Assert.Empty(_logins);
    }

    [Fact]
    public async Task InTheCms_AMailboxNeedsOnlyItsAddressAndPassword()
    {
        var accounts = new EmailAccountService(_db, new PassThroughEncryptionService(),
            new FakeEmailProviderAdapterResolver(new FakeEmailProviderAdapter()), _audit);

        var inbound = await accounts.CreateAsync(new CreateEmailAccountRequest("anna.reyes@sawo.com", null, EmailAccountPurpose.Inbound,
            EmailAccountKind.Primary, EmailProtocol.Imap, null, 0, null, null, EmailAuthMethod.Password, "pw", null, null, true, null, AutoAssignOwner: true),
            CancellationToken.None);
        var outbound = await accounts.CreateAsync(new CreateEmailAccountRequest("icare@sawo.com", "IEMAS", EmailAccountPurpose.Outbound,
            EmailAccountKind.Primary, EmailProtocol.Smtp, null, 0, null, null, EmailAuthMethod.Password, "pw", null, null, false),
            CancellationToken.None);
        var elsewhere = await accounts.CreateAsync(new CreateEmailAccountRequest("a@gmail.com", null, EmailAccountPurpose.Inbound,
            EmailAccountKind.Primary, EmailProtocol.Imap, null, 0, null, null, EmailAuthMethod.Password, "pw", null, null, true), CancellationToken.None);

        Assert.True(inbound.Succeeded, inbound.Error);
        Assert.Equal(("mail.sawo.com", 993, "anna.reyes@sawo.com", "Anna Reyes"),
            (inbound.Value!.Host, inbound.Value.Port, inbound.Value.Username, inbound.Value.OwnerEmployeeName));
        Assert.Equal(("mail.sawo.com", 465), (outbound.Value!.Host, outbound.Value.Port));
        Assert.False(elsewhere.Succeeded);
    }

    [Fact]
    public async Task AStoredPassword_IsNeverSentToADifferentServer()
    {
        var logins = new List<EmailProviderConnectionSettings>();
        var adapter = new FakeEmailProviderAdapter { TestConnectionBehavior = (s, _) => { logins.Add(s); return Task.FromResult(new ProviderConnectionTestResult(true, null, TimeSpan.Zero)); } };
        var encryption = new PassThroughEncryptionService();
        var accounts = new EmailAccountService(_db, encryption, new FakeEmailProviderAdapterResolver(adapter), _audit);
        var saved = await accounts.CreateAsync(new CreateEmailAccountRequest("anna@sawo.com", null, EmailAccountPurpose.Inbound,
            EmailAccountKind.Primary, EmailProtocol.Imap, null, 0, null, null, EmailAuthMethod.Password, "pw", null, null, true), CancellationToken.None);

        var same = await accounts.TestSettingsAsync(new TestMailboxSettingsRequest("anna@sawo.com", EmailAccountPurpose.Inbound,
            "mail.sawo.com", 993, "SSL/TLS", null, null, saved.Value!.Id), CancellationToken.None);
        var moved = await accounts.TestSettingsAsync(new TestMailboxSettingsRequest("anna@sawo.com", EmailAccountPurpose.Inbound,
            "evil.example", 993, "SSL/TLS", null, null, saved.Value.Id), CancellationToken.None);

        Assert.True(same.Succeeded, same.Error);
        Assert.False(moved.Succeeded);
        Assert.Equal("mail.sawo.com", Assert.Single(logins).Host);
    }
}

/// <summary>The first-time walkthrough's practice inquiry.</summary>
public class PracticeInquiryTests
{
    private const string Mailbox = "agent1@sawo.com";

    private static PracticeInquiryService Practice(InquiryHarness h) =>
        new(h.Db, new NotificationService(h.Db, h.PopUps), h.ManagementEmail, h.PopUps);

    [Fact]
    public async Task WalksFromNewInquiryToEscalation_TellingOnlyTheEmployee()
    {
        using var h = new InquiryHarness(Mailbox);
        var me = h.Agents[Mailbox].Id;
        var practice = Practice(h);

        var start = await practice.StartAsync(me, CancellationToken.None);
        var one = await practice.AdvanceAsync(me, start.Value!.CaseId, CancellationToken.None);
        var two = await practice.AdvanceAsync(me, start.Value.CaseId, CancellationToken.None);
        var three = await practice.AdvanceAsync(me, start.Value.CaseId, CancellationToken.None);
        var after = await practice.AdvanceAsync(me, start.Value.CaseId, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2, 3, 3 }, new[] { start.Value.Step, one.Value!.Step, two.Value!.Step, three.Value!.Step, after.Value!.Step });
        Assert.True(three.Value.Finished);
        Assert.Equal(new[] { "New customer inquiry", "Friendly reminder", "Last reminder (escalation warning)", "Escalated to your supervisor" },
            h.PopUpTitlesFor(Mailbox));
        Assert.Contains("practice", h.PopUps.Sent[0].Command.Message);
        // The sample inquiry and the sample supervisor email both go to the employee, nobody else.
        Assert.Equal(2, h.ManagementEmail.Sent.Count);
        Assert.All(h.ManagementEmail.Sent, e => Assert.Equal(Mailbox, e.To.Single().Address));
        Assert.All(h.ManagementEmail.Sent, e => Assert.StartsWith("[PRACTICE]", e.Subject));
        Assert.Equal(CaseWorkStatus.Escalated, (await h.Db.Cases.SingleAsync()).WorkStatus);
        Assert.Empty(await h.Db.EscalationEvents.ToListAsync());
    }

    [Fact]
    public async Task IsLeftAloneByTheTimersAndReports_AndDisappearsWhenEnded()
    {
        using var h = new InquiryHarness(Mailbox);
        var me = h.Agents[Mailbox].Id;
        var start = await Practice(h).StartAsync(me, CancellationToken.None);

        await h.Supervision.RunAsync(100, CancellationToken.None, DateTimeOffset.UtcNow.AddMinutes(30));
        var overview = await new Application.Dashboard.DashboardService(h.Db).GetSummaryAsync(CancellationToken.None);

        Assert.Empty(await h.Db.Reminders.ToListAsync());
        Assert.Equal(0, overview.OpenCases);

        await Practice(h).EndAsync(me, start.Value!.CaseId, CancellationToken.None);

        Assert.Empty(await h.Db.Cases.ToListAsync());
        Assert.Empty(await h.Db.EmailMessages.ToListAsync());
        Assert.Empty(await h.Db.CaseEvents.ToListAsync());
        Assert.Empty(await h.Db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task AForgottenPractice_IsRemovedAfterAnHour()
    {
        using var h = new InquiryHarness(Mailbox);
        await Practice(h).StartAsync(h.Agents[Mailbox].Id, CancellationToken.None);

        await h.Supervision.RunAsync(100, CancellationToken.None, DateTimeOffset.UtcNow.AddHours(2));

        Assert.Empty(await h.Db.Cases.ToListAsync());
    }

    [Fact]
    public async Task NeverTakesACaseNumber_AndAnotherPersonCannotTouchIt()
    {
        using var h = new InquiryHarness(Mailbox, "agent2@sawo.com");
        var start = await Practice(h).StartAsync(h.Agents[Mailbox].Id, CancellationToken.None);

        var intruder = await Practice(h).AdvanceAsync(h.Agents["agent2@sawo.com"].Id, start.Value!.CaseId, CancellationToken.None);
        h.Receive(Mailbox, "anna@customer.example", "Heater price?", "What is the price of the heater?", InquiryHarness.Monday0900);
        await h.RunAsync(InquiryHarness.Monday0900);

        Assert.False(intruder.Succeeded);
        Assert.Equal("CASE-000001", (await h.CaseAsync(Mailbox, "anna@customer.example")).CaseNumber);
    }
}

/// <summary>"Keep only this mailbox and this outbound account."</summary>
public class CleanStartTests
{
    [Fact]
    public async Task KeepsTheChosenAccounts_TheirPeople_AndPcs_AndRemovesTheRest()
    {
        var db = TestDbContext.CreateNew();
        var boss = new Employee { FullName = "Big Boss", Email = "boss@sawo.com" };
        var maria = new Employee { FullName = "Maria Santos", Email = "maria.santos@sawo.com", SupervisorEmployeeId = boss.Id };
        var other = new Employee { FullName = "Old Tester", Email = "tester@sawo.com" };
        db.Employees.AddRange(boss, maria, other);
        EmailAccount Account(string address, EmailAccountPurpose purpose, Guid? owner) => new()
        {
            EmailAddress = address, Purpose = purpose, Host = "mail.sawo.com", Port = purpose == EmailAccountPurpose.Inbound ? 993 : 465, OwnerEmployeeId = owner,
        };
        var keep = Account("maria.santos@sawo.com", EmailAccountPurpose.Inbound, maria.Id);
        var outbound = Account("icare@sawo.com", EmailAccountPurpose.Outbound, null);
        var drop = Account("tester@sawo.com", EmailAccountPurpose.Inbound, other.Id);
        db.EmailAccounts.AddRange(keep, outbound, drop);
        db.Agents.AddRange(
            new Agent { ClientName = "Maria PC", EnrollmentEmailAddress = keep.EmailAddress, EmployeeId = maria.Id },
            new Agent { ClientName = "Old PC", EnrollmentEmailAddress = drop.EmailAddress, EmployeeId = other.Id });
        db.EmailMessages.Add(new EmailMessage { EmailAccountId = drop.Id, ProviderMessageId = "1", Subject = "old" });
        db.Cases.Add(new Case { CaseNumber = "CASE-000001", EmailAccountId = drop.Id, Subject = "old", OwnerEmployeeId = other.Id });
        await db.SaveChangesAsync();
        var service = new EmailDataResetService(db, new NoOpAuditService(), new FakeCurrentUserService());

        var plan = await service.PlanKeepOnlyAsync(new[] { keep.Id, outbound.Id }, CancellationToken.None);
        var result = await service.ResetAsync("RESET", null, CancellationToken.None, new[] { keep.Id, outbound.Id });

        Assert.Equal(new[] { "Old PC", "Old Tester", "tester@sawo.com" }, plan.Remove.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.NotNull(result);
        Assert.Equal(new[] { "icare@sawo.com", "maria.santos@sawo.com" }, (await db.EmailAccounts.Select(a => a.EmailAddress).ToListAsync()).OrderBy(a => a));
        Assert.Equal(new[] { "Big Boss", "Maria Santos" }, (await db.Employees.Select(e => e.FullName).ToListAsync()).OrderBy(n => n));
        Assert.Equal("Maria PC", (await db.Agents.SingleAsync()).ClientName);
        Assert.Empty(await db.Cases.ToListAsync());
        Assert.Empty(await db.EmailMessages.ToListAsync());
    }

    [Fact]
    public async Task KeepingNothing_IsNeverUnderstoodAsDeleteEveryMailbox()
    {
        var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(new EmailAccount { EmailAddress = "a@sawo.com", Host = "h", Port = 993 });
        await db.SaveChangesAsync();
        var service = new EmailDataResetService(db, new NoOpAuditService(), new FakeCurrentUserService());

        var plan = await service.PlanKeepOnlyAsync(new[] { Guid.NewGuid() }, CancellationToken.None);
        await service.ResetAsync("RESET", null, CancellationToken.None, new[] { Guid.NewGuid() });

        Assert.Empty(plan.Remove);
        Assert.Single(await db.EmailAccounts.ToListAsync());
    }
}
