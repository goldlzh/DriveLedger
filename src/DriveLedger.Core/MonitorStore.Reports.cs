namespace DriveLedger.Core;

public sealed partial class MonitorStore
{
    public ReportSnapshot QueryReport(DateTimeOffset from, DateTimeOffset to, string? folder = null)
    {
        if (to < from) throw new ArgumentException("End precedes start."); folder = folder is null ? null : Accounting.NormalizePath(folder);
        using var c = Open(); using var t = c.BeginTransaction(deferred: true); var samples = new List<DiskSample>();
        using var disk=File.Exists(DiskSampleDatabasePath)?OpenSamples(queryOnly:true):null;
        using var diskTransaction=disk?.BeginTransaction(deferred:true);
        using (var cmd = Command(disk??c, diskTransaction??t, "SELECT payload FROM samples WHERE at >= $0 AND at <= $1 ORDER BY at", from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds()))
        using (var r = cmd.ExecuteReader()) while (r.Read()) samples.Add(Decode<DiskSample>(r.GetString(0))!);
        var notes = new List<CoverageNote>();
        if(disk is null)notes.Add(new("samples-unavailable","Disk sample database is missing; available legacy samples are shown without creating replacement history.",from,to));
        using (var cmd = Command(c, t, "SELECT payload FROM coverage WHERE start<=$1 AND (end IS NULL OR end >= $0)", from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds()))
        using (var r = cmd.ExecuteReader()) while (r.Read()) notes.Add(Decode<CoverageNote>(r.GetString(0))!);
        for (var i = 1; i < samples.Count; i++) if (samples[i].At - samples[i - 1].At > TimeSpan.FromMinutes(2)) notes.Add(new("sample-gap", "No disk observations inside this interval; change has not been distributed across days.", samples[i - 1].At, samples[i].At));
        var first = samples.FirstOrDefault(); var last = samples.LastOrDefault();
        long? delta = samples.Count >= 2 && samples.All(x => x.VolumeId == first!.VolumeId) ? (last!.TotalBytes - last.FreeBytes) - (first!.TotalBytes - first.FreeBytes) : null;
        if (samples.Select(x => x.VolumeId).Distinct().Skip(1).Any()) notes.Add(new("volume-change", "Disk delta unavailable across volume identities.", from, to));
        var attributedFrom = delta is null ? from : first!.At; var attributedTo = delta is null ? to : last!.At;
        if (attributedFrom != from || attributedTo != to) notes.Add(new("observation-window", "Attribution is restricted to actual disk endpoints.", attributedFrom, attributedTo));
        var folders = new List<FolderChange>();
        using (var cmd = Command(c, t, """
            SELECT path,SUM(added),SUM(released),SUM(moved_in),SUM(moved_out) FROM directory_deltas
            WHERE at>$0 AND at<=$1 GROUP BY path ORDER BY SUM(added)-SUM(released)+SUM(moved_in)-SUM(moved_out) DESC
            """, attributedFrom.ToUnixTimeMilliseconds(), attributedTo.ToUnixTimeMilliseconds()))
        using (var r = cmd.ExecuteReader()) while (r.Read()) folders.Add(new(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        long added,released;
        using(var cmd=Command(c,t,"SELECT COALESCE(SUM(added),0),COALESCE(SUM(released),0) FROM volume_deltas WHERE at>$0 AND at<=$1",attributedFrom.ToUnixTimeMilliseconds(),attributedTo.ToUnixTimeMilliseconds()))
        using(var r=cmd.ExecuteReader()){r.Read();added=r.GetInt64(0);released=r.GetInt64(1);}
        var files = new List<FileChange>();
        using (var cmd = Command(c, t, "SELECT payload FROM events WHERE at>$0 AND at<=$1 ORDER BY at,id", attributedFrom.ToUnixTimeMilliseconds(), attributedTo.ToUnixTimeMilliseconds()))
        using (var r = cmd.ExecuteReader()) while (r.Read()) files.Add(Decode<FileChange>(r.GetString(0))!);
        if (folder is not null) { folders = folders.Where(x => Accounting.IsWithin(x.Path, folder)).ToList(); files = files.Where(x => (x.Path is not null && Accounting.IsWithin(x.Path, folder)) || (x.OldPath is not null && Accounting.IsWithin(x.OldPath, folder))).ToList(); }
        var cutoff = Decode<DateTimeOffset?>(Meta(c, t, "detailsFrom")); var status = Decode<CollectorStatus>(Meta(c, t, "status")) ?? new();
        if (!status.BaselineReady) notes.Add(new("baseline-incomplete", "File attribution baseline is not ready.", from, to));
        var result = new ReportSnapshot(from, to, first, last, delta, added, released, folders, files, samples, notes, cutoff is null || attributedFrom >= cutoff, cutoff, status);diskTransaction?.Commit(); t.Commit(); return result;
    }
}
