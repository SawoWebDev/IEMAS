using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailAccounts;
using Iemas.Application.Employees;
using Iemas.Domain.Agents;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Infrastructure.Providers;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Agents;

/// <summary>One-step PC setup: email + password with a company installer key, everything else found or created.</summary>
public class AgentSelfEnrollmentServiceTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    private sealed class NoAutoconfig : IMailAutoconfigLookup
    {
        public Task<IReadOnlyList<MailServerCandidate>> LookupAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailServerCandidate>>(Array.Empty<MailServerCandidate>());
    }

    private sealed record Harness(TestDbContext Db, AgentSelfEnrollmentService Service, FakeEmailProviderAdapter Adapter, NoOpAuditService Audit, EnrollmentAttemptLimiter Limiter, string CompanyKey);

    private static async Task<Harness> CreateAsync(Func<EmailProviderConnectionSettings, ProviderConnectionTestResult>? login = null, bool selfEnrollment = true)
    {
        var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var adapter = new FakeEmailProviderAdapter
        {
            TestConnectionBehavior = (s, _) => Task.FromResult(login?.Invoke(s) ?? new ProviderConnectionTestResult(true, null, TimeSpan.Zero)),
        };
        var resolver = new FakeEmailProviderAdapterResolver(adapter);
        var encryption = new PassThroughEncryptionService();
        var registration = new AgentRegistrationService(db, new FakeAgentTokenService(), encryption, audit);
        var limiter = new EnrollmentAttemptLimiter();
        var service = new AgentSelfEnrollmentService(db, new MailServerDiscovery(db, resolver, new NoAutoconfig()), resolver, encryption, audit,
            registration, new EmployeeService(db, audit), new EmailAccountService(db, encryption, resolver, audit), limiter);

        var user = new User { Email = "admin@iemas.local", PasswordHash = "x", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var created = (await new AgentInstallKeyService(db, audit).CreateAsync(
            new CreateInstallPackageRequest(null, "https://172.16.2.68:8443", true, 365, 1000, selfEnrollment), user.Id, CancellationToken.None)).Value!;
        return new Harness(db, service, adapter, audit, limiter, created.RawKey);
    }

    private static SelfEnrollAgentRequest Request(Harness h, string email = "maria.santos@sawo.com", string password = "pw1", string? key = null, string pc = "SALES-PC-07") =>
        new() { InstallKey = key ?? h.CompanyKey, EmailAddress = email, Password = password, ClientName = pc, ServerAddress = "https://172.16.2.68:8443", AgentVersion = "1.2.0" };

    [Fact]
    public async Task BrandNewAddress_CreatesEmployeeAndMailbox_AndApprovesThePc()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        h.Db.EmailAccounts.Add(new EmailAccount
        {
            EmailAddress = "other@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "mail.sawo.com", Port = 993,
            Encryption = "SSL/TLS", Username = "other@sawo.com", AuthMethod = EmailAuthMethod.Password, IsActive = true, ClassificationProfileName = "Sales",
        });
        await h.Db.SaveChangesAsync();

        var result = await h.Service.EnrollAsync(Request(h), "10.0.0.5", CancellationToken.None);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.True(result.Response!.EmployeeCreated);
        Assert.True(result.Response.MailboxCreated);
        Assert.Equal(AgentRegistrationStatus.Approved, result.Response.Status);
        Assert.Equal("Maria Santos", result.Response.EmployeeName);

        var account = await h.Db.EmailAccounts.SingleAsync(a => a.EmailAddress == "maria.santos@sawo.com");
        Assert.Equal("mail.sawo.com", account.Host);       // taken from the existing mailbox on the same domain
        Assert.Equal("Sales", account.ClassificationProfileName);
        Assert.True(account.MonitoringEnabled);
        Assert.NotNull(account.ProcessEmailsReceivedAfter);
        Assert.Equal((await h.Db.Employees.SingleAsync(e => e.Email == "maria.santos@sawo.com")).Id, account.OwnerEmployeeId);
        Assert.Equal(AgentRegistrationStatus.Approved, (await h.Db.Agents.SingleAsync()).RegistrationStatus);
        Assert.Contains(h.Audit.Entries, e => e.Action == "AGENT_SELF_ENROLLED");
    }

    [Fact]
    public async Task ExistingMailboxWithOwner_ApprovesForThatOwner_WithoutCreatingAnything()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        var owner = new Employee { FullName = "Maria Santos", Email = "maria.santos@sawo.com", IsActive = true };
        h.Db.Employees.Add(owner);
        h.Db.EmailAccounts.Add(new EmailAccount
        {
            EmailAddress = "maria.santos@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "mail.sawo.com", Port = 993,
            Username = "maria.santos@sawo.com", AuthMethod = EmailAuthMethod.Password, IsActive = true, OwnerEmployeeId = owner.Id,
        });
        await h.Db.SaveChangesAsync();

        var result = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.False(result.Response!.EmployeeCreated);
        Assert.False(result.Response.MailboxCreated);
        Assert.Equal(1, await h.Db.Employees.CountAsync());
        Assert.Equal(owner.Id, (await h.Db.Agents.SingleAsync()).EmployeeId);
    }

    [Fact]
    public async Task OwnMailboxAddedWithMonitoringOff_IsSwitchedOn_ByThePcSetup()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        var owner = new Employee { FullName = "Maria Santos", Email = "maria.santos@sawo.com", IsActive = true };
        h.Db.Employees.Add(owner);
        h.Db.EmailAccounts.Add(new EmailAccount
        {
            EmailAddress = "maria.santos@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "mail.sawo.com", Port = 993,
            Username = "maria.santos@sawo.com", AuthMethod = EmailAuthMethod.Password, IsActive = true, OwnerEmployeeId = owner.Id, MonitoringEnabled = false,
        });
        await h.Db.SaveChangesAsync();
        var before = DateTimeOffset.UtcNow;

        var result = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Failure?.Message);
        var account = await h.Db.EmailAccounts.SingleAsync(a => a.EmailAddress == "maria.santos@sawo.com");
        Assert.True(account.MonitoringEnabled);
        Assert.True(account.ProcessEmailsReceivedAfter >= before);
        Assert.Contains(h.Audit.Entries, e => e.Action == "EMAIL_ACCOUNT_MONITORING_ENABLED");
    }

    [Fact]
    public async Task WrongPassword_WritesNothing()
    {
        var h = await CreateAsync(_ => new ProviderConnectionTestResult(false, "bad login", TimeSpan.Zero, AuthenticationFailed: true));
        using var _ = h.Db;

        var result = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfEnrollCodes.WrongPassword, result.Failure!.Code);
        Assert.Empty(h.Db.Agents);
        Assert.Empty(h.Db.Employees);
        Assert.Empty(h.Db.EmailAccounts);
        Assert.Contains(h.Audit.Entries, e => e.Action == "AGENT_SELF_ENROLL_FAILED");
        Assert.DoesNotContain(h.Audit.Entries, e => (e.Details ?? "").Contains("pw1"));
    }

    [Fact]
    public async Task UnreachableMailServer_IsReportedAsServerNotFound_NotAsWrongPassword()
    {
        var h = await CreateAsync(_ => new ProviderConnectionTestResult(false, "no route", TimeSpan.Zero));
        using var _ = h.Db;

        var result = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.Equal(SelfEnrollCodes.ServerNotFound, result.Failure!.Code);
        Assert.Empty(h.Db.EmailAccounts);
    }

    [Fact]
    public async Task ExistingMailboxWithNewPassword_StoresTheProvenPassword()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        var owner = new Employee { FullName = "Maria Santos", Email = "maria.santos@sawo.com", IsActive = true };
        var enc = new PassThroughEncryptionService().Encrypt("old-password");
        var account = new EmailAccount
        {
            EmailAddress = "maria.santos@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "mail.sawo.com", Port = 993,
            Username = "maria.santos@sawo.com", AuthMethod = EmailAuthMethod.Password, IsActive = true, OwnerEmployeeId = owner.Id,
            Credential = new EmailCredential { EncryptedSecret = enc.Ciphertext, Nonce = enc.Nonce, Tag = enc.Tag, KeyId = enc.KeyId },
        };
        h.Db.Employees.Add(owner);
        h.Db.EmailAccounts.Add(account);
        await h.Db.SaveChangesAsync();

        var result = await h.Service.EnrollAsync(Request(h, password: "new-password"), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Failure?.Message);
        var credential = (await h.Db.EmailAccounts.Include(a => a.Credential).SingleAsync()).Credential!;
        var stored = new PassThroughEncryptionService().Decrypt(new(credential.EncryptedSecret, credential.Nonce, credential.Tag, credential.KeyId));
        Assert.Equal("new-password", stored);
    }

    [Fact]
    public async Task PersonalAndUnknownKeys_AreRefused_BeforeAnyMailboxLogin()
    {
        var h = await CreateAsync(selfEnrollment: false); // a normal "Everyone" key, not a company installer
        using var _ = h.Db;
        var logins = 0;
        h.Adapter.TestConnectionBehavior = (_, _) => { logins++; return Task.FromResult(new ProviderConnectionTestResult(true, null, TimeSpan.Zero)); };

        var personal = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);
        var unknown = await h.Service.EnrollAsync(Request(h, key: "not-a-key"), null, CancellationToken.None);

        Assert.Equal(SelfEnrollCodes.InvalidKey, personal.Failure!.Code);
        Assert.Equal(SelfEnrollCodes.InvalidKey, unknown.Failure!.Code);
        Assert.Equal(0, logins);
    }

    [Fact]
    public async Task RevokedKey_IsRefused()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        (await h.Db.AgentInstallKeys.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow;
        await h.Db.SaveChangesAsync();

        var result = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.Equal(SelfEnrollCodes.InvalidKey, result.Failure!.Code);
    }

    [Fact]
    public async Task AddressOutsideTheCompanyDomains_IsRefused_WithoutALogin()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        var logins = 0;
        h.Adapter.TestConnectionBehavior = (_, _) => { logins++; return Task.FromResult(new ProviderConnectionTestResult(true, null, TimeSpan.Zero)); };

        var result = await h.Service.EnrollAsync(Request(h, email: "someone@gmail.com"), null, CancellationToken.None);

        Assert.Equal(SelfEnrollCodes.DomainNotAllowed, result.Failure!.Code);
        Assert.Equal(0, logins);
    }

    [Fact]
    public async Task DeactivatedEmployee_IsRefused_AndNeverReactivated()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        h.Db.Employees.Add(new Employee { FullName = "Maria Santos", Email = "maria.santos@sawo.com", IsActive = false });
        await h.Db.SaveChangesAsync();

        var result = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.Equal(SelfEnrollCodes.EmployeeDeactivated, result.Failure!.Code);
        Assert.False((await h.Db.Employees.SingleAsync()).IsActive);
        Assert.Empty(h.Db.Agents);
    }

    [Fact]
    public async Task SharedMailbox_GoesToPending_ForAnAdministrator()
    {
        var h = await CreateAsync();
        using var _ = h.Db;
        var owner = new Employee { FullName = "Sam Lee", Email = "sam@sawo.com", IsActive = true };
        h.Db.Employees.Add(owner);
        h.Db.EmailAccounts.Add(new EmailAccount
        {
            EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "mail.sawo.com", Port = 993,
            Username = "sales@sawo.com", AuthMethod = EmailAuthMethod.Password, IsActive = true, OwnerEmployeeId = owner.Id, Kind = EmailAccountKind.Shared,
        });
        await h.Db.SaveChangesAsync();

        var result = await h.Service.EnrollAsync(Request(h, email: "sales@sawo.com"), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal(AgentRegistrationStatus.Pending, result.Response!.Status);
        Assert.NotNull(result.Response.Message);
    }

    [Fact]
    public async Task RepeatedWrongPasswords_LockTheAddress()
    {
        var h = await CreateAsync(_ => new ProviderConnectionTestResult(false, "bad", TimeSpan.Zero, AuthenticationFailed: true));
        using var _ = h.Db;
        for (var i = 0; i < EnrollmentAttemptLimiter.MaxFailures; i++)
            await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        var result = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.Equal(SelfEnrollCodes.TooManyAttempts, result.Failure!.Code);
    }

    [Fact]
    public async Task ReinstallOnTheSamePc_RevokesTheEarlierRegistration()
    {
        var h = await CreateAsync();
        using var _ = h.Db;

        var first = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);
        var second = await h.Service.EnrollAsync(Request(h), null, CancellationToken.None);

        Assert.True(first.Succeeded && second.Succeeded);
        var agents = await h.Db.Agents.ToListAsync();
        Assert.Equal(AgentRegistrationStatus.Revoked, agents.Single(a => a.Id == first.Response!.AgentId).RegistrationStatus);
        Assert.Equal(AgentRegistrationStatus.Approved, agents.Single(a => a.Id == second.Response!.AgentId).RegistrationStatus);
    }

    [Fact]
    public void TheRequestNeverPrintsThePassword()
    {
        var request = new SelfEnrollAgentRequest { EmailAddress = "a@sawo.com", Password = "super-secret", ClientName = "PC" };
        Assert.DoesNotContain("super-secret", request.ToString());
    }

    [Theory]
    [InlineData("maria.santos@x.com", "Maria Santos")]
    [InlineData("juan_dela-cruz2@x.com", "Juan Dela Cruz")]
    [InlineData("info@x.com", "Info")]
    public void NameIsDerivedFromTheAddress(string email, string expected) =>
        Assert.Equal(expected, AgentSelfEnrollmentService.NameFromAddress(email));

    [Fact]
    public void PresetLinesAreParsed_ForTheRightDomainOnly()
    {
        var text = "sawo.com = mail.sawo.com:993:ssl\nother.com = x.other.com:143:starttls\nbroken line\nsawo.com = bad:port";
        var list = MailServerDiscovery.ParsePresets(text, "sawo.com");
        Assert.Equal(new MailServerCandidate("mail.sawo.com", 993, "SSL/TLS"), Assert.Single(list));
        Assert.Equal("STARTTLS", MailServerDiscovery.ParsePresets(text, "other.com").Single().Encryption);
    }

    [Fact]
    public void ThunderbirdAutoconfigXml_IsParsed()
    {
        var xml = "<clientConfig><emailProvider><incomingServer type=\"imap\"><hostname>imap.example.org</hostname><port>143</port><socketType>STARTTLS</socketType></incomingServer>"
            + "<incomingServer type=\"pop3\"><hostname>pop.example.org</hostname><port>995</port><socketType>SSL</socketType></incomingServer></emailProvider></clientConfig>";
        var list = ThunderbirdAutoconfigLookup.ParseXml(xml);
        Assert.Equal(new MailServerCandidate("imap.example.org", 143, "STARTTLS"), Assert.Single(list));
    }
}
