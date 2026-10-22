using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Zipper;

public enum PathMode { Relative, Full, None }

public enum DeleteMode { Keep, Delete, Recycle }

public enum ArchiveTime { Now, Original, Newest }

public enum FinishAction { None, Shutdown, Restart, Sleep, Hibernate }

// Options for the archive contents. Exclude/Store are name masks such as "*.tmp".
public sealed record AddOptions(int Level = 5, string? Password = null, bool OnlyNewer = false,
    string[]? Exclude = null, string[]? Store = null, PathMode Paths = PathMode.Relative, string? Comment = null,
    bool OpenShared = false, bool PreserveAccessTime = false, bool StoreCreationTime = false, bool StoreAccessTime = false,
    bool HighPrecision = true, bool OnlyArchiveAttribute = false, DateTime? ModifiedAfter = null, DateTime? ModifiedBefore = null);

public sealed record SevenZipOptions(int DictionaryMb = 0, bool Solid = true, string? Volume = null, string? ExtraArgs = null,
    bool Streams = false, bool SymLinks = false, bool HardLinks = false);

// One "Add" request, from the Add dialog or from an Explorer verb.
public sealed record AddJob(string ArchivePath, bool SevenZip, string[] Sources, AddOptions Options, SevenZipOptions SevenZipOptions,
    bool Separate = false, bool DoubleExtension = false, bool TestAfter = false,
    DeleteMode DeleteAfter = DeleteMode.Keep, ArchiveTime Time = ArchiveTime.Now,
    string? NameMask = null, bool KeepPrevious = false, bool ClearArchiveAttribute = false,
    bool Background = false, bool WaitForOthers = false, FinishAction Finish = FinishAction.None)
{
    public static AddJob Quick(string[] sources, string archivePath) =>
        new(archivePath, archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase), sources, new AddOptions(), new SevenZipOptions());

    // Like WinRAR: named after the item, or after the parent folder when several items are selected.
    public static string DefaultName(string[] sources, string extension)
    {
        var first = sources[0].TrimEnd('\\');
        var parent = Path.GetDirectoryName(first)!;
        var name = sources.Length == 1 ? BaseName(first, doubleExtension: false) : new DirectoryInfo(parent).Name;
        return Path.Combine(parent, name + extension);
    }

    static string BaseName(string path, bool doubleExtension) =>
        doubleExtension || Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);

    // WinRAR name masks: "yyyymmddhhmmss", where "mm" is the month before the hour and the minutes after it.
    public static string ApplyMask(string mask, DateTime time)
    {
        var result = new StringBuilder();
        var afterHour = false;
        foreach (var run in Regex.Matches(mask, @"(.)\1*").Select(m => m.Value))
        {
            var digits = new string('0', run.Length);
            var letter = char.ToLowerInvariant(run[0]);
            if (letter == 'h') afterHour = true;
            result.Append(letter switch
            {
                'y' => (run.Length <= 2 ? time.Year % 100 : time.Year).ToString(digits),
                'm' => (afterHour ? time.Minute : time.Month).ToString(digits),
                'd' => time.Day.ToString(digits),
                'h' => time.Hour.ToString(digits),
                's' => time.Second.ToString(digits),
                _ => run,
            });
        }
        return result.ToString();
    }

    public void Execute(string folderInArchive, string? existingPassword, JobProgress? progress = null)
    {
        if (Background) Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        if (WaitForOthers) WaitForOtherCopies(progress);

        var archivePath = NameMask is { Length: > 0 }
            ? Path.Combine(Path.GetDirectoryName(ArchivePath)!, Path.GetFileNameWithoutExtension(ArchivePath) + ApplyMask(NameMask, DateTime.Now) + Path.GetExtension(ArchivePath))
            : ArchivePath;
        var sources = Sources.Where(s => !string.Equals(Path.GetFullPath(s), Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase)).ToArray();
        var targets = Separate
            ? sources.Select(s => (Archive: Path.Combine(Path.GetDirectoryName(archivePath)!, BaseName(s.TrimEnd('\\'), DoubleExtension) + Path.GetExtension(archivePath)), Files: new[] { s }))
            : [(archivePath, sources)];

        foreach (var (archive, files) in targets)
        {
            DateTime? original = File.Exists(archive) ? File.GetLastWriteTime(archive) : null;
            if (KeepPrevious && File.Exists(archive)) File.Copy(archive, archive + ".bak", overwrite: true);

            if (SevenZip) SevenZipArchiver.Add(archive, files, Options, SevenZipOptions, progress);
            else ArchiveService.Add(archive, files, folderInArchive, Options, existingPassword, progress);

            if (TestAfter)
            {
                if (progress != null) progress.File = "Testando " + Path.GetFileName(archive);
                var errors = SevenZip ? SevenZipArchiver.Test(archive, Options.Password) : ArchiveService.Test(archive, Options.Password ?? existingPassword);
                if (errors.Count > 0) throw new InvalidDataException($"O teste de {Path.GetFileName(archive)} falhou:\n" + string.Join("\n", errors.Take(10)));
            }

            if (!File.Exists(archive)) continue; // split 7z volumes are named .7z.001, .7z.002...
            if (Time == ArchiveTime.Original && original != null) File.SetLastWriteTime(archive, original.Value);
            if (Time == ArchiveTime.Newest) File.SetLastWriteTime(archive, files.SelectMany(AllFiles).Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.Now).Max());
        }

        if (ClearArchiveAttribute)
            foreach (var f in sources.SelectMany(AllFiles))
                File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.Archive);

        if (DeleteAfter != DeleteMode.Keep)
        {
            var recycle = DeleteAfter == DeleteMode.Recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
            foreach (var s in sources)
            {
                if (Directory.Exists(s)) FileSystem.DeleteDirectory(s, UIOption.OnlyErrorDialogs, recycle);
                else FileSystem.DeleteFile(s, UIOption.OnlyErrorDialogs, recycle);
            }
        }

        RunFinishAction(Finish);
    }

    // "Aguardar se o Zipper estiver ativo": wait until no other Zipper process is running.
    static void WaitForOtherCopies(JobProgress? progress)
    {
        var self = Environment.ProcessId;
        while (Process.GetProcessesByName("Zipper").Any(p => p.Id != self))
        {
            if (progress != null) progress.File = "Aguardando outra cópia do Zipper...";
            if (progress?.Token.WaitHandle.WaitOne(1000) == true) progress.Token.ThrowIfCancellationRequested();
            else if (progress == null) Thread.Sleep(1000);
        }
    }

    [DllImport("powrprof.dll")]
    static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    static void RunFinishAction(FinishAction action)
    {
        switch (action)
        {
            // 60 s delay: Windows shows a warning and "shutdown /a" still cancels it
            case FinishAction.Shutdown: Process.Start("shutdown", "/s /t 60"); break;
            case FinishAction.Restart: Process.Start("shutdown", "/r /t 60"); break;
            case FinishAction.Sleep: SetSuspendState(false, false, false); break;
            case FinishAction.Hibernate: SetSuspendState(true, false, false); break;
        }
    }

    static IEnumerable<string> AllFiles(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories) : [path];
}
