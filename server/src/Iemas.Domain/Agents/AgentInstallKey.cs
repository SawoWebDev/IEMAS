using Iemas.Domain.Common;
using Iemas.Domain.Identity;

namespace Iemas.Domain.Agents;

/// <summary>
/// A pre-approval an administrator creates by downloading an Agent installer package from the CMS.
/// The package carries the raw key; an Agent that registers with it is approved straight away instead of
/// waiting as Pending, because an authenticated administrator already vouched for it when they made the
/// download. Limits keep a leaked package harmless: it expires, it has a maximum number of uses, it can be
/// revoked, and when it names an email account it only works for that account. Only the SHA-256 hash of
/// the key is stored.
/// </summary>
public class AgentInstallKey : Entity
{
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>When set, the key only registers PCs for this mailbox (and therefore its owner).</summary>
    public string? EmailAddress { get; set; }

    public string? Label { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxUses { get; set; }
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }
}
