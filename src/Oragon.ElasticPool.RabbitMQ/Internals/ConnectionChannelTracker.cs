using System.Collections.Concurrent;
using RabbitMQ.Client;

namespace Oragon.ElasticPool.RabbitMQ.Internals;

/// <summary>
/// Tracks the live channel count per <see cref="IConnection"/> for the eager-spread
/// strategy used by <c>AddElasticChannelPool</c>'s Factory hook (per RESEARCH Q2).
/// </summary>
/// <remarks>
/// <para><b>Atomicity invariant.</b> All count mutations use compare-and-swap via
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd(TKey, TValue)"/> /
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryUpdate(TKey, TValue, TValue)"/> with
/// retry-on-race; <see cref="ReleaseSlot"/> uses
/// <see cref="ICollection{T}.Remove(T)"/> on the backing key/value collection for an
/// atomic compare-and-remove when the count would reach 0. The dictionary entry is
/// removed at count==0 to bound dictionary growth — no entry exists for a connection
/// with zero live channels.</para>
/// <para>Threat T-03-08 (negative count / count > max) is mitigated here.</para>
/// </remarks>
internal sealed class ConnectionChannelTracker
{
    private readonly ConcurrentDictionary<IConnection, int> _counts = new();

    /// <summary>
    /// Atomically increments the channel count for <paramref name="connection"/> if it
    /// is below <paramref name="max"/>. Returns true on success (slot acquired), false
    /// when the connection is already saturated.
    /// </summary>
    public bool TryAcquireSlot(IConnection connection, int max)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (max < 1) throw new ArgumentOutOfRangeException(nameof(max), "max must be >= 1.");

        while (true)
        {
            var hasEntry = _counts.TryGetValue(connection, out var current);
            if (hasEntry && current >= max) return false;
            var next = current + 1;
            if (!hasEntry)
            {
                // No entry yet → insert with value 1.
                if (_counts.TryAdd(connection, next)) return true;
                // Another thread inserted between our read and TryAdd — re-read.
            }
            else
            {
                // Entry exists with value `current` → CAS-update to current+1.
                // WR-01 fix: handle current==0 via TryUpdate, not TryAdd. Under the
                // current invariant (entries are removed at count 1) this branch is
                // unreachable for current==0, but routing it through TryUpdate makes
                // the CAS direction unambiguous and removes the latent livelock that
                // would occur if a future ReleaseSlot ever stored 0 in-place.
                if (_counts.TryUpdate(connection, next, current)) return true;
                // Lost CAS — re-read.
            }
        }
    }

    /// <summary>
    /// Atomically decrements the channel count for <paramref name="connection"/>. The
    /// count is bounded below at 0 (no-op when the connection has no entry). When the
    /// count reaches 0, the entry is removed from the dictionary.
    /// </summary>
    public void ReleaseSlot(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        while (true)
        {
            if (!_counts.TryGetValue(connection, out var current)) return; // never-seen or already-zeroed: no-op
            if (current <= 1)
            {
                // Atomic compare-remove via ICollection<KVP>.Remove. Works on net8/9/10.
                if (((ICollection<KeyValuePair<IConnection, int>>)_counts)
                        .Remove(new KeyValuePair<IConnection, int>(connection, current)))
                    return;
                // Lost CAS — re-read.
            }
            else
            {
                if (_counts.TryUpdate(connection, current - 1, current)) return;
                // Lost CAS — re-read.
            }
        }
    }

    /// <summary>Returns the current live-channel count for <paramref name="connection"/>, or 0.</summary>
    public int CountFor(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _counts.TryGetValue(connection, out var c) ? c : 0;
    }
}
