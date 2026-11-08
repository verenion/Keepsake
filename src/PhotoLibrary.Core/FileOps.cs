using System.Runtime.InteropServices;

namespace PhotoLibrary.Core;

/// <summary>Deleting files the way a user would expect: into the Recycle Bin on Windows, so a mistake is recoverable.</summary>
public static class FileOps
{
    /// <summary>
    /// Sends the files to the Recycle Bin (Windows) or deletes them (elsewhere). Returns the paths that could not be
    /// removed. Paths that no longer exist count as done.
    /// </summary>
    public static List<string> Recycle(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0) return [];
        if (OperatingSystem.IsWindows())
        {
            // One shell operation for the whole batch: far faster than per-file calls and a single undo entry.
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", existing) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            var rc = SHFileOperation(ref op);
            if (rc == 0 && !op.fAnyOperationsAborted) return [];
            return existing.Where(File.Exists).ToList();
        }
        var failed = new List<string>();
        foreach (var p in existing)
        {
            try { File.Delete(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(p); }
        }
        return failed;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    // Natural alignment: SHFILEOPSTRUCT is only byte-packed on 32-bit Windows, and the app is x64 only.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
