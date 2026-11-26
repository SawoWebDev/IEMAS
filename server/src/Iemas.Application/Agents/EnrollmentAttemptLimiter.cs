using System.Collections.Concurrent;

namespace Iemas.Application.Agents;

/// <summary>
/// Per-address lockout for PC self-enrollment: after <see cref="MaxFailures"/> wrong passwords for one address
/// within <see cref="Window"/>, further attempts are refused until the window passes. In memory on purpose:
/// it only needs to slow guessing down, and a server restart clearing it is harmless.
/// </summary>
public class EnrollmentAttemptLimiter
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _failures = new();

    public bool IsLocked(string email)
    {
        if (!_failures.TryGetValue(email, out var entry)) return false;
        if (DateTimeOffset.UtcNow - entry.Since > Window)
        {
            _failures.TryRemove(email, out _);
            return false;
        }
        return entry.Count >= MaxFailures;
    }

    public void RecordFailure(string email) =>
        _failures.AddOrUpdate(email, _ => (1, DateTimeOffset.UtcNow),
            (_, old) => DateTimeOffset.UtcNow - old.Since > Window ? (1, DateTimeOffset.UtcNow) : (old.Count + 1, old.Since));

    public void Reset(string email) => _failures.TryRemove(email, out _);
}
