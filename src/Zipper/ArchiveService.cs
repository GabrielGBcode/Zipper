using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;

namespace Zipper;

public sealed record ArchiveItem(string Name, long Size, long PackedSize, DateTime Modified, long Crc, bool IsDirectory, bool IsCrypted);

public static class ArchiveService
{
    public static List<ArchiveItem> List(string zipPath)
    {
        using var zf = new ZipFile(zipPath);
        return zf.Cast<ZipEntry>()
            .Select(e => new ArchiveItem(e.Name.Replace('\\', '/').TrimEnd('/'), e.Size, e.CompressedSize, e.DateTime,
                e.HasCrc ? e.Crc : -1, e.IsDirectory, e.IsCrypted))
            .ToList();
    }

    public static bool HasEncrypted(string zipPath)
    {
        using var zf = new ZipFile(zipPath);
        return zf.Cast<ZipEntry>().Any(e => e.IsCrypted);
    }

    // Adds files/folders under `folderInArchive` ("" or "a/b/"). Entries with the same name are replaced.
    // shortcut: rebuilds the whole archive (existing entries are recompressed), switch to passthrough copy if big archives get slow.
    public static void Add(string zipPath, IEnumerable<string> paths, string folderInArchive, AddOptions options, string? existingPassword, JobProgress? progress = null)
    {
        var incoming = new List<(string Name, string? File)>();
        foreach (var p in paths.Select(Path.GetFullPath))
            Collect(p, Path.GetDirectoryName(p.TrimEnd('\\'))!, folderInArchive, options, incoming);
        incoming = incoming.DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (options.OnlyNewer && File.Exists(zipPath))
        {
            var existing = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in List(zipPath)) existing[item.Name] = item.Modified;
            // ZIP stores times with 2-second precision
            incoming.RemoveAll(i => i.File != null && existing.TryGetValue(i.Name, out var t) && File.GetLastWriteTime(i.File) <= t.AddSeconds(2));
        }
        var replaced = incoming.Select(i => i.Name.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var comment = options.Comment;

        var tmp = zipPath + ".tmp";
        try
        {
            var target = File.Create(tmp);
            using (var output = new ZipOutputStream(target))
            {
                using var zf = File.Exists(zipPath) ? new ZipFile(zipPath) { Password = existingPassword } : null;
                var kept = zf?.Cast<ZipEntry>().Where(e => !replaced.Contains(e.Name.TrimEnd('/'))).ToList() ?? [];
                if (progress != null) progress.Total = kept.Sum(e => e.Size) + incoming.Sum(i => i.File == null ? 0 : new FileInfo(i.File).Length);

                foreach (var e in kept)
                {
                    progress?.Begin(e.Name, e.Size);
                    using var src = e.IsDirectory ? null : zf!.GetInputStream(e);
                    Put(output, e.Name, e.DateTime, e.IsCrypted ? existingPassword : null, src, Level(e.Name, options), KeepNtTimes(e.ExtraData), progress);
                    if (progress != null) progress.Packed = target.Position;
                }
                foreach (var (name, file) in incoming)
                {
                    if (file == null)
                    {
                        Put(output, name, DateTime.Now, null, null, 0, null, progress);
                        continue;
                    }
                    progress?.Begin(file, new FileInfo(file).Length);
                    var accessed = File.GetLastAccessTime(file);
                    using (var src = File.Open(file, FileMode.Open, FileAccess.Read, options.OpenShared ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read))
                        Put(output, name, File.GetLastWriteTime(file), options.Password, src, Level(name, options), NtTimes(file, options), progress);
                    if (options.PreserveAccessTime) File.SetLastAccessTime(file, accessed);
                    if (progress != null) progress.Packed = target.Position;
                }
                comment ??= zf?.ZipFileComment;
                if (!string.IsNullOrEmpty(comment)) output.SetComment(comment);
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    static void Collect(string path, string root, string prefix, AddOptions options, List<(string, string?)> into)
    {
        var fileName = Path.GetFileName(path);
        if (MatchesMask(fileName, options.Exclude)) return;
        var relative = options.Paths switch
        {
            PathMode.None => fileName,
            PathMode.Full => path[Path.GetPathRoot(path)!.Length..],
            _ => Path.GetRelativePath(root, path),
        };
        var name = prefix + relative.Replace('\\', '/');
        if (!Directory.Exists(path))
        {
            var modified = File.GetLastWriteTime(path);
            if (options.OnlyArchiveAttribute && !File.GetAttributes(path).HasFlag(FileAttributes.Archive)) return;
            if (modified < options.ModifiedAfter || modified > options.ModifiedBefore) return;
            into.Add((name, path));
            return;
        }
        if (options.Paths != PathMode.None) into.Add((name + "/", null));
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
            Collect(child, root, prefix, options, into);
    }

    static bool MatchesMask(string fileName, string[]? masks) =>
        masks?.Any(m => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(m, fileName)) == true;

    static int Level(string entryName, AddOptions options) =>
        MatchesMask(Path.GetFileName(entryName.TrimEnd('/')), options.Store) ? 0 : options.Level;

    // NTFS times extra field: 100 ns modification time ("alta precisão"), plus creation/access times when asked.
    static byte[]? NtTimes(string file, AddOptions options)
    {
        if (!options.HighPrecision && !options.StoreCreationTime && !options.StoreAccessTime) return null;
        var none = DateTime.FromFileTimeUtc(0); // zero FILETIME means "not stored"
        var extra = new ZipExtraData();
        extra.AddEntry(new NTTaggedData
        {
            LastModificationTime = File.GetLastWriteTimeUtc(file),
            CreateTime = options.StoreCreationTime ? File.GetCreationTimeUtc(file) : none,
            LastAccessTime = options.StoreAccessTime ? File.GetLastAccessTimeUtc(file) : none,
        });
        return extra.GetEntryData();
    }

    // Keeps only the NTFS times of a re-written entry (its old AES field must not be copied).
    static byte[]? KeepNtTimes(byte[]? extraData)
    {
        if (extraData == null || new ZipExtraData(extraData).GetData<NTTaggedData>() is not { } times) return null;
        var extra = new ZipExtraData();
        extra.AddEntry(times);
        return extra.GetEntryData();
    }

    static void Put(ZipOutputStream output, string name, DateTime modified, string? password, Stream? data, int level, byte[]? extra, JobProgress? progress)
    {
        output.SetLevel(level);
        var entry = new ZipEntry(name) { DateTime = modified, IsUnicodeText = true };
        if (extra != null) entry.ExtraData = extra;
        output.Password = data == null ? null : password;
        if (output.Password != null) entry.AESKeySize = 256;
        output.PutNextEntry(entry);
        if (data != null) Copy(data, output, progress);
        output.CloseEntry();
    }

    static void Copy(Stream from, Stream to, JobProgress? progress)
    {
        var buffer = new byte[81920];
        int n;
        while ((n = from.Read(buffer)) > 0)
        {
            to.Write(buffer, 0, n);
            progress?.Step(n);
        }
    }

    // `names` are item names without trailing '/'; a folder name deletes everything inside it.
    public static void Delete(string zipPath, IEnumerable<string> names)
    {
        var targets = names.ToList();
        using var zf = new ZipFile(zipPath);
        var doomed = zf.Cast<ZipEntry>().Where(e => Matches(e.Name, targets)).ToList();
        zf.BeginUpdate();
        foreach (var e in doomed) zf.Delete(e);
        zf.CommitUpdate();
    }

    public static List<string> Test(string zipPath, string? password)
    {
        var errors = new List<string>();
        var buffer = new byte[81920];
        using var zf = new ZipFile(zipPath) { Password = password };
        foreach (ZipEntry e in zf)
        {
            if (e.IsDirectory) continue;
            try
            {
                var crc = new Crc32();
                using var s = zf.GetInputStream(e);
                int n;
                while ((n = s.Read(buffer)) > 0) crc.Update(new ArraySegment<byte>(buffer, 0, n));
                // AES (AE-2) entries store CRC 0; the AES stream checks its own authentication code instead
                if (e.AESKeySize == 0 && e.HasCrc && crc.Value != e.Crc) errors.Add($"{e.Name}: CRC inválido");
            }
            catch (Exception ex)
            {
                errors.Add($"{e.Name}: {ex.Message}");
            }
        }
        return errors;
    }

    // Extracts `names` (null = everything) found under `prefix`, writing paths relative to that prefix.
    // `overwrite` is asked for each file that already exists.
    public static void Extract(string zipPath, IReadOnlyList<string>? names, string prefix, string destination, string? password, Func<string, bool> overwrite, JobProgress? progress = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        using var zf = new ZipFile(zipPath) { Password = password };
        var selected = zf.Cast<ZipEntry>().Where(e =>
        {
            var n = e.Name.Replace('\\', '/');
            return n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Length > prefix.Length && (names == null || Matches(n, names));
        }).ToList();
        if (progress != null) progress.Total = selected.Sum(e => e.Size);

        foreach (var e in selected)
        {
            var entryName = e.Name.Replace('\\', '/');
            var target = Path.GetFullPath(Path.Combine(root, entryName[prefix.Length..]));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Caminho inseguro dentro do arquivo: {e.Name}");
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (File.Exists(target) && !overwrite(target))
            {
                if (progress != null) progress.Done += e.Size;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            progress?.Begin(entryName, e.Size);
            // write to a temp file first so a wrong password or corrupt data never destroys an existing file
            var tmp = target + ".zipper-tmp";
            try
            {
                using (var s = zf.GetInputStream(e))
                using (var f = File.Create(tmp))
                    Copy(s, f, progress);
                if (progress != null) progress.Packed += e.CompressedSize;
                File.Move(tmp, target, overwrite: true);
            }
            finally
            {
                File.Delete(tmp);
            }
            File.SetLastWriteTime(target, e.DateTime);
        }
    }

    static bool Matches(string entryName, IEnumerable<string> names)
    {
        var n = entryName.Replace('\\', '/').TrimEnd('/');
        return names.Any(t => n.Equals(t, StringComparison.OrdinalIgnoreCase) || n.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase));
    }
}

// Whole-archive operations for either format, picked by extension.
public static class AnyArchive
{
    public static bool Is7z(string path) => path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

    public static bool IsEncrypted(string path) => Is7z(path) ? SevenZipArchiver.IsEncrypted(path) : ArchiveService.HasEncrypted(path);

    public static List<string> Test(string path, string? password) => Is7z(path) ? SevenZipArchiver.Test(path, password) : ArchiveService.Test(path, password);

    public static void ExtractAll(string path, string destination, string? password, Func<string, bool> overwrite, JobProgress? progress = null)
    {
        if (Is7z(path)) SevenZipArchiver.Extract(path, destination, password, overwrite, progress);
        else ArchiveService.Extract(path, null, "", destination, password, overwrite, progress);
    }
}
