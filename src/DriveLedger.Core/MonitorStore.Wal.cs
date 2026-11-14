using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace DriveLedger.Core;

public sealed partial class MonitorStore
{
    // Preserve the auxiliary WAL/SHM after the last writer closes so the ordinary
    // read-only viewer never needs directory write permission to open a stopped service's history.
    private static void PersistWal(SqliteConnection connection)
    {
        var setting = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(setting, 1);
            var result = FileControl(connection.Handle!, "main", 10, setting);
            if (result != 0) throw new SqliteException("Unable to retain WAL files for read-only viewing.", result);
        }
        finally { Marshal.FreeHGlobal(setting); }
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FileControl(SafeHandle database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int operation, IntPtr argument);
}
