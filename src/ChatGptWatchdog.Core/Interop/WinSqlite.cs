using System.Runtime.InteropServices;

namespace ChatGptWatchdog.Core.Interop;

/// <summary>
/// Minimal read-only access to SQLite through winsqlite3.dll, which ships with Windows 10/11.
/// Used only for optional lookups; every failure just returns an empty result.
/// </summary>
internal static class WinSqlite
{
    private const string Dll = "winsqlite3.dll";
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READONLY = 0x00000001;
    private const int SQLITE_OPEN_URI = 0x00000040;

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    private static byte[] Utf8Z(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Runs a query and returns the first column of every row as strings.</summary>
    public static List<string> QueryColumn(string dbPath, string sql, out string? error)
    {
        error = null;
        var result = new List<string>();
        if (!File.Exists(dbPath)) { error = "database not found"; return result; }
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try
        {
            var uri = "file:" + dbPath.Replace('\\', '/') + "?mode=ro";
            var rc = sqlite3_open_v2(Utf8Z(uri), out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero);
            if (rc != SQLITE_OK) { error = $"open failed ({rc})"; return result; }
            sqlite3_busy_timeout(db, 2000);
            rc = sqlite3_prepare_v2(db, Utf8Z(sql), -1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) { error = $"prepare failed ({rc})"; return result; }
            while (sqlite3_step(stmt) == SQLITE_ROW)
            {
                var p = sqlite3_column_text(stmt, 0);
                if (p != IntPtr.Zero) result.Add(Marshal.PtrToStringUTF8(p) ?? "");
            }
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
        return result;
    }
}
