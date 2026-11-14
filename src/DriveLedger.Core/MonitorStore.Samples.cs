using Microsoft.Data.Sqlite;
namespace DriveLedger.Core;

public sealed partial class MonitorStore
{
    public string DiskSampleDatabasePath => DatabasePath + ".samples.db";

    private SqliteConnection OpenSamples(bool queryOnly=false)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource=DiskSampleDatabasePath,
            Mode=readOnly || queryOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout=30,Pooling=true
        }.ToString());
        c.Open();
        if (!readOnly && !queryOnly) PersistWal(c);
        Execute(c,null,"PRAGMA busy_timeout=30000");return c;
    }

    private void InitializeSamples(SqliteConnection main)
    {
        using var disk=OpenSamples();
        Execute(disk,null,"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        Execute(disk,null,"CREATE TABLE IF NOT EXISTS samples(at INTEGER NOT NULL,volume TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(at,volume))");
        // Copy first, then clear legacy rows. A restart between these operations safely replays INSERT OR IGNORE.
        Execute(disk,null,"ATTACH DATABASE $0 AS legacy",DatabasePath);
        Execute(disk,null,"INSERT OR IGNORE INTO samples SELECT * FROM legacy.samples");
        Execute(disk,null,"DETACH DATABASE legacy");
        Execute(main,null,"DELETE FROM samples");
    }
}
