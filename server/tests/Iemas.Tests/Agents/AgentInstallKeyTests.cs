using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Domain.Agents;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Agents;

/// <summary>
/// One-click Agent installer: an installer key created by an administrator approves the PC at registration,
/// within the key's limits (mailbox, expiry, number of uses, withdrawal).
/// </summary>
public class AgentInstallKeyTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    private static async Task<(TestDbContext Db, Employee Owner)> SeedAsync(bool withOwner = true)
    {
        var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "Maria Santos", Email = "maria@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        db.EmailAccounts.Add(Account("maria@sawo.com", withOwner ? owner.Id : null));
        db.EmailAccounts.Add(Account("sales@sawo.com", owner.Id));
        await db.SaveChangesAsync();
        return (db, owner);
    }

    private static EmailAccount Account(string email, Guid? ownerId) => new()
    {
        EmailAddress = email, Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "imap.example.com", Port = 993,
        Username = email, AuthMethod = EmailAuthMethod.Password, IsActive = true, OwnerEmployeeId = ownerId,
    };

    private static AgentRegistrationService Registration(TestDbContext db, NoOpAuditService? audit = null) =>
        new(db, new FakeAgentTokenService(), new PassThroughEncryptionService(), audit ?? new NoOpAuditService());

    private static AgentInstallKeyService Keys(TestDbContext db, NoOpAuditService? audit = null) => new(db, audit ?? new NoOpAuditService());

    private static CreateInstallPackageRequest Package(string? email, int days = 7, int uses = 3) =>
        new(email, "https://172.16.2.68:8443", true, days, uses);

    private static RegisterAgentRequest Register(string email, string? key) => new(email, "SALES-PC-07", "https://172.16.2.68:8443", "1.1.1", null, key);

    [Fact]
    public async Task ValidKeyForTheMailbox_ApprovesThePcStraightAway_AndTheAgentCollectsItsKey()
    {
        var (db, owner) = await SeedAsync();
        using var _ = db;
        var audit = new NoOpAuditService();
        var created = (await Keys(db).CreateAsync(Package("maria@sawo.com"), AdminId, CancellationToken.None)).Value!;

        var result = await Registration(db, audit).RegisterAsync(Register("maria@sawo.com", created.RawKey), "10.0.0.5", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(AgentRegistrationStatus.Approved, result.Value!.Status);
        var agent = await db.Agents.SingleAsync();
        Assert.Equal(owner.Id, agent.EmployeeId);
        Assert.Equal(AdminId, agent.ApprovedByUserId);
        Assert.Equal(1, (await db.AgentInstallKeys.SingleAsync()).UseCount);
        Assert.Contains(audit.Entries, e => e.Action == "AGENT_AUTO_APPROVED");

        var status = await Registration(db).GetRegistrationStatusAsync(result.Value.RegistrationRequestToken, CancellationToken.None);
        Assert.NotNull(status.Value!.RegistrationKey);
    }

    [Fact]
    public async Task KeyForAnyMailbox_ApprovesForTheOwnerOfTheTypedMailbox()
    {
        var (db, owner) = await SeedAsync();
        using var _ = db;
        var created = (await Keys(db).CreateAsync(Package(null, uses: 25), AdminId, CancellationToken.None)).Value!;

        var result = await Registration(db).RegisterAsync(Register("sales@sawo.com", created.RawKey), null, CancellationToken.None);

        Assert.Equal(AgentRegistrationStatus.Approved, result.Value!.Status);
        Assert.Equal(owner.Id, (await db.Agents.SingleAsync()).EmployeeId);
    }

    [Fact]
    public async Task KeyForAnotherMailbox_IsRefused_AndNothingIsCreated()
    {
        var (db, _) = await SeedAsync();
        using var __ = db;
        var created = (await Keys(db).CreateAsync(Package("maria@sawo.com"), AdminId, CancellationToken.None)).Value!;

        var result = await Registration(db).RegisterAsync(Register("sales@sawo.com", created.RawKey), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("maria@sawo.com", result.Error);
        Assert.Equal(0, await db.Agents.CountAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("usedUp")]
    [InlineData("unknown")]
    public async Task UnusableKey_IsRefused_WithAMessageTheAgentRecognises(string why)
    {
        var (db, _) = await SeedAsync();
        using var __ = db;
        var keys = Keys(db);
        var created = (await keys.CreateAsync(Package("maria@sawo.com", uses: 1), AdminId, CancellationToken.None)).Value!;
        var raw = created.RawKey;
        switch (why)
        {
            case "expired": created.Key.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); break;
            case "revoked": await keys.RevokeAsync(created.Key.Id, CancellationToken.None); break;
            case "usedUp": await Registration(db).RegisterAsync(Register("maria@sawo.com", raw), null, CancellationToken.None); break;
            case "unknown": raw = "not-a-real-key"; break;
        }
        var before = await db.Agents.CountAsync();

        var result = await Registration(db).RegisterAsync(Register("maria@sawo.com", raw), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("can no longer be used", result.Error); // the Agent falls back to a normal Pending request on this text
        Assert.Equal(before, await db.Agents.CountAsync());
    }

    [Fact]
    public async Task MailboxWithoutOwner_CannotGetAnInstaller()
    {
        var (db, _) = await SeedAsync(withOwner: false);
        using var __ = db;

        var result = await Keys(db).CreateAsync(Package("maria@sawo.com"), AdminId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no active owner", result.Error);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(91, 3)]
    [InlineData(7, 0)]
    [InlineData(7, 501)]
    public async Task OutOfRangeLimits_AreRefused(int days, int uses)
    {
        var (db, _) = await SeedAsync();
        using var __ = db;

        var result = await Keys(db).CreateAsync(Package("maria@sawo.com", days, uses), AdminId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await db.AgentInstallKeys.CountAsync());
    }

    [Fact]
    public async Task OnlyTheHashOfTheKeyIsStored()
    {
        var (db, _) = await SeedAsync();
        using var __ = db;

        var created = (await Keys(db).CreateAsync(Package(null), AdminId, CancellationToken.None)).Value!;

        var stored = await db.AgentInstallKeys.SingleAsync();
        Assert.NotEqual(created.RawKey, stored.KeyHash);
        Assert.Equal(64, stored.KeyHash.Length);
    }

    [Fact]
    public async Task WithoutAKey_RegistrationStillWaitsForApproval()
    {
        var (db, _) = await SeedAsync();
        using var __ = db;

        var result = await Registration(db).RegisterAsync(Register("maria@sawo.com", null), null, CancellationToken.None);

        Assert.Equal(AgentRegistrationStatus.Pending, result.Value!.Status);
    }
}
