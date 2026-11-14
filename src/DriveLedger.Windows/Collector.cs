using DriveLedger.Core;
using System.ComponentModel;
using System.Text.Json;

namespace DriveLedger.Windows;
public sealed record CollectorOptions(string Volume = "C:\\", TimeSpan? SampleInterval = null, TimeSpan? JournalInterval = null, TimeSpan? ScanInterval = null, string? FixtureScanRoot = null, string? InstallAccountSid = null, int BatchSize = 256, TimeSpan? ScanBudget = null, TimeSpan? BatchDelay = null);
public sealed class Collector
{
    readonly MonitorStore store;
    readonly CollectorOptions options;
    readonly Func<UsnJournal, ulong, FileObservation> observe;
    readonly Func<string?> bootSessionProvider;
    string? bootSession;
    DateTimeOffset? activeDegradedSince;
    DateTimeOffset? activeGapAt;
    readonly object gate = new();
    DateTimeOffset? pausedAt;
    readonly Queue<(DateTimeOffset Start, DateTimeOffset End)> pauses = new();
    int scanRequested;
    int running;
    long failures, observed;
    string? sampleError;
    readonly HashSet<ulong> dirty = new();
    public Collector(MonitorStore store, CollectorOptions options) : this(store, options, (journal, reference) => journal.Resolve(reference))
    {
    }

