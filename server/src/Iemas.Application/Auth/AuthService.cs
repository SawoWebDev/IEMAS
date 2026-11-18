using System.Security.Cryptography;
using Iemas.Application.Auth.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Auth;

public class AuthService
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IAuditService _auditService;

    public AuthService(
        IAppDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IAuditService auditService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _auditService = auditService;
    }

    /// <summary>Failed attempts for one account within <see cref="LockoutWindow"/> that trigger a lockout.</summary>
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    /// <summary>Maximum length (RFC 5321 address limit) of an attacker-supplied email kept in the audit trail.</summary>
    public const int MaxAuditEmailLength = 320;

    /// <summary>Strips control characters, trims, lower-cases and truncates an email for lookup/audit use.</summary>
    public static string NormalizeEmailForAudit(string? email)
    {
        if (string.IsNullOrEmpty(email)) return string.Empty;
        var cleaned = new string(email.Where(c => !char.IsControl(c)).ToArray()).Trim().ToLowerInvariant();
        return cleaned.Length > MaxAuditEmailLength ? cleaned[..MaxAuditEmailLength] : cleaned;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var auditEmail = NormalizeEmailForAudit(request.Email);

        // Per-account lockout, window based (no schema change): count recent LOGIN_FAILED rows for this
        // address. Identical outward result to a bad password, so it never reveals whether the account exists.
        var since = DateTimeOffset.UtcNow - LockoutWindow;
        var recentFailures = await _db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Action == "LOGIN_FAILED" && a.EntityId == auditEmail && a.CreatedAt >= since, cancellationToken);
        if (recentFailures >= MaxFailedAttempts)
        {
            await _auditService.LogAsync("LOGIN_LOCKED", "User", auditEmail, "Too many failed attempts", cancellationToken);
            return null;
        }

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == auditEmail, cancellationToken);

        if (user is null || !user.IsActive || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            await _auditService.LogAsync("LOGIN_FAILED", "User", auditEmail, "Invalid credentials or inactive user", cancellationToken);
            return null;
        }

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var accessToken = _jwtTokenService.GenerateAccessToken(user, roles);
        var (refreshTokenValue, refreshExpiresAt) = _jwtTokenService.GenerateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashToken(refreshTokenValue),
            ExpiresAt = refreshExpiresAt
        });

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("LOGIN_SUCCESS", "User", user.Id.ToString(), null, cancellationToken);

        // Access token lifetime is defined by the JWT service; expose refresh expiry as the session bound.
        return new LoginResponse(
            accessToken,
            refreshTokenValue,
            refreshExpiresAt,
            user.Id,
            user.Email,
            user.DisplayName,
            roles);
    }

    public async Task<LoginResponse?> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(request.RefreshToken);
        var existing = await _db.RefreshTokens
            .Include(rt => rt.User).ThenInclude(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

        if (existing is null || !existing.IsActive || !existing.User.IsActive)
        {
            return null;
        }

        // Rotate: revoke old, issue new (requirements §84 credential rotation).
        existing.RevokedAt = DateTimeOffset.UtcNow;

        var roles = existing.User.UserRoles.Select(ur => ur.Role.Name).ToList();
        var accessToken = _jwtTokenService.GenerateAccessToken(existing.User, roles);
        var (refreshTokenValue, refreshExpiresAt) = _jwtTokenService.GenerateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = existing.User.Id,
            TokenHash = HashToken(refreshTokenValue),
            ExpiresAt = refreshExpiresAt
        });

        await _db.SaveChangesAsync(cancellationToken);

        return new LoginResponse(
            accessToken,
            refreshTokenValue,
            refreshExpiresAt,
            existing.User.Id,
            existing.User.Email,
            existing.User.DisplayName,
            roles);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(refreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);
        if (existing is not null && existing.RevokedAt is null)
        {
            existing.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}
