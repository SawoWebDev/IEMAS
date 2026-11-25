using System.Collections.Concurrent;

namespace Iemas.Application.Agents;

/// <summary>
/// Stops the PC self-registration screen being used to guess mailbox passwords: after
/// <see cref="MaxFailures"/> wrong passwords for one address, that address is refused for
/// <see cref="Window"/>. Kept in memory, in addition to the per-IP rate limit.
/// </summary>
public sealed class EnrollmentThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public EnrollmentThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsBlocked(string email)
    {
        if (!_failures.TryGetValue(email, out var list)) return false;
        lock (list)
        {
            Prune(list);
            return list.Count >= MaxFailures;
        }
    }

    public void RecordFailure(string email)
    {
        var list = _failures.GetOrAdd(email, _ => new List<DateTimeOffset>());
        lock (list)
        {
            Prune(list);
            list.Add(_time.GetUtcNow());
        }
    }

    public void Clear(string email) => _failures.TryRemove(email, out _);

    private void Prune(List<DateTimeOffset> list)
    {
        var cutoff = _time.GetUtcNow() - Window;
        list.RemoveAll(t => t < cutoff);
    }
}
