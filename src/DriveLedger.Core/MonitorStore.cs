using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace DriveLedger.Core;

public sealed partial class MonitorStore
{
    private readonly string connectionString;
    private readonly bool readOnly;
    public string DatabasePath { get; }
    // At is the last successful allocation read, independent of path observations.
    private sealed record IndexedFile(FileObservation Observation, DateTimeOffset? At, DateTimeOffset? PathObservedAt = null);
    public MonitorStore(string databasePath, bool readOnly = false)
    {
        DatabasePath = Path.GetFullPath(databasePath); this.readOnly = readOnly;
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30, Pooling = true }.ToString();
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        if (!readOnly) PersistWal(c);
        // SQLite NOCASE folds ASCII only. Match the path rules used by Accounting exactly,
        // including Unicode case pairs, on every pooled/read-only connection as well.
        c.CreateCollation("DRIVELEDGER_PATH",StringComparer.OrdinalIgnoreCase.Compare);
        Execute(c, null, "PRAGMA busy_timeout=30000");
        return c;
    }
    private void Writable() { if (readOnly) throw new InvalidOperationException("Database is read-only."); }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value); return cmd;
    }
    private static int Execute(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args) { using var cmd = Command(c, t, sql, args); return cmd.ExecuteNonQuery(); }
    private static string? Meta(SqliteConnection c, SqliteTransaction? t, string key) { using var cmd = Command(c, t, "SELECT value FROM meta WHERE key=$0", key); return cmd.ExecuteScalar() as string; }
    private static void SetMeta(SqliteConnection c, SqliteTransaction? t, string key, string value) => Execute(c, t, "INSERT INTO meta VALUES($0,$1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    private static T? Decode<T>(string? value) => value is null ? default : JsonSerializer.Deserialize<T>(value);
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value);
    public void Initialize()
    {
        if (readOnly) { using var read = Open(); using var check = Command(read, null, "SELECT value FROM meta LIMIT 1"); check.ExecuteScalar(); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); using var c = Open();
        Execute(c, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        Execute(c, null, """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY,payload TEXT NOT NULL,scan TEXT);
            CREATE TABLE IF NOT EXISTS creation_provenance(id TEXT PRIMARY KEY,payload TEXT NOT NULL,seen INTEGER NOT NULL,proven INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY,at INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS events_time ON events(at);
            CREATE TABLE IF NOT EXISTS directory_deltas(at INTEGER NOT NULL,day TEXT NOT NULL,path TEXT COLLATE NOCASE NOT NULL,added INTEGER NOT NULL,released INTEGER NOT NULL,moved_in INTEGER NOT NULL,moved_out INTEGER NOT NULL,PRIMARY KEY(at,path));
            CREATE INDEX IF NOT EXISTS directory_time ON directory_deltas(at);
            CREATE TABLE IF NOT EXISTS samples(at INTEGER NOT NULL,volume TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(at,volume));
            CREATE TABLE IF NOT EXISTS coverage(id INTEGER PRIMARY KEY,start INTEGER NOT NULL,end INTEGER,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS volume_deltas(at INTEGER PRIMARY KEY,added INTEGER NOT NULL,released INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS file_paths(path TEXT COLLATE NOCASE NOT NULL,id TEXT NOT NULL,PRIMARY KEY(path,id));
            CREATE INDEX IF NOT EXISTS file_paths_id ON file_paths(id);
            CREATE INDEX IF NOT EXISTS file_paths_ordinal ON file_paths(path COLLATE DRIVELEDGER_PATH,id);
            """);
        using(var t=c.BeginTransaction())
        {
            if(Meta(c,t,"volumeTotalsV1") is null)
            {
                Execute(c,t,"INSERT INTO volume_deltas SELECT at,SUM(added),SUM(released) FROM directory_deltas WHERE length(path)=3 GROUP BY at");
                Execute(c,t,"""
                    INSERT INTO volume_deltas SELECT at,
                    SUM(CASE WHEN json_extract(payload,'$.Path') IS NULL THEN json_extract(payload,'$.AddedBytes') ELSE 0 END),
                    SUM(CASE WHEN json_extract(payload,'$.OldPath') IS NULL THEN json_extract(payload,'$.ReleasedBytes') ELSE 0 END)
                    FROM events GROUP BY at
                    ON CONFLICT(at) DO UPDATE SET added=added+excluded.added,released=released+excluded.released
                    """);
                SetMeta(c,t,"volumeTotalsV1","1");
            }
            if(Meta(c,t,"pathIndexV1") is null)
            {
                Execute(c,t,"INSERT OR IGNORE INTO file_paths SELECT value,files.id FROM files,json_each(files.payload,'$.Observation.Paths')");
                SetMeta(c,t,"pathIndexV1","1");
            }
            // Legacy unbounded automatic observations were point failures, not ongoing intervals.
            Execute(c,t,"""
                UPDATE coverage SET end=start,payload=json_set(payload,'$.End',json_extract(payload,'$.Start'))
                WHERE end IS NULL AND json_extract(payload,'$.Code') IN ('allocation-unavailable','incomplete-scan')
                """);
            t.Commit();
        }
        InitializeSamples(c);
    }
    private static IndexedFile? Find(SqliteConnection c, SqliteTransaction t, string id) { using var cmd = Command(c, t, "SELECT payload FROM files WHERE id=$0", id); return Decode<IndexedFile>(cmd.ExecuteScalar() as string); }
    private static List<(string Id, IndexedFile File)> FilePage(SqliteConnection c, SqliteTransaction t, string afterId, string? unseenScan = null, string? subtree = null)
    {
        using var cmd = subtree is not null
            ? Command(c,t,"SELECT id,payload FROM files WHERE id>$0 AND id IN (SELECT id FROM file_paths WHERE path >= $1 COLLATE DRIVELEDGER_PATH AND path < $2 COLLATE DRIVELEDGER_PATH) ORDER BY id LIMIT 256",afterId,subtree.TrimEnd('\\')+"\\",subtree.TrimEnd('\\')+"]")
            : Command(c,t,"SELECT id,payload FROM files WHERE id>$0 AND (scan IS NULL OR scan<>$1) ORDER BY id LIMIT 256",afterId,unseenScan);
        using var r = cmd.ExecuteReader(); var result = new List<(string, IndexedFile)>(); while (r.Read()) result.Add((r.GetString(0), Decode<IndexedFile>(r.GetString(1))!)); return result;
    }
    private static void Save(SqliteConnection c, SqliteTransaction t, IndexedFile f, string? scan)
    {
        Execute(c,t,"INSERT INTO files VALUES($0,$1,$2) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,scan=COALESCE(excluded.scan,files.scan)",f.Observation.FileId,Encode(f),scan);
        Execute(c,t,"DELETE FROM file_paths WHERE id=$0",f.Observation.FileId);
        foreach(var path in f.Observation.Paths)Execute(c,t,"INSERT INTO file_paths VALUES($0,$1)",path,f.Observation.FileId);
    }
    public void ApplyBatch(IReadOnlyList<FileObservation> observations, IReadOnlyList<string> deletedIds, DateTimeOffset at, JournalCheckpoint? checkpoint = null, bool baseline = false, string? scanId = null)
        => ApplyBatch(observations, deletedIds, at, false, checkpoint, baseline, scanId);
    public void ApplyJournalBatch(IReadOnlyList<FileObservation> observations, IReadOnlyList<string> deletedIds, IReadOnlyList<string> createdIds, DateTimeOffset at, JournalCheckpoint checkpoint, bool baseline = false, string? scanId = null)
        => ApplyBatchCore(observations, deletedIds, at, true, checkpoint, baseline, scanId, createdIds.ToHashSet(StringComparer.Ordinal));
    public void ApplyBatch(IReadOnlyList<FileObservation> observations, IReadOnlyList<string> deletedIds, DateTimeOffset at, bool unknownAsBaseline, JournalCheckpoint? checkpoint = null, bool baseline = false, string? scanId = null)
        => ApplyBatchCore(observations, deletedIds, at, unknownAsBaseline, checkpoint, baseline, scanId, null);
    private void ApplyBatchCore(IReadOnlyList<FileObservation> observations, IReadOnlyList<string> deletedIds, DateTimeOffset at, bool unknownAsBaseline, JournalCheckpoint? checkpoint, bool baseline, string? scanId, HashSet<string>? createdIds)
    {
        Writable(); using var c = Open(); using var t = c.BeginTransaction(); var previous = Decode<JournalCheckpoint>(Meta(c, t, "checkpoint"));
        if (checkpoint is not null && previous is not null && checkpoint.VolumeId == previous.VolumeId && checkpoint.JournalId == previous.JournalId && checkpoint.NextUsn <= previous.NextUsn) return;
        if (!baseline && createdIds is not null)
        {
            var readableNow = observations.Where(item => item.AllocatedBytes.HasValue).Select(item => item.FileId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in createdIds)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                // A creation record remains evidence even when the native allocation read failed.
                // A compact consumed receipt also prevents later repeated proof from rearming it.
                var awaitingBasis = Find(c,t,id) is null && !readableNow.Contains(id);
                var inserted = Execute(c,t,"INSERT OR IGNORE INTO creation_provenance(id,payload,seen,proven) VALUES($0,$1,$2,$3)",id,EmptyCreationBasis(id),at.ToUnixTimeMilliseconds(),awaitingBasis?1:2);
                if(inserted>0 && awaitingBasis)
                    Gap(c,t,"creation-allocation-unavailable",$"Creation observed; allocation not yet known: {id}",at,at);
                Execute(c,t,"UPDATE creation_provenance SET proven=1 WHERE id=$0 AND proven=0",id);
                ConfirmCreation(c,t,id,at);
            }
        }
        foreach (var raw in observations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(raw.FileId); if (raw.AllocatedBytes < 0 || raw.LogicalBytes < 0) throw new ArgumentException("Negative file size.");
            var item = raw with { Paths = Accounting.NormalizePaths(raw.Paths) }; var old = Find(c, t, item.FileId);
            if (item.AllocatedBytes is null) { Gap(c, t, "allocation-unavailable", $"Allocation unavailable: {item.FileId}", at, at); item = item with { AllocatedBytes = old?.Observation.AllocatedBytes }; }
            item = item with { LogicalBytes = item.LogicalBytes ?? old?.Observation.LogicalBytes };
            if (item.Paths.Count == 0 && old is not null) item = item with { Paths = old.Observation.Paths };
            if(item.Paths.Count==0)Gap(c,t,"path-unavailable",$"Path attribution unavailable: {item.FileId}",at,at);
            var oldPath = old?.Observation.Paths.FirstOrDefault(); var path = item.Paths.FirstOrDefault();
            if (item.IsDirectory && oldPath is not null && path is not null && !oldPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                var cursor="";
                while(true)
                {
                    var page=FilePage(c,t,cursor,subtree:oldPath);if(page.Count==0)break;cursor=page[^1].Id;
                    foreach(var(id,child)in page)
                    {
                        if(id==item.FileId)continue;
                        var paths=child.Observation.Paths.Select(p=>Accounting.IsWithin(p,oldPath)?path+p[oldPath.Length..]:p).ToArray();
                        var moved=child.Observation with{Paths=Accounting.NormalizePaths(paths)};
                        if(!baseline)Change(c,t,child.Observation,moved,at,"migration",child.At);
                        Save(c,t,child with{Observation=moved,PathObservedAt=at},null);
                    }
                }
            }
            var firstKnowledge = unknownAsBaseline && old is null && createdIds?.Contains(item.FileId) != true;
            if (firstKnowledge && !baseline)
            {
                Gap(c,t,"first-knowledge",$"Prior allocation unknown: {item.FileId}",at,at);
                Execute(c,t,"INSERT OR IGNORE INTO creation_provenance(id,payload,seen) VALUES($0,$1,$2)",item.FileId,Encode(new IndexedFile(item,raw.AllocatedBytes.HasValue?at:null,at)),at.ToUnixTimeMilliseconds());
            }
            var awaitingCreationBasis = false;
            if(old is null)
            {
                using var pending=Command(c,t,"SELECT 1 FROM creation_provenance WHERE id=$0 AND proven=1 AND json_extract(payload,'$.Observation.AllocatedBytes') IS NULL",item.FileId);
                awaitingCreationBasis=pending.ExecuteScalar() is not null;
            }
            if (!baseline && !firstKnowledge && !awaitingCreationBasis) Change(c, t, old?.Observation, item, at, old is null ? "created" : "observed", old?.At);
            if (!baseline && raw.AllocatedBytes.HasValue)
            {
                // Preserve the earliest readable allocation; a previous unknown is never zero.
                Execute(c,t,"UPDATE creation_provenance SET payload=$1 WHERE id=$0 AND proven<>2 AND json_extract(payload,'$.Observation.AllocatedBytes') IS NULL",item.FileId,Encode(new IndexedFile(item,at,at)));
                ConfirmCreation(c,t,item.FileId,at);
            }
            Save(c,t,new(item,raw.AllocatedBytes.HasValue?at:old?.At,at),scanId);
        }
        foreach (var id in deletedIds) Delete(c, t, id, at, baseline);
        if (checkpoint is not null) SetMeta(c, t, "checkpoint", Encode(checkpoint)); t.Commit();
    }
    private static string EmptyCreationBasis(string id) => Encode(new IndexedFile(new FileObservation(id,[],null,null),null));
    private static void ConfirmCreation(SqliteConnection c, SqliteTransaction t, string id, DateTimeOffset at)
    {
        using var command=Command(c,t,"SELECT payload FROM creation_provenance WHERE id=$0 AND proven=1",id);
        var first=Decode<IndexedFile>(command.ExecuteScalar() as string);
        if(first?.Observation.AllocatedBytes is null)return;
        Change(c,t,null,first.Observation,at,"creation-confirmed",first.At);
        Gap(c,t,"creation-confirmed",$"Creation evidence resolved allocation first observed at {first.At:O}: {id}",at,at);
        Execute(c,t,"UPDATE creation_provenance SET proven=2,payload=$1 WHERE id=$0",id,EmptyCreationBasis(id));
    }
    private static void Delete(SqliteConnection c, SqliteTransaction t, string id, DateTimeOffset at, bool baseline)
    {
        var old = Find(c, t, id); if (old is null) return; if (!baseline) Change(c, t, old.Observation, null, at, "deleted", old.At); Execute(c, t, "DELETE FROM files WHERE id=$0", id);Execute(c,t,"DELETE FROM file_paths WHERE id=$0",id);
    }
    private static void Change(SqliteConnection c, SqliteTransaction t, FileObservation? old, FileObservation? current, DateTimeOffset at, string kind, DateTimeOffset? observedFrom = null)
    {
        long after = current?.AllocatedBytes ?? 0;
        // A newly available size establishes knowledge; its unknown preceding size is not zero.
        long before = old is null ? 0 : old.AllocatedBytes ?? after;
        var added = Math.Max(0, after - before); var released = Math.Max(0, before - after);
        var oldPath = old?.Paths.FirstOrDefault(); var path = current?.Paths.FirstOrDefault();
        var moved = old is not null && current is not null && !string.Equals(oldPath,path,StringComparison.OrdinalIgnoreCase);
        if (added == 0 && released == 0 && !moved) return; var migration = moved ? Math.Min(before, after) : 0;
        var change = new FileChange((current ?? old)!.FileId, oldPath, path, at, added, released, migration, current?.AllocatedBytes, current?.LogicalBytes, moved ? "migration" : kind, (current ?? old)!.Paths, observedFrom);
        Execute(c, t, "INSERT INTO events(at,payload) VALUES($0,$1)", at.ToUnixTimeMilliseconds(), Encode(change));
        Execute(c,t,"INSERT INTO volume_deltas VALUES($0,$1,$2) ON CONFLICT(at) DO UPDATE SET added=added+excluded.added,released=released+excluded.released",at.ToUnixTimeMilliseconds(),added,released);
        var oldAnc = Accounting.Ancestors(oldPath).ToHashSet(StringComparer.OrdinalIgnoreCase); var newAnc = Accounting.Ancestors(path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in oldAnc.Union(newAnc, StringComparer.OrdinalIgnoreCase))
        {
            var was = oldAnc.Contains(folder); var now = newAnc.Contains(folder); var a = now ? added : 0; var r = was ? released : 0; var mi = now && !was ? migration : 0; var mo = was && !now ? migration : 0;
            if (a == 0 && r == 0 && mi == 0 && mo == 0) continue;
            Execute(c, t, """
                INSERT INTO directory_deltas VALUES($0,$1,$2,$3,$4,$5,$6)
                ON CONFLICT(at,path) DO UPDATE SET added=added+excluded.added,released=released+excluded.released,moved_in=moved_in+excluded.moved_in,moved_out=moved_out+excluded.moved_out
                """, at.ToUnixTimeMilliseconds(), at.ToString("yyyy-MM-ddzzz"), folder, a, r, mi, mo);
        }
    }
    public void CompleteScan(string scanId, DateTimeOffset at, bool canInferDeletions, bool baseline)
    {
        Writable(); using var c = Open(); using var t = c.BeginTransaction();
        if(canInferDeletions)
        {
            var cursor="";
            while(true){var page=FilePage(c,t,cursor,scanId);if(page.Count==0)break;cursor=page[^1].Id;foreach(var(id,_)in page)Delete(c,t,id,at,baseline);}
        }
        else Gap(c, t, "incomplete-scan", "Unseen files retained; scan did not cover all paths.", at, at);
        if (canInferDeletions) SetMeta(c, t, "lastCompletedScan", Encode(at));
        if (baseline && canInferDeletions) { var status = Decode<CollectorStatus>(Meta(c, t, "status")) ?? new(); SetMeta(c, t, "status", Encode(status with { BaselineReady = true, UpdatedAt = at })); }
        t.Commit();
    }
    public DateTimeOffset? ReadNextScanAttemptAt() { using var c = Open(); return Decode<DateTimeOffset?>(Meta(c, null, "nextScanAttemptAt")); }
    public void SetNextScanAttemptAt(DateTimeOffset at) { Writable(); using var c = Open(); SetMeta(c, null, "nextScanAttemptAt", Encode(at)); }
    public DateTimeOffset? ReadLastCompletedScan() { using var c = Open(); return Decode<DateTimeOffset?>(Meta(c, null, "lastCompletedScan")); }
    public JournalCheckpoint? ReadCheckpoint() { using var c = Open(); return Decode<JournalCheckpoint>(Meta(c, null, "checkpoint")); }
    public void RecordDiskSample(DiskSample sample)
    {
        Writable(); if (sample.TotalBytes < 0 || sample.FreeBytes < 0 || sample.FreeBytes > sample.TotalBytes) throw new ArgumentException("Invalid disk sample.");
        using var c = OpenSamples(); Execute(c, null, "INSERT OR REPLACE INTO samples VALUES($0,$1,$2)", sample.At.ToUnixTimeMilliseconds(), sample.VolumeId, Encode(sample));
    }
    private static void Gap(SqliteConnection c, SqliteTransaction? t, string code, string message, DateTimeOffset start, DateTimeOffset? end) => Execute(c, t,
        "INSERT INTO coverage(start,end,payload) VALUES($0,$1,$2)", start.ToUnixTimeMilliseconds(), end?.ToUnixTimeMilliseconds(), Encode(new CoverageNote(code, message, start, end)));
    public void AddCoverageGap(string code, string message, DateTimeOffset start, DateTimeOffset? end = null) { Writable(); using var c = Open(); Gap(c, null, code, message, start, end??start); }
    public void SetCollectorStatus(CollectorStatus status) { Writable(); using var c = Open(); SetMeta(c, null, "status", Encode(status)); }
    public CollectorStatus ReadCollectorStatus() { using var c = Open(); return Decode<CollectorStatus>(Meta(c, null, "status")) ?? new(); }
    public void Prune(DateTimeOffset now)
    {
        Writable(); using var c = Open(); using var t = c.BeginTransaction(); var cutoff = now.AddDays(-30);
        Execute(c, t, "DELETE FROM events WHERE at<$0", cutoff.ToUnixTimeMilliseconds());
        Execute(c, t, "DELETE FROM creation_provenance WHERE seen<$0", now.AddDays(-365).ToUnixTimeMilliseconds());
        Execute(c, t, "DELETE FROM directory_deltas WHERE at<$0", now.AddDays(-365).ToUnixTimeMilliseconds());
        Execute(c,t,"DELETE FROM volume_deltas WHERE at<$0",now.AddDays(-365).ToUnixTimeMilliseconds());
        Execute(c, t, "DELETE FROM samples WHERE at<$0", now.AddDays(-365).ToUnixTimeMilliseconds());
        Execute(c, t, "DELETE FROM coverage WHERE COALESCE(end,start)<$0", now.AddDays(-365).ToUnixTimeMilliseconds());
        var prior = Decode<DateTimeOffset?>(Meta(c, t, "detailsFrom")); SetMeta(c, t, "detailsFrom", Encode(prior > cutoff ? prior : cutoff)); t.Commit();
        using var disk=OpenSamples();Execute(disk,null,"DELETE FROM samples WHERE at<$0",now.AddDays(-365).ToUnixTimeMilliseconds());
    }
}
