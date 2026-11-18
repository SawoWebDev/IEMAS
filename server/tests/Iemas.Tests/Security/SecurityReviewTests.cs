using System.Reflection;
using System.Security.Cryptography;
using Iemas.Api.Controllers;
using Iemas.Application.Auth;
using Iemas.Application.Auth.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Inquiries;
using Iemas.Domain.Identity;
using Iemas.Infrastructure.Audit;
using Iemas.Infrastructure.Security;
using Iemas.Tests.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iemas.Tests.Security;

/// <summary>Security review (recommendation 11): authorization matrix, encryption at rest, audit content.</summary>
public class SecurityReviewTests
{
    private static IEnumerable<Type> Controllers() =>
        typeof(AuthController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
        Controllers().SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any())
            .Select(m => (c, m)));

    private static bool Anonymous(Type c, MethodInfo m) =>
        m.GetCustomAttribute<AllowAnonymousAttribute>() != null || c.GetCustomAttribute<AllowAnonymousAttribute>() != null;

    private static string? Policy(Type c, MethodInfo m) =>
        m.GetCustomAttribute<AuthorizeAttribute>()?.Policy ?? c.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

    private static bool Authorized(Type c, MethodInfo m) =>
        m.GetCustomAttribute<AuthorizeAttribute>() != null || c.GetCustomAttribute<AuthorizeAttribute>() != null;

    [Fact]
    public void EveryAction_RequiresAuthorization_OrIsAnExplicitlyAllowedAnonymousEndpoint()
    {
        var unprotected = Actions().Where(a => !Authorized(a.Controller, a.Action) && !Anonymous(a.Controller, a.Action))
            .Select(a => $"{a.Controller.Name}.{a.Action.Name}").ToList();
        Assert.Empty(unprotected);
    }

    [Fact]
    public void AnonymousEndpoints_AreOnlyTheAuthBoundary_AndAllRateLimited()
    {
        var allowed = new HashSet<string>
        {
            "AuthController.Login", "AuthController.Refresh", "AgentAuthController.Authenticate",
            "AgentEnrollmentController.Register", "AgentEnrollmentController.GetStatus",
        };
        var anonymous = Actions().Where(a => Anonymous(a.Controller, a.Action)).ToList();
        Assert.NotEmpty(anonymous);
        foreach (var (c, m) in anonymous)
        {
            Assert.True(allowed.Contains($"{c.Name}.{m.Name}"), $"Unexpected anonymous endpoint {c.Name}.{m.Name}");
            var limited = m.GetCustomAttribute<EnableRateLimitingAttribute>() != null || c.GetCustomAttribute<EnableRateLimitingAttribute>() != null;
            Assert.True(limited, $"{c.Name}.{m.Name} is anonymous but not rate limited");
        }
    }

    [Fact]
    public void StateChangingActions_NeverRelyOnABareAuthorize_ExceptSelfServiceOnes()
    {
        // A bare [Authorize] means "any signed-in CMS user": acceptable only for the user's own session/password.
        var selfService = new HashSet<string> { "AuthController.Logout", "UsersController.ChangeOwnPassword" };
        var write = new[] { typeof(HttpPostAttribute), typeof(HttpPutAttribute), typeof(HttpDeleteAttribute), typeof(HttpPatchAttribute) };
        var offenders = Actions()
            .Where(a => write.Any(w => a.Action.GetCustomAttribute(w) != null))
            .Where(a => !Anonymous(a.Controller, a.Action) && string.IsNullOrEmpty(Policy(a.Controller, a.Action)))
            .Select(a => $"{a.Controller.Name}.{a.Action.Name}")
            .Where(n => !selfService.Contains(n)).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void ConfigurationAndSecretSurfaces_RequireAdministratorForWrites()
    {
        var writes = new[] { typeof(HttpPostAttribute), typeof(HttpPutAttribute), typeof(HttpDeleteAttribute) };
        var admin = new[] { "RequireAdministrator", "RequireSuperAdministrator" };
        foreach (var t in new[] { typeof(EmailAccountsController), typeof(AiModelsController), typeof(UsersController), typeof(AgentsController), typeof(MaintenanceController) })
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any()))
            {
                if (t == typeof(UsersController) && m.Name == "ChangeOwnPassword") continue;
                if (writes.Any(w => m.GetCustomAttribute(w) != null))
                    Assert.Contains(Policy(t, m), admin);
            }
        }
    }

    [Fact]
    public void InquiryAuditAndAgentControllers_AreRestricted()
    {
        Assert.Equal("RequireSupervisorOrAbove", typeof(InquiriesController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("RequireAdministrator", typeof(InquiriesController).GetMethod(nameof(InquiriesController.RunNow))!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("RequireAuditorOrAbove", typeof(AuditLogsController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("RequireAgent", typeof(AgentOperationsController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    private static AgentReplyProtector NewProtector() =>
        new(new AesGcmCredentialEncryptionService(Options.Create(new CredentialEncryptionOptions { Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })));

    [Fact]
    public void AgentReplyBody_IsEncryptedAtRest_AndRoundTrips()
    {
        var protector = NewProtector();
        const string body = "Hello Mr Smith, your refund of 120 EUR will be processed tomorrow.";

        var stored = protector.Protect(body);

        Assert.DoesNotContain("refund", stored);
        Assert.DoesNotContain("Smith", stored);
        Assert.Equal(body, protector.Reveal(stored));
        Assert.NotEqual(stored, protector.Protect(body)); // fresh nonce each time
    }

    [Fact]
    public void AgentReplyBody_WithTamperedCiphertext_IsNotRevealed()
    {
        var protector = NewProtector();
        var parts = protector.Protect("secret text").Split(':');
        var cipher = Convert.FromBase64String(parts[3]);
        cipher[0] ^= 0xFF;
        parts[3] = Convert.ToBase64String(cipher);

        Assert.Null(protector.Reveal(string.Join(':', parts)));
    }

    [Fact]
    public void AgentReplyBody_WithAnotherKey_IsNotRevealed() =>
        Assert.Null(NewProtector().Reveal(NewProtector().Protect("hello")));

    [Fact]
    public void AgentReplyBody_MasksCardNumbersAndPasswordsBeforeStorage()
    {
        var text = AgentReplyProtector.Sanitize("Card 4111 1111 1111 1111 and password: hunter2 thanks");
        Assert.DoesNotContain("4111", text);
        Assert.DoesNotContain("hunter2", text);
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "H";
        public bool Verify(string password, string hash) => password == "right-password";
    }

    private sealed class FakeJwt : IJwtTokenService
    {
        public string GenerateAccessToken(User user, IEnumerable<string> roles) => "access-token-value";
        public (string Token, DateTimeOffset ExpiresAt) GenerateRefreshToken() => ("refresh-token-value", DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task LoginAudit_RecordsOutcome_ButNeverPasswordsOrTokens()
    {
        using var db = TestDbContext.CreateNew();
        db.Users.Add(new User { Email = "agent@sawo.com", DisplayName = "A", PasswordHash = "x", IsActive = true });
        await db.SaveChangesAsync();
        var auth = new AuthService(db, new FakeHasher(), new FakeJwt(), new AuditService(db, new FakeCurrentUserService()));

        Assert.Null(await auth.LoginAsync(new LoginRequest("agent@sawo.com", "wrong-password-123"), default));
        Assert.NotNull(await auth.LoginAsync(new LoginRequest("agent@sawo.com", "right-password"), default));

        var logs = db.AuditLogs.ToList();
        Assert.Contains(logs, l => l.Action == "LOGIN_FAILED");
        Assert.Contains(logs, l => l.Action == "LOGIN_SUCCESS");
        foreach (var l in logs)
        {
            var all = $"{l.Action} {l.EntityId} {l.Details}";
            Assert.DoesNotContain("wrong-password-123", all);
            Assert.DoesNotContain("right-password", all);
            Assert.DoesNotContain("access-token-value", all);
            Assert.DoesNotContain("refresh-token-value", all);
        }
    }
}
