using Iemas.Application.Auth;
using Iemas.Application.Auth.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Audit;
using Iemas.Domain.Identity;
using Iemas.Infrastructure.Audit;
using Iemas.Infrastructure.Security;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Security;

public class AuthHardeningTests
{
    private sealed class Hasher : IPasswordHasher
    {
        public string Hash(string password) => "H";
        public bool Verify(string password, string hash) => password == "right-password";
    }

    private sealed class Jwt : IJwtTokenService
    {
        public string GenerateAccessToken(User user, IEnumerable<string> roles) => "a";
        public (string Token, DateTimeOffset ExpiresAt) GenerateRefreshToken() => ("r", DateTimeOffset.UtcNow.AddDays(1));
    }

    private static (AuthService Auth, TestDbContext Db) Create()
    {
        var db = TestDbContext.CreateNew();
        db.Users.Add(new User { Email = "agent@sawo.com", DisplayName = "A", PasswordHash = "x", IsActive = true });
        db.SaveChanges();
        return (new AuthService(db, new Hasher(), new Jwt(), new AuditService(db, new FakeCurrentUserService())), db);
    }

    [Fact]
    public async Task FifthFailure_LocksAccount_EvenForCorrectPassword_AndWritesLoginLocked()
    {
        var (auth, db) = Create();
        using var _ = db;
        for (var i = 0; i < AuthService.MaxFailedAttempts; i++)
            Assert.Null(await auth.LoginAsync(new LoginRequest("Agent@Sawo.com", "bad"), default));

        Assert.Null(await auth.LoginAsync(new LoginRequest("agent@sawo.com", "right-password"), default));
        Assert.Contains(db.AuditLogs, l => l.Action == "LOGIN_LOCKED");
        Assert.Equal(AuthService.MaxFailedAttempts, db.AuditLogs.Count(l => l.Action == "LOGIN_FAILED"));
    }

    [Fact]
    public async Task Lockout_DoesNotAffectOtherAccounts_AndUnknownAccountsLockIdentically()
    {
        var (auth, db) = Create();
        using var _ = db;
        for (var i = 0; i < AuthService.MaxFailedAttempts; i++)
            await auth.LoginAsync(new LoginRequest("ghost@nowhere.com", "bad"), default);

        Assert.Null(await auth.LoginAsync(new LoginRequest("ghost@nowhere.com", "right-password"), default));
        Assert.Contains(db.AuditLogs, l => l.Action == "LOGIN_LOCKED" && l.EntityId == "ghost@nowhere.com");
        Assert.NotNull(await auth.LoginAsync(new LoginRequest("agent@sawo.com", "right-password"), default));
    }

    [Fact]
    public async Task OldFailures_OutsideWindow_DoNotLock()
    {
        var (auth, db) = Create();
        using var _ = db;
        for (var i = 0; i < AuthService.MaxFailedAttempts; i++)
            db.AuditLogs.Add(new AuditLog
            {
                Action = "LOGIN_FAILED", EntityType = "User", EntityId = "agent@sawo.com",
                CreatedAt = DateTimeOffset.UtcNow - AuthService.LockoutWindow - TimeSpan.FromMinutes(1)
            });
        await db.SaveChangesAsync();

        Assert.NotNull(await auth.LoginAsync(new LoginRequest("agent@sawo.com", "right-password"), default));
    }

    [Fact]
    public async Task FailedLoginAudit_TruncatesAndStripsControlCharacters()
    {
        var (auth, db) = Create();
        using var _ = db;
        var evil = "a\r\nb\u0000" + new string('x', 1000) + "@e.com";

        await auth.LoginAsync(new LoginRequest(evil, "bad"), default);

        var row = Assert.Single(db.AuditLogs, l => l.Action == "LOGIN_FAILED");
        Assert.True(row.EntityId!.Length <= AuthService.MaxAuditEmailLength);
        Assert.DoesNotContain(row.EntityId, c => char.IsControl(c));
    }

    [Theory]
    [InlineData(null, true, true, JwtSecretStrength.WeakWarning)]
    [InlineData("short", true, true, JwtSecretStrength.WeakWarning)]
    [InlineData("short", false, true, JwtSecretStrength.WeakFatal)]
    [InlineData("short", false, false, JwtSecretStrength.WeakError)]
    [InlineData("0123456789012345678901234567890", false, true, JwtSecretStrength.WeakFatal)]
    [InlineData("01234567890123456789012345678901", false, true, JwtSecretStrength.Ok)]
    public void JwtSecretValidator_AppliesEnvironmentPolicy(string? secret, bool dev, bool failFast, JwtSecretStrength expected)
    {
        Assert.Equal(expected, JwtSecretValidator.Evaluate(secret, dev, failFast));
    }
}
