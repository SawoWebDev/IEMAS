using System.Security.Cryptography;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>
/// Installer keys for the one-click Agent installer (see <see cref="AgentInstallKey"/>). An administrator
/// creates one by downloading an installer package; PCs that register with it are approved straight away.
/// </summary>
public class AgentInstallKeyService
{
    public const int MaxValidDays = 36500;
    public const int MaxUsesLimit = 100000;

    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;

    public AgentInstallKeyService(IAppDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public record CreatedInstallKey(AgentInstallKey Key, string RawKey, string? OwnerName);

    public async Task<Result<CreatedInstallKey>> CreateAsync(CreateInstallPackageRequest request, Guid createdByUserId, CancellationToken cancellationToken)
    {
        if (request.ValidDays is < 1 or > MaxValidDays)
            return Result<CreatedInstallKey>.Failure($"Valid for must be between 1 and {MaxValidDays} days.");
        if (request.MaxUses is < 1 or > MaxUsesLimit)
            return Result<CreatedInstallKey>.Failure($"Number of PCs must be between 1 and {MaxUsesLimit}.");
        if (!Uri.TryCreate(request.ServerUrl?.Trim(), UriKind.Absolute, out var server) || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
            return Result<CreatedInstallKey>.Failure("Enter the server address the PCs use, for example https://172.16.2.68:8443.");

        string? email = null;
        string? ownerName = null;
        if (!string.IsNullOrWhiteSpace(request.EmailAddress))
        {
            email = request.EmailAddress.Trim().ToLowerInvariant();
            var account = await _db.EmailAccounts.AsNoTracking()
                .Where(a => a.EmailAddress == email && a.Purpose == EmailAccountPurpose.Inbound)
                .Select(a => new { Owner = a.OwnerEmployee })
                .FirstOrDefaultAsync(cancellationToken);
            if (account is null)
                return Result<CreatedInstallKey>.Failure($"{email} is not a monitored mailbox. Add it on the Email Accounts page first.");
            if (account.Owner is null || !account.Owner.IsActive)
                return Result<CreatedInstallKey>.Failure($"{email} has no active owner. Set the owner on the Email Accounts page first.");
            ownerName = account.Owner.FullName;
        }

        var rawKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = new AgentInstallKey
        {
            KeyHash = Hash(rawKey),
            EmailAddress = email,
            Label = email is null ? "Any monitored mailbox" : $"{ownerName} ({email})",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(request.ValidDays),
            MaxUses = request.MaxUses,
            CreatedByUserId = createdByUserId,
        };
        _db.AgentInstallKeys.Add(key);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("AGENT_INSTALLER_CREATED", "AgentInstallKey", key.Id.ToString(),
            $"{key.Label}; up to {key.MaxUses} PC(s); valid until {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC", cancellationToken);
        return Result<CreatedInstallKey>.Success(new CreatedInstallKey(key, rawKey, ownerName));
    }

    public async Task<List<AgentInstallKeyDto>> ListAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.AgentInstallKeys.AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .Take(100)
            .Select(k => new
            {
                k.Id, k.EmailAddress, k.Label, k.CreatedAt, CreatedBy = k.CreatedByUser != null ? k.CreatedByUser.Email : null,
                k.ExpiresAt, k.MaxUses, k.UseCount, k.LastUsedAt, k.RevokedAt,
            })
            .ToListAsync(cancellationToken);

        var emails = rows.Where(r => r.EmailAddress != null).Select(r => r.EmailAddress!).Distinct().ToList();
        var owners = await _db.EmailAccounts.AsNoTracking()
            .Where(a => emails.Contains(a.EmailAddress) && a.OwnerEmployee != null)
            .Select(a => new { a.EmailAddress, a.OwnerEmployee!.FullName })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AgentInstallKeyDto(
            r.Id, r.EmailAddress, owners.FirstOrDefault(o => o.EmailAddress == r.EmailAddress)?.FullName, r.Label, r.CreatedAt, r.CreatedBy,
            r.ExpiresAt, r.MaxUses, r.UseCount, r.LastUsedAt, r.RevokedAt,
            r.RevokedAt is null && r.ExpiresAt > now && r.UseCount < r.MaxUses)).ToList();
    }

    public async Task<Result<bool>> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = await _db.AgentInstallKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null) return Result<bool>.Failure("Installer not found.");
        if (key.RevokedAt is not null) return Result<bool>.Failure("This installer was already withdrawn.");

        key.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("AGENT_INSTALLER_REVOKED", "AgentInstallKey", id.ToString(), key.Label, cancellationToken);
        return Result<bool>.Success(true);
    }

    internal static string Hash(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
}
