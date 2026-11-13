// Interim: from teams-channel-notes-dotnet (Interim/RealPath.cs): Python's Path.resolve(). Belongs in PyCompat beside PyPath.
using System.Runtime.InteropServices;

using PyCompat;

namespace MeetingPreps.Interim;

/// <summary>pathlib's Path.resolve() on Windows (ntpath.realpath, not strict): made absolute, ".." collapsed,
/// then links followed and the case corrected to what's on disk, through GetFinalPathNameByHandle. A path that
/// doesn't exist yet resolves its longest existing part and keeps the rest as given. The text matters: it's the
/// state file's keys, the hook's paths and the lock names.</summary>
public static partial class RealPath
{
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(nint file, char* buffer, uint size, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static string Resolve(string path)
    {
        var full = PyPath.Pure(Path.GetFullPath(path));
        var tail = new List<string>();
        var current = full;
        while (true)
        {
            var final = Final(current);
            if (final is not null)
            {
                tail.Reverse();
                return PyPath.Pure(tail.Count == 0 ? final : Path.Combine([final, .. tail]));
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                return full;
            tail.Add(Path.GetFileName(current));
            current = parent;
        }
    }

    /// <summary>_getfinalpathname without the \\?\ prefix, or null if the path can't be opened.</summary>
    private static unsafe string? Final(string path)
    {
        var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle == -1 || handle == 0)
            return null;
        try
        {
            var buffer = new char[1024];
            while (true)
            {
                uint length;
                fixed (char* p = buffer)
                    length = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
                if (length == 0)
                    return null;
                if (length < buffer.Length)
                    return StripPrefix(new string(buffer, 0, (int)length));
                buffer = new char[length + 1];
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }
}
