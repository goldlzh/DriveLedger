using System.Diagnostics;

namespace DriveLedger.Windows;

internal sealed class ActiveFileTracker
{
    internal const int Capacity = 8_192;
    internal const int HighFrequencyCapacity = 4_096;
    const int LowFrequencyRounds = 6;
    const double ActivityWindowSeconds = 30;
    // Give the first repeat check precedence over small changes without displacing heavy writers.
    const long FirstRefreshPriorityBytes = 64 * 1024;

    readonly Dictionary<ulong, Entry> files = [];

    internal IEnumerable<ulong> References => files.Keys;
    internal void Clear() => files.Clear();
    internal void Remove(ulong reference) => files.Remove(reference);

    internal bool TryTrack(ulong reference, long? allocatedBytes, long now)
    {
        if (!files.TryGetValue(reference, out var entry))
        {
            if (files.Count >= Capacity)
                return false;
            files.Add(reference, entry = new Entry());
        }

        entry.Observe(allocatedBytes, now);
        return true;
    }

    internal ulong[] SelectForRefresh(long now)
    {
        if (files.Count <= HighFrequencyCapacity)
            return files.Keys.ToArray();

        var ranked = files.OrderByDescending(file => file.Value.Priority(now))
            .ThenByDescending(file => file.Value.NeedsFirstRefresh)
            .ThenBy(file => file.Key)
            .ToArray();
        var lowFrequencyCount = ranked.Length - HighFrequencyCapacity;
        var lowFrequencyBudget = (lowFrequencyCount + LowFrequencyRounds - 1) / LowFrequencyRounds;
        return ranked.Take(HighFrequencyCapacity)
            .Concat(ranked.Skip(HighFrequencyCapacity)
                .OrderBy(file => file.Value.LastRefresh)
                .Take(lowFrequencyBudget))
            .Select(file => file.Key)
            .ToArray();
    }

    internal void Refreshed(ulong reference, long? allocatedBytes, long now)
    {
        var entry = files[reference];
        entry.NeedsFirstRefresh = false;
        entry.LastRefresh = now;
        entry.Observe(allocatedBytes, now);
    }

    sealed class Entry
    {
        long? allocation;
        long change;
        long changedAt;
        internal bool NeedsFirstRefresh = true;
        internal long LastRefresh;

        internal double Priority(long now) =>
            Math.Max(Activity(now), NeedsFirstRefresh ? FirstRefreshPriorityBytes : 0);

        internal double Activity(long now) => change == 0 ? 0 :
            change * Math.Max(0, 1 - Stopwatch.GetElapsedTime(changedAt, now).TotalSeconds / ActivityWindowSeconds);

        internal void Observe(long? allocatedBytes, long now)
        {
            if (allocatedBytes is not >= 0)
                return;

            if (allocation is { } previous)
            {
                var difference = Math.Abs(allocatedBytes.Value - previous);
                if (difference > Activity(now))
                {
                    change = difference;
                    changedAt = now;
                }
            }

            allocation = allocatedBytes;
        }
    }
}
