using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitorInputSwitcher;

internal sealed record LanFileEntry(string Name, long Length, long Modified, int ZoneLength);
internal sealed record LanSourceEntry(LanFileEntry Entry, string Path, string Id);
internal sealed class LanFileFailure(string code) : IOException(code)
{
    public string Code { get; } = code;
}

internal static class LanFileTree
{
    public const long Limit = 10L * 1024 * 1024 * 1024;
    public const int EntryLimit = 10_000;

    public static bool IsRelativeName(string name) => name is { Length: > 0 and <= 1024 } &&
        !name.Contains('\\') && name.Split('/').All(part => part.Length is > 0 and <= 255 &&
            part is not "." and not ".." && !part.EndsWith('.') && !part.EndsWith(' ') &&
            part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !Reserved(part.Split('.')[0].TrimEnd()));

    private static bool Reserved(string value)
    {
        string name = value.ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
            name.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(name[3]);
    }

    public static void VerifyLocalPath(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (!Path.IsPathFullyQualified(path) || root is not { Length: 3 } || root[1] != ':' ||
            new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new LanFileFailure("local-path");

        for (string? component = Path.GetFullPath(path); component is not null; component = Path.GetDirectoryName(component))
        {
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                throw new LanFileFailure("reparse-point");
        }
    }

    public static IDisposable PinDirectory(string path)
    {
        VerifyLocalPath(path);
        var ancestors = new Stack<string>();
        for (string? part = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            part is not null && !Path.EndsInDirectorySeparator(part);
            part = Path.GetDirectoryName(part))
            ancestors.Push(part);

        var pin = new DirectoryPin();
        try
        {
            foreach (string ancestor in ancestors)
            {
                var handle = CreateFileW(ancestor, 0, 3, 0, 3, 0x02200000, 0);
                pin.Handles.Add(handle);
                if (handle.IsInvalid) throw new LanFileFailure("destination-changed");
                var attributes = File.GetAttributes(ancestor);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new LanFileFailure("destination-changed");
            }
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }

    private sealed class DirectoryPin : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = [];
        public void Dispose()
        {
            foreach (var handle in Handles) handle.Dispose();
        }
    }

    public static LanSourceEntry[] Scan(string[] roots)
    {
        var entries = new List<LanSourceEntry>();
        long bytes = 0;
        foreach (string root in roots)
        {
            VerifyLocalPath(root);
            Add(root, Path.GetFileName(root));
        }
        Validate(entries.Select(item => item.Entry).ToArray());
        return entries.ToArray();

        void Add(string path, string relative)
        {
            if (entries.Count >= EntryLimit || !IsRelativeName(relative))
                throw new LanFileFailure("entry-limit");
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted | FileAttributes.Offline)) != 0)
                throw new LanFileFailure("unsupported-attributes");
            bool directory = (attributes & FileAttributes.Directory) != 0;
            int zone = StreamLength(path, directory);
            long length = directory ? -1 : new FileInfo(path).Length;
            bytes = checked(bytes + Math.Max(0, length) + zone);
            if (bytes > Limit) throw new LanFileFailure("size-limit");
            entries.Add(new(new(relative, length, File.GetLastWriteTimeUtc(path).Ticks, zone), path, FileIdentity(path)));
            if (!directory) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                Add(child, relative + "/" + Path.GetFileName(child));
        }
    }

    private static string FileIdentity(string path)
    {
        // Backup semantics permits opening a directory. Open the link itself so a replaced
        // entry can never take on the identity of a link target.
        using var handle = CreateFileW(path, 0, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileId>()))
            throw new LanFileFailure("source-identity");
        return $"{id.Volume:X16}{id.High:X16}{id.Low:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileId information, uint size);

    public static long Validate(LanFileEntry[] entries)
    {
        if (entries.Length is 0 or > EntryLimit) throw new LanFileFailure("entry-limit");
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in entries)
        {
            if (entry is null || !IsRelativeName(entry.Name) || entry.Length < -1 || entry.Length > Limit ||
                entry.Modified < 0 || entry.Modified > DateTime.MaxValue.Ticks || entry.ZoneLength is < 0 or > 65536 ||
                entry.Length == -1 && entry.ZoneLength != 0 || !names.TryAdd(entry.Name, entry.Length == -1))
                throw new LanFileFailure("invalid-entry");
            int slash = entry.Name.LastIndexOf('/');
            if (slash >= 0 && (!names.TryGetValue(entry.Name[..slash], out bool directory) || !directory))
                throw new LanFileFailure("invalid-parent");
            bytes += Math.Max(0, entry.Length) + entry.ZoneLength;
            if (bytes > Limit) throw new LanFileFailure("size-limit");
        }
        return bytes;
    }

    public static void Commit(string stage, string destination, LanFileEntry[] entries)
    {
        VerifyLocalPath(destination);
        foreach (var entry in entries.Where(entry => !entry.Name.Contains('/')))
        {
            string source = Path.Combine(stage, entry.Name);
            string extension = entry.Length < 0 ? "" : Path.GetExtension(entry.Name);
            if (extension == entry.Name) extension = "";
            string stem = entry.Name[..(entry.Name.Length - extension.Length)];
            for (int index = 1; ; index++)
            {
                string suffix = index == 1 ? "" : $" ({index})";
                int stemLength = Math.Min(stem.Length, 255 - extension.Length - suffix.Length);
                if (stemLength <= 0 || index > EntryLimit) throw new LanFileFailure("name-collision");
                string target = Path.Combine(destination, index == 1 ? entry.Name : stem[..stemLength] + suffix + extension);
                try
                {
                    if (entry.Length < 0) Directory.Move(source, target);
                    else File.Move(source, target);
                    break;
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
            }
        }
    }

    public static void CleanStage(string path)
    {
        if (!Directory.Exists(path)) return;
        VerifyLocalPath(path);
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0) CleanStage(child);
            else File.Delete(child);
        }
        Directory.Delete(path);
    }

    public static int StreamLength(string path, bool directory)
    {
        nint search = FindFirstStreamW(path, 0, out var data, 0);
        if (search == -1)
        {
            if (Marshal.GetLastWin32Error() is 38 or 1 or 50) return 0;
            throw new LanFileFailure("stream-query");
        }
        int zone = 0;
        try
        {
            do
            {
                if (data.Name == "::$DATA") continue;
                if (directory || !string.Equals(data.Name, ":Zone.Identifier:$DATA", StringComparison.OrdinalIgnoreCase) ||
                    data.Length is <= 0 or > 65536) throw new LanFileFailure("unsupported-stream");
                zone = (int)data.Length;
            } while (FindNextStreamW(search, out data));
            if (Marshal.GetLastWin32Error() != 38) throw new LanFileFailure("stream-query");
            return zone;
        }
        finally { FindClose(search); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Length;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint FindFirstStreamW(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(nint search, out StreamData data);
    [DllImport("kernel32.dll")] private static extern bool FindClose(nint search);
}