    internal Collector(MonitorStore store, CollectorOptions options, Func<UsnJournal, ulong, FileObservation> observe, Func<string?>? bootSessionProvider = null)
    {
        this.observe = observe;
        this.bootSessionProvider = bootSessionProvider ?? ReadBootSession;
        this.store = store;
        this.options = options;
        if (!string.Equals(options.Volume, "C:\\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only C NTFS is supported");
        if (options.BatchSize is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options));
        foreach (var interval in new[]
        {
            options.SampleInterval,
            options.JournalInterval,
            options.ScanInterval,
            options.ScanBudget
        }

        )
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options));
        if (options.JournalInterval > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.FixtureScanRoot is not null && !Accounting.IsWithin(Path.GetFullPath(options.FixtureScanRoot), options.Volume))
            throw new ArgumentException("Fixture must be on C");
    }

    public void Pause()
    {
        lock (gate)
            pausedAt ??= DateTimeOffset.Now;
    }

    public void Resume()
    {
        lock (gate)
        {
            if (pausedAt is { } at)
            {
                pauses.Enqueue((at, DateTimeOffset.Now));
                pausedAt = null;
                RequestScan();
            }
        }
    }

    public void RequestScan() => Interlocked.Exchange(ref scanRequested, 1);
    bool Paused
    {
        get
        {
            lock (gate)
                return pausedAt.HasValue;
        }
    }

    void Status(string state, bool? ready = null, string? message = null)
    {
        if (activeDegradedSince is not null)
        {
            if (state == "运行中")
                state = "降级运行";
            message = $"active-set continuity degraded since {activeDegradedSince:O}; prior open writers are not fully tracked. " + message;
            RecordActiveGap(false);
        }

        var old = store.ReadCollectorStatus();
        store.SetCollectorStatus(old with { State = state, BaselineReady = ready ?? old.BaselineReady, FilesObserved = observed, AccessFailures = failures, Message = message ?? Volatile.Read(ref sampleError), UpdatedAt = DateTimeOffset.Now });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("Collector is already running");
        dirty.Clear();
        activeJson = null;
        activeDegradedSince = null;
        activeGapAt = null;
        bootSession = bootSessionProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? sampler = null;
        try
        {
            if (new DriveInfo(options.Volume).DriveFormat != "NTFS")
                throw new NotSupportedException("C must be NTFS");
            store.Initialize();
            var previous = store.ReadCollectorStatus();
            if (previous.UpdatedAt is { } last)
                store.AddCoverageGap("startup", "Collector was stopped", last, DateTimeOffset.Now);
            Status("初始化");
            sampler = Task.Run(() => SampleLoop(stop.Token), stop.Token);
            await IndexLoop(stop.Token);
        }
        catch (OperationCanceledException)when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Status("错误", message: e.Message);
            throw;
        }
        finally
        {
            stop.Cancel();
            if (sampler is not null)
                try
                {
                    await sampler;
                }
                catch (OperationCanceledException)
                {
                }

            lock (gate)
            {
                if (pausedAt is { } at)
                {
                    pauses.Enqueue((at, DateTimeOffset.Now));
                    pausedAt = null;
                }
            }

            DrainPauses();
            RecordActiveGap(true);
            if (cancellationToken.IsCancellationRequested)
                Status("已停止");
            Interlocked.Exchange(ref running, 0);
        }
    }

    async Task SampleLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!Paused)
            {
                try
                {
                    store.RecordDiskSample(NativeMetadata.Sample(options.Volume));
                    Volatile.Write(ref sampleError, null);
                }
                catch (Exception e)when (e is Win32Exception or IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Volatile.Write(ref sampleError, "Disk sampling: " + e.Message);
                }
            }

            await Task.Delay(options.SampleInterval ?? TimeSpan.FromSeconds(60), token);
        }
    }

    void DrainPauses()
    {
        lock (gate)
        {
            while (pauses.TryDequeue(out var p))
                store.AddCoverageGap("paused", "Collection paused by user", p.Start, p.End);
        }
    }

    async Task IndexLoop(CancellationToken token)
    {
        UsnJournal? journal = null;
        JournalState? state = null;
        var next = 0L;
        var scanInterval = options.ScanInterval ?? TimeSpan.FromDays(1);
        var nextScan = store.ReadNextScanAttemptAt() ?? DateTimeOffset.MinValue;
        try
        {
            try
            {
                journal = new(options.Volume);
                state = journal.Query();
                RestoreActive(journal.VolumeId, state.JournalId);
                // Durable active continuity state must precede even an empty journal checkpoint.
                PersistActive(journal.VolumeId, state.JournalId);
                var cp = store.ReadCheckpoint();
                if (cp is not null && cp.VolumeId != journal.VolumeId)
                    Status("初始化", false);
                if (cp is not null && cp.VolumeId == journal.VolumeId && cp.JournalId == state.JournalId.ToString() && cp.NextUsn >= Math.Max(state.FirstUsn, state.LowestValidUsn) && cp.NextUsn <= state.NextUsn)
                {
                    next = cp.NextUsn;
                    if (store.ReadCollectorStatus().BaselineReady && store.ReadLastCompletedScan() is { } completed)
                    {
                        var completedDeadline = completed + scanInterval;
                        if (completedDeadline > nextScan) nextScan = completedDeadline;
                    }
                }
                else
                {
                    next = state.NextUsn;
                    if (cp is not null) DegradeActive("Journal checkpoint continuity was lost");
                    store.AddCoverageGap("journal-gap", "No continuous journal checkpoint; metadata reconciliation required", store.ReadCollectorStatus().UpdatedAt ?? DateTimeOffset.Now, DateTimeOffset.Now);
                }
            }
            catch (Exception e)when (e is Win32Exception or NotSupportedException)
            {
                journal?.Dispose();
                journal = null;
                store.AddCoverageGap("journal-unavailable", e.Message, DateTimeOffset.Now);
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                DrainPauses();
                if (Paused)
                {
                    Status("已暂停");
                    await Task.Delay(options.JournalInterval ?? TimeSpan.FromSeconds(5), token);
                    continue;
                }

                if (DateTimeOffset.Now >= nextScan || Interlocked.Exchange(ref scanRequested, 0) == 1)
                {
                    var start = DateTimeOffset.Now;
                    // Persist before traversal so process termination cannot cause a restart storm.
                    store.SetNextScanAttemptAt(start + scanInterval);
                    var baseline = !store.ReadCollectorStatus().BaselineReady;
                    Status("核对中");
                    var id = Guid.NewGuid().ToString("N");
                    if (!baseline && journal is not null && state is not null)
                    {
                        try
                        {
                            next = CatchUp(journal, state, next, null, false, token);
                        }
                        catch (Exception e)when (e is Win32Exception or InvalidDataException or NotSupportedException)
                        {
                            DegradeActive(e.Message);
                            store.AddCoverageGap("journal-gap", e.Message, start, DateTimeOffset.Now);
                            journal.Dispose();
                            journal = null;
                        }
                    }

                    bool complete;
                    try
                    {
                        complete = await Scan(id, baseline, token, () =>
                        {
                            if (journal is null || state is null)
                                return true;
                            try
                            {
                                // Scan metadata has already committed. Newer journal/active reads
                                // follow it and share the active scan membership before reconciliation.
                                next = CatchUp(journal, state, next, id, baseline, token);
                                RefreshDirty(journal, token, id, baseline);
                                return true;
                            }
                            catch (Exception exception)when (exception is Win32Exception or InvalidDataException or NotSupportedException)
                            {
                                DegradeActive(exception.Message);
                                store.AddCoverageGap("journal-gap", exception.Message, start, DateTimeOffset.Now);
                                journal.Dispose();
                                journal = null;
                                return false;
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        store.SetNextScanAttemptAt(DateTimeOffset.Now + scanInterval);
                        store.AddCoverageGap("incomplete-scan", "Scan cancelled; unseen files retained", start, DateTimeOffset.Now);
                        throw;
                    }

                    if (journal is not null && state is not null)
                    {
                        try
                        {
                            next = CatchUp(journal, state, next, id, baseline, token);
                        }
                        catch (Exception e)when (e is Win32Exception or InvalidDataException or NotSupportedException)
                        {
                            complete = false;
                            DegradeActive(e.Message);
                            store.AddCoverageGap("journal-gap", e.Message, start, DateTimeOffset.Now);
                            state = journal.Query();
                            next = state.NextUsn;
                        }
                    }

                    // Initial and partial scans remain explicitly covered even after current status becomes ready.
                    store.CompleteScan(id, DateTimeOffset.Now, complete, baseline);
                    if (baseline || !complete)
                        store.AddCoverageGap(baseline ? "initial-baseline" : "incomplete-scan", "Metadata coverage interval; unavailable paths remain unknown", start, DateTimeOffset.Now);
                    Status("运行中", true);
                    store.Prune(DateTimeOffset.Now);
                    // Attempt scheduling is separate from truthful successful completion.
                    // Partial work waits the normal interval, including across restarts.
                    nextScan = DateTimeOffset.Now + scanInterval;
                    store.SetNextScanAttemptAt(nextScan);
                }

                if (journal is not null && state is not null)
                    try
                    {
                        var current = journal.Query();
                        if (current.JournalId != state.JournalId || next < Math.Max(current.FirstUsn, current.LowestValidUsn) || next > current.NextUsn)
                            throw new InvalidDataException("Journal identity or range changed");
                        next = CatchUp(journal, state, next, null, false, token);
                    }
                    catch (Exception e)when (e is Win32Exception or InvalidDataException or NotSupportedException)
                    {
                        DegradeActive(e.Message);
                        store.AddCoverageGap("journal-gap", e.Message, store.ReadCollectorStatus().UpdatedAt ?? DateTimeOffset.Now, DateTimeOffset.Now);
                        try
                        {
                            state = journal.Query();
                            next = state.NextUsn;
                        }
                        catch (Win32Exception)
                        {
                            journal.Dispose();
                            journal = null;
                        }

                        nextScan = DateTimeOffset.MinValue;
                    }

                if (journal is not null)
                    RefreshDirty(journal, token);
                Status("运行中");
                await Task.Delay(options.JournalInterval ?? TimeSpan.FromSeconds(5), token);
            }
        }
        finally
        {
            journal?.Dispose();
        }
    }

    async Task<bool> Scan(string scanId, bool baseline, CancellationToken token, Func<bool> refresh)
    {
        var complete = true;
        var batch = new List<FileObservation>(options.BatchSize);
        var deadline = DateTimeOffset.Now + (options.ScanBudget ?? TimeSpan.FromMinutes(30));
        var refreshAt = DateTimeOffset.Now + (options.JournalInterval ?? TimeSpan.FromSeconds(5));
        foreach (var item in NativeMetadata.Walk(options.FixtureScanRoot ?? options.Volume, e =>
        {
            complete = false;
            failures++;
        }, token))
        {
            if (Paused || DateTimeOffset.Now > deadline)
            {
                complete = false;
                break;
            }

            batch.Add(Scope(item));
            observed++;
            if (batch.Count >= options.BatchSize || DateTimeOffset.Now >= refreshAt)
            {
                store.ApplyBatch(batch, [], DateTimeOffset.Now, true, baseline: baseline, scanId: scanId);
                batch.Clear();
                complete &= refresh();
                refreshAt = DateTimeOffset.Now + (options.JournalInterval ?? TimeSpan.FromSeconds(5));
                await Task.Delay(options.BatchDelay ?? TimeSpan.FromMilliseconds(10), token);
            }
        }

        if (batch.Count > 0)
            store.ApplyBatch(batch, [], DateTimeOffset.Now, true, baseline: baseline, scanId: scanId);
        complete &= refresh();
        return complete;
    }

    FileObservation Scope(FileObservation item) => options.FixtureScanRoot is null ? item : item with
    {
        Paths = item.Paths.Where(p => Accounting.IsWithin(p, options.FixtureScanRoot)).ToArray()
    };
    sealed record ActiveState(string VolumeId, ulong JournalId, ulong[]? References, string? BootSession = null, DateTimeOffset? DegradedSince = null);
    string? activeJson;
    string? activeVolume;
    ulong activeJournal;
    internal static string? ReadBootSession()
    {
        try
        {
            using var system = System.Diagnostics.Process.GetProcessById(4);
            if (system.ProcessName != "System" || system.SessionId != 0)
                return null;
            // Fixed kernel-process creation time, not wall-clock-minus-uptime estimation.
            return "system-process-start:" + system.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception)when (exception is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    void DegradeActive(string message)
    {
        activeDegradedSince ??= DateTimeOffset.Now;
        activeGapAt ??= activeDegradedSince;
        if (activeVolume is not null) PersistActive(activeVolume, activeJournal);
        store.AddCoverageGap("active-set-unavailable", message, DateTimeOffset.Now);
        RequestScan();
    }

    void RecordActiveGap(bool force)
    {
        if (activeDegradedSince is null || activeGapAt is null)
            return;
        var now = DateTimeOffset.Now;
        if (!force && now - activeGapAt < TimeSpan.FromMinutes(1))
            return;
        store.AddCoverageGap("active-set-unavailable", "Open-writer tracking continuity remains degraded", activeGapAt.Value, now);
        activeGapAt = now;
    }

    void RestoreActive(string volume, ulong journal)
    {
        activeVolume = volume;
        activeJournal = journal;
        var path = store.DatabasePath + ".active.json";
        if (!File.Exists(path))
        {
            if (store.ReadCheckpoint()is not null)
                DegradeActive("Active writer state is missing for an existing checkpoint");
            return;
        }

        try
        {
            if (new FileInfo(path).Length > 131072)
                throw new InvalidDataException("Active set file exceeds bound");
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<ActiveState>(json) ?? throw new InvalidDataException("Invalid active set");
            if (string.IsNullOrWhiteSpace(state.VolumeId) || state.References is null || state.References.Length > 4096 || state.DegradedSince > DateTimeOffset.Now || state.BootSession?.Length > 128)
                throw new InvalidDataException("Active set structure is invalid or exceeds bound");
            if (bootSession is not null && state.BootSession is not null && bootSession != state.BootSession)
            {
                if (state.DegradedSince is { } since)
                    store.AddCoverageGap("active-set-recovered", "Verified kernel boot-session change ended prior open-writer lifetime", since, DateTimeOffset.Now);
                // A full kernel session change retires the old writer set. Sleep/resume and
                // application restarts retain the same System-process creation timestamp.
                return;
            }

            if (state.VolumeId == volume && state.JournalId == journal)
            {
                dirty.UnionWith(state.References);
                activeJson = json;
                activeDegradedSince = state.DegradedSince;
                activeGapAt = activeDegradedSince is null ? null : DateTimeOffset.Now;
                if (activeDegradedSince is not null)
                    store.AddCoverageGap("active-set-unavailable", "Open-writer continuity was already degraded before restart", DateTimeOffset.Now);
            }
            else
                DegradeActive("Active writer state volume or journal identity changed");
        }
        catch (Exception e)when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            DegradeActive(e.Message);
        }
    }

    void PersistActive(string volume, ulong journal)
    {
        var json = JsonSerializer.Serialize(new ActiveState(volume, journal, dirty.Order().ToArray(), bootSession, activeDegradedSince));
        if (json == activeJson)
            return;
        var path = store.DatabasePath + ".active.json";
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            file.Write(bytes);
            file.Flush(true);
        }

        File.Move(temporary, path, true);
        activeJson = json;
    }

    void RefreshDirty(UsnJournal journal, CancellationToken token, string? scanId = null, bool baseline = false)
    {
        var batch = new List<FileObservation>(options.BatchSize);
        foreach (var reference in dirty.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (Paused)
                break;
            try
            {
                var item = Scope(observe(journal, reference));
                if (item.Paths.Count > 0)
                    batch.Add(item);
                else
                    dirty.Remove(reference);
            }
            catch (Exception exception)when (exception is Win32Exception or IOException or UnauthorizedAccessException)
            { /* No FILE_DELETE evidence: preserve the index until journal/reconciliation. */
                failures++;
                store.AddCoverageGap("metadata-unavailable", exception.Message, DateTimeOffset.Now);
            }

            if (batch.Count >= options.BatchSize)
            {
                store.ApplyBatch(batch, [], DateTimeOffset.Now, true, baseline: baseline, scanId: scanId);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            store.ApplyBatch(batch, [], DateTimeOffset.Now, true, baseline: baseline, scanId: scanId);
    }

    long CatchUp(UsnJournal journal, JournalState state, long next, string? scanId, bool baseline, CancellationToken token)
    {
        var target = journal.Query().NextUsn;
        // One bounded page coalesces repeated changes; no wait-for-close, so active writers are refreshed each poll.
        while (next < target)
        {
            token.ThrowIfCancellationRequested();
            if (Paused)
                break;
            var page = journal.Read(next, state.JournalId);
            if (page.NextUsn <= next)
                break;
            var known = new List<FileObservation>();
            var created = new List<FileObservation>();
            var deleted = new List<string>();
            var creationEvidence = new List<string>();
            foreach (var group in page.Records.GroupBy(r => r.FileReference))
            {
                token.ThrowIfCancellationRequested();
                var reason = group.Aggregate(0u, (value, r) => value | r.Reason);
                // CLOSE summaries repeat previously accumulated FILE_CREATE flags. Only
                // a non-close creation record establishes fresh creation provenance.
                if (group.Any(record => (record.Reason & 0x80000100) == 0x100 && CreationIsInScope(journal, record)))
                    creationEvidence.Add(journal.FileId(group.Key));
                try
                {
                    var item = Scope(observe(journal, group.Key));
                    if (item.Paths.Count == 0)
                        continue;
                    ((reason & 0x100) != 0 ? created : known).Add(item);
                    observed++;
                    if ((group.Last().Reason & 0x80000000) != 0)
                        dirty.Remove(group.Key);
                    else if (dirty.Contains(group.Key) || dirty.Count < 4096)
                        dirty.Add(group.Key);
                    else
                    {
                        store.AddCoverageGap("dirty-overflow", "Active writer set exceeded bound; scan requested", DateTimeOffset.Now);
                        DegradeActive("Active writer set exceeded bound");
                    }
                }
                catch (Win32Exception e)
                {
                    if ((reason & 0x200) != 0 && e.NativeErrorCode is 2 or 3 or 87)
                    {
                        deleted.Add(journal.FileId(group.Key));
                        dirty.Remove(group.Key);
                    }
                    else if (options.FixtureScanRoot is null && e.NativeErrorCode is not (2 or 3))
                    {
                        failures++;
                        store.AddCoverageGap("metadata-unavailable", e.Message, DateTimeOffset.Now);
                    }
                }
                catch (Exception exception)when (exception is IOException or UnauthorizedAccessException)
                {
                    failures++;
                    store.AddCoverageGap("metadata-unavailable", exception.Message, DateTimeOffset.Now);
                    // Retain a bounded retry candidate; an observation race is not deletion evidence.
                    if (dirty.Contains(group.Key) || dirty.Count < 4096)
                        dirty.Add(group.Key);
                    else
                        DegradeActive("Active writer retry set exceeded bound");
                }
            }

            PersistActive(journal.VolumeId, state.JournalId);
            var at = DateTimeOffset.Now;
            store.ApplyJournalBatch(known.Concat(created).ToArray(), deleted, creationEvidence, at, new(journal.VolumeId, state.JournalId.ToString(), page.NextUsn), baseline, scanId);
            next = page.NextUsn;
        }

        if (scanId is not null)
            store.ApplyBatch([], [], DateTimeOffset.Now, new JournalCheckpoint(journal.VolumeId, state.JournalId.ToString(), next));
        return next;
    }

    bool CreationIsInScope(UsnJournal journal, UsnRecord record)
    {
        if (options.FixtureScanRoot is null)
            return true;
        // Proof-only rows must obey the same fixture boundary as readable observations.
        // Parent metadata can scope creation even when the new object's first read races.
        try
        {
            var parent = journal.Resolve(record.ParentReference);
            return parent.Paths.Any(path => Accounting.IsWithin(Path.Combine(path, record.Name), options.FixtureScanRoot));
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
