using ICSharpCode.SharpZipLib.Zip;
using Zipper;

namespace Zipper.Tests;

public sealed class ArchiveServiceTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("zipper-test").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void Password_add_delete_test_and_extract_roundtrip()
    {
        var src = Directory.CreateDirectory(Path.Combine(dir, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "ção.txt"), "beta");
        var zip = Path.Combine(dir, "t.zip");

        ArchiveService.Add(zip, [Path.Combine(src, "a.txt"), Path.Combine(src, "sub")], "", new AddOptions(Password: "s3nha"), null);
        Assert.True(ArchiveService.HasEncrypted(zip));
        Assert.Empty(ArchiveService.Test(zip, "s3nha"));
        Assert.NotEmpty(ArchiveService.Test(zip, "errada"));

        // adding to an encrypted archive keeps the old entries encrypted and readable
        File.WriteAllText(Path.Combine(dir, "c.txt"), "gamma");
        ArchiveService.Add(zip, [Path.Combine(dir, "c.txt")], "sub/", new AddOptions(Level: 9), "s3nha");
        ArchiveService.Delete(zip, ["a.txt"]);

        var items = ArchiveService.List(zip);
        Assert.Equal(new[] { "sub/c.txt", "sub/ção.txt" }, items.Where(i => !i.IsDirectory).Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.True(items.Single(i => i.Name == "sub/ção.txt").IsCrypted);
        Assert.False(items.Single(i => i.Name == "sub/c.txt").IsCrypted);

        var outDir = Path.Combine(dir, "out");
        ArchiveService.Extract(zip, ["sub"], "", outDir, "s3nha", _ => true);
        Assert.Equal("beta", File.ReadAllText(Path.Combine(outDir, "sub", "ção.txt")));
        Assert.Equal("gamma", File.ReadAllText(Path.Combine(outDir, "sub", "c.txt")));

        // extracting from inside a folder strips that folder; a refused overwrite keeps the file
        File.WriteAllText(Path.Combine(outDir, "c.txt"), "keep");
        ArchiveService.Extract(zip, null, "sub/", outDir, "s3nha", _ => false);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outDir, "c.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(outDir, "ção.txt")));
    }

    [Fact]
    public void Wrong_password_does_not_destroy_existing_file()
    {
        var zip = Path.Combine(dir, "p.zip");
        File.WriteAllText(Path.Combine(dir, "x.txt"), "secret");
        ArchiveService.Add(zip, [Path.Combine(dir, "x.txt")], "", new AddOptions(Password: "certa"), null);

        var outDir = Directory.CreateDirectory(Path.Combine(dir, "o")).FullName;
        File.WriteAllText(Path.Combine(outDir, "x.txt"), "original");
        Assert.ThrowsAny<Exception>(() => ArchiveService.Extract(zip, null, "", outDir, "errada", _ => true));
        Assert.Equal("original", File.ReadAllText(Path.Combine(outDir, "x.txt")));
    }

    [Fact]
    public void Extract_rejects_entries_outside_destination()
    {
        var zip = Path.Combine(dir, "evil.zip");
        using (var zos = new ZipOutputStream(File.Create(zip)))
        {
            zos.PutNextEntry(new ZipEntry("../evil.txt"));
            zos.Write("x"u8);
        }

        Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(zip, null, "", Path.Combine(dir, "o"), null, _ => true));
        Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
    }

    [Fact]
    public void Zip_options_exclude_flatten_store_update_and_comment()
    {
        var src = Directory.CreateDirectory(Path.Combine(dir, "src", "deep")).FullName;
        File.WriteAllText(Path.Combine(src, "keep.txt"), new string('a', 5000));
        File.WriteAllText(Path.Combine(src, "skip.tmp"), "x");
        File.WriteAllText(Path.Combine(src, "inner.zip"), new string('b', 5000));
        var zip = Path.Combine(dir, "o.zip");

        var options = new AddOptions(Exclude: ["*.tmp"], Store: ["*.zip"], Paths: PathMode.None, Comment: "olá");
        ArchiveService.Add(zip, [Path.Combine(dir, "src")], "", options, null);

        var items = ArchiveService.List(zip);
        Assert.Equal(new[] { "inner.zip", "keep.txt" }, items.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.True(items.Single(i => i.Name == "inner.zip").PackedSize >= 5000); // stored, not compressed
        Assert.True(items.Single(i => i.Name == "keep.txt").PackedSize < 5000);
        using (var zf = new ZipFile(zip)) Assert.Equal("olá", zf.ZipFileComment);

        // "add and update" skips files that are not newer than the archived copy
        File.WriteAllText(Path.Combine(src, "keep.txt"), "changed");
        File.SetLastWriteTime(Path.Combine(src, "keep.txt"), DateTime.Now.AddDays(-30));
        ArchiveService.Add(zip, [Path.Combine(src, "keep.txt")], "", options with { OnlyNewer = true, Comment = null }, null);
        Assert.Equal(5000, ArchiveService.List(zip).Single(i => i.Name == "keep.txt").Size);
        using (var zf = new ZipFile(zip)) Assert.Equal("olá", zf.ZipFileComment); // comment kept
    }

    [Fact]
    public void SevenZip_job_with_password_and_separate_archives()
    {
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "alpha");
        File.WriteAllText(b, "beta");

        var job = AddJob.Quick([a, b], Path.Combine(dir, "x.7z")) with { Options = new AddOptions(Password: "s3nha"), Separate = true, TestAfter = true };
        job.Execute("", null);

        Assert.True(File.Exists(Path.Combine(dir, "a.7z")) && File.Exists(Path.Combine(dir, "b.7z")));
        Assert.Empty(SevenZipArchiver.Test(Path.Combine(dir, "a.7z"), "s3nha"));
        Assert.NotEmpty(SevenZipArchiver.Test(Path.Combine(dir, "a.7z"), "errada"));
        Assert.NotEmpty(SevenZipArchiver.Test(Path.Combine(dir, "a.7z"), null)); // no password: fails instead of hanging
    }

    [Fact]
    public void Explorer_selection_from_several_processes_is_combined()
    {
        var verb = "--test-" + Guid.NewGuid().ToString("N");
        string[]? collected = null;
        var collector = new Thread(() => collected = ShellIntegration.CollectSelection(verb, ["a"]));
        collector.Start();
        Thread.Sleep(200);
        var others = Enumerable.Range(0, 3).Select(i => Task.Run(() => ShellIntegration.CollectSelection(verb, [$"b{i}"]))).ToArray();
        Task.WaitAll(others);
        collector.Join();

        Assert.All(others, t => Assert.Empty(t.Result));
        Assert.Equal(new[] { "a", "b0", "b1", "b2" }, collected!.Order());
    }

    [Fact]
    public void Name_mask_uses_winrar_month_and_minute_rules()
    {
        var t = new DateTime(2026, 10, 21, 17, 5, 9);
        Assert.Equal("20261021170509", AddJob.ApplyMask("yyyymmddhhmmss", t));
        Assert.Equal("26-10_17.05", AddJob.ApplyMask("yy-mm_hh.mm", t));
    }

    [Fact]
    public void Zip_progress_reaches_total_and_cancel_keeps_original()
    {
        var src = Path.Combine(dir, "big.bin");
        File.WriteAllBytes(src, new byte[300_000]);
        var zip = Path.Combine(dir, "p.zip");
        var progress = new JobProgress();
        ArchiveService.Add(zip, [src], "", new AddOptions(), null, progress);
        Assert.Equal(300_000, progress.Total);
        Assert.Equal(progress.Total, progress.Done);
        Assert.True(progress.Packed > 0);

        var before = File.ReadAllBytes(zip);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveService.Add(zip, [src], "", new AddOptions(Level: 9), null, new JobProgress { Token = cancel.Token }));
        Assert.Equal(before, File.ReadAllBytes(zip));
        Assert.False(File.Exists(zip + ".tmp"));
    }

    [Fact]
    public void SevenZip_progress_and_cancel_removes_partial_archive()
    {
        var src = Path.Combine(dir, "big.bin");
        File.WriteAllBytes(src, new byte[300_000]);
        var archive = Path.Combine(dir, "p.7z");
        var progress = new JobProgress();
        SevenZipArchiver.Add(archive, [src], new AddOptions(), new SevenZipOptions(), progress);
        Assert.Equal(300_000, progress.Total);
        Assert.True(File.Exists(archive));

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var other = Path.Combine(dir, "c.7z");
        Assert.ThrowsAny<OperationCanceledException>(() => SevenZipArchiver.Add(other, [src], new AddOptions(), new SevenZipOptions(), new JobProgress { Token = cancel.Token }));
        Assert.False(File.Exists(other));
    }

    [Fact]
    public void Zip_filters_by_archive_attribute_and_date_and_stores_ntfs_times()
    {
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        var old = Path.Combine(dir, "old.txt");
        foreach (var f in new[] { a, b, old }) File.WriteAllText(f, f);
        File.SetAttributes(b, File.GetAttributes(b) & ~FileAttributes.Archive);
        File.SetLastWriteTime(old, new DateTime(2020, 1, 1));
        var zip = Path.Combine(dir, "f.zip");

        ArchiveService.Add(zip, [a, b, old], "", new AddOptions(OnlyArchiveAttribute: true, ModifiedAfter: new DateTime(2021, 1, 1), StoreCreationTime: true), null);

        Assert.Equal(["a.txt"], ArchiveService.List(zip).Select(i => i.Name));
        using var zf = new ZipFile(zip);
        var times = new ZipExtraData(zf.GetEntry("a.txt").ExtraData).GetData<NTTaggedData>();
        Assert.Equal(File.GetCreationTimeUtc(a), times!.CreateTime.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void SevenZip_extract_with_encrypted_names_overwrite_choice_and_wrong_password()
    {
        var src = Directory.CreateDirectory(Path.Combine(dir, "src", "sub")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(src, "b.txt"), "beta");
        var archive = Path.Combine(dir, "e.7z");
        SevenZipArchiver.Add(archive, [Path.Combine(dir, "src")], new AddOptions(Password: "s3nha"), new SevenZipOptions(ExtraArgs: "-mhe=on"));

        Assert.True(AnyArchive.IsEncrypted(archive));
        var outDir = Directory.CreateDirectory(Path.Combine(dir, "out", "src", "sub")).FullName;
        File.WriteAllText(Path.Combine(outDir, "a.txt"), "keep");

        // wrong password: nothing is touched
        Assert.ThrowsAny<Exception>(() => AnyArchive.ExtractAll(archive, Path.Combine(dir, "out"), "errada", _ => true));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.False(File.Exists(Path.Combine(outDir, "b.txt")));

        var asked = new List<string>();
        var progress = new JobProgress();
        AnyArchive.ExtractAll(archive, Path.Combine(dir, "out"), "s3nha", f => { asked.Add(Path.GetFileName(f)); return false; }, progress);
        Assert.Equal(["a.txt"], asked);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(outDir, "b.txt")));
        Assert.Equal(9, progress.Total);
    }
}
