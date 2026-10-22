using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Zipper;

// Creates .7z archives through the bundled 7zr.exe (official 7-Zip console build, LGPL).
static class SevenZipArchiver
{
    static string Exe => Path.Combine(AppContext.BaseDirectory, "7zr.exe");

    public static void Add(string archive, string[] files, AddOptions options, SevenZipOptions sevenZip, JobProgress? progress = null)
    {
        List<string> args = [options.OnlyNewer ? "u" : "a", "-t7z", $"-mx{options.Level}", $"-ms={(sevenZip.Solid ? "on" : "off")}", "-y", "-sccUTF-8"];
        if (sevenZip.DictionaryMb > 0) args.Add($"-md{sevenZip.DictionaryMb}m");
        if (sevenZip.Volume != null) args.Add($"-v{sevenZip.Volume}");
        // shortcut: the password goes on 7zr's command line (visible to this user's processes), pipe it through stdin if that matters
        if (options.Password != null) args.Add($"-p{options.Password}");
        if (options.Paths == PathMode.Full) args.Add("-spf2");
        if (options.OpenShared) args.Add("-ssw");
        if (options.PreserveAccessTime) args.Add("-ssp");
        if (options.StoreCreationTime) args.Add("-mtc=on");
        if (options.StoreAccessTime) args.Add("-mta=on");
        if (sevenZip.Streams) args.Add("-sns");
        if (sevenZip.SymLinks) args.Add("-snl");
        if (sevenZip.HardLinks) args.Add("-snh");
        foreach (var mask in options.Exclude ?? []) args.Add($"-xr!{mask}");
        if (!string.IsNullOrWhiteSpace(sevenZip.ExtraArgs)) args.AddRange(sevenZip.ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.Add("--");
        args.Add(archive);
        args.AddRange(files);

        if (progress != null)
            progress.Total = files.Sum(f => Directory.Exists(f) ? new DirectoryInfo(f).EnumerateFiles("*", SearchOption.AllDirectories).Sum(i => i.Length) : new FileInfo(f).Length);
        var existed = File.Exists(archive);
        try
        {
            Run(args, progress, archive);
        }
        catch when (!existed)
        {
            // don't leave a half-written archive (or volumes) behind after a cancel or an error
            foreach (var part in Directory.EnumerateFiles(Path.GetDirectoryName(archive)!, Path.GetFileName(archive) + "*")) File.Delete(part);
            throw;
        }
    }

    public sealed record Entry(string Path, long Size, bool IsDirectory, bool Encrypted);

    public static List<Entry> List(string archive, string? password)
    {
        List<string> args = ["l", "-slt", "-ba", "-sccUTF-8"];
        if (password != null) args.Add($"-p{password}");
        args.AddRange(["--", archive]);
        // -slt prints one "Key = Value" block per item, separated by blank lines
        return Regex.Split(Run(args).Replace("\r", ""), @"\n\s*\n")
            .Select(block => block.Split('\n').Select(l => l.Split(" = ", 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => kv[1]))
            .Where(f => f.ContainsKey("Path"))
            .Select(f => new Entry(f["Path"], long.TryParse(f.GetValueOrDefault("Size"), out var size) ? size : 0,
                f.GetValueOrDefault("Attributes", "").StartsWith('D') || f.GetValueOrDefault("Folder") == "+", f.GetValueOrDefault("Encrypted") == "+"))
            .ToList();
    }

    public static bool IsEncrypted(string archive)
    {
        try
        {
            return List(archive, null).Any(e => e.Encrypted);
        }
        catch (InvalidOperationException)
        {
            return true; // encrypted file names: 7-Zip cannot even list the archive without the password
        }
    }

    public static void Extract(string archive, string destination, string? password, Func<string, bool> overwrite, JobProgress? progress = null)
    {
        // a wrong password would leave truncated files behind (even over existing ones), so check it first
        if (password != null && Test(archive, password) is [var error, ..]) throw new InvalidDataException(error);
        var root = Path.GetFullPath(destination);
        var entries = List(archive, password);
        // 7-Zip cannot ask per file, so existing files are asked about up front and excluded when kept
        var keep = entries.Where(e => !e.IsDirectory && File.Exists(Path.Combine(root, e.Path)) && !overwrite(Path.Combine(root, e.Path)));

        List<string> args = ["x", "-y", "-aoa", "-sccUTF-8", $"-o{root}"];
        if (password != null) args.Add($"-p{password}");
        args.AddRange(keep.Select(e => $"-x!{e.Path}"));
        args.AddRange(["--", archive]);
        if (progress != null) progress.Total = entries.Sum(e => e.Size);
        Run(args, progress);
    }

    public static List<string> Test(string archive, string? password)
    {
        List<string> args = ["t", "-sccUTF-8"];
        if (password != null) args.Add($"-p{password}");
        args.AddRange(["--", File.Exists(archive) ? archive : archive + ".001"]);
        try
        {
            Run(args);
            return [];
        }
        catch (InvalidOperationException ex)
        {
            return [ex.Message];
        }
    }

    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr process);

    internal static string Run(IEnumerable<string> args, JobProgress? progress = null, string? archive = null)
    {
        var start = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // closed right away so a missing password fails instead of waiting for input
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        if (progress != null) start.ArgumentList.Insert(1, "-bsp1"); // percentage lines on stdout

        using var process = Process.Start(start)!;
        if (Process.GetCurrentProcess().PriorityClass == ProcessPriorityClass.BelowNormal) process.PriorityClass = ProcessPriorityClass.BelowNormal;
        process.StandardInput.Close();
        using var cancel = progress?.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        if (progress != null) progress.Paused = paused => _ = paused ? NtSuspendProcess(process.Handle) : NtResumeProcess(process.Handle);

        var error = process.StandardError.ReadToEndAsync();
        var output = new StringBuilder();
        var line = new StringBuilder();
        int c;
        while ((c = process.StandardOutput.Read()) >= 0)
        {
            output.Append((char)c);
            // 7-Zip redraws its progress line with backspaces/carriage returns
            if (c is '\b' or '\r' or '\n')
            {
                if (progress != null && line.Length > 0) ReadProgress(line.ToString(), progress, archive);
                line.Clear();
            }
            else line.Append((char)c);
        }
        process.WaitForExit();
        if (progress != null) progress.Paused = null;
        progress?.Token.ThrowIfCancellationRequested();
        // 7-Zip exit codes: 0 ok, 1 warning (e.g. a locked file was skipped), 2+ error
        if (process.ExitCode > 1)
            throw new InvalidOperationException("7-Zip: " + (error.Result.Trim() is { Length: > 0 } e ? e : output.ToString().Trim()));
        return output.ToString();
    }

    // "  37% 12 + pasta\arquivo.txt"
    static void ReadProgress(string text, JobProgress progress, string? archive)
    {
        var m = Regex.Match(text, @"(\d+)%(?:\s+\d+)?(?:\s+[+U=-]\s+(.+))?");
        if (!m.Success) return;
        progress.Done = progress.Total * int.Parse(m.Groups[1].Value) / 100;
        if (m.Groups[2].Success) progress.Begin(m.Groups[2].Value.Trim(), 0);
        if (archive != null && File.Exists(archive)) progress.Packed = new FileInfo(archive).Length;
    }
}
