using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zipper;

// "Zipper" submenu in Explorer's right-click menu (per user, no admin) and the command-line actions it calls.
// Files and folders get registry verbs; .zip/.7z get ZipperShell.dll (native\ZipperShell.cpp), whose
// "Extrair para" item shows the target folder name, which a static verb label cannot.
static class ShellIntegration
{
    const string Classes = @"Software\Classes\";

    // Files (except .zip/.7z) and folders.
    static readonly string[] Menus = [@"*\shell\Zipper", @"Directory\shell\Zipper"];

    // ZipperShell.dll: the .zip/.7z menu, and the right-drag menu on folders and drives ("Extrair aqui" where the
    // archive is dropped). Same GUID as CLSID_ZipperMenu in native\ZipperShell.cpp.
    const string HandlerClsid = "{55C77995-1843-4308-9FFF-F15E42746FF7}";
    static readonly string[] ArchiveHandlers = [@"SystemFileAssociations\.zip\shellex\ContextMenuHandlers\Zipper", @"SystemFileAssociations\.7z\shellex\ContextMenuHandlers\Zipper",
        @"Directory\shellex\DragDropHandlers\Zipper", @"Drive\shellex\DragDropHandlers\Zipper"];

    // Verbs written by older versions: flat ones (1.0), then the static archive submenus.
    static readonly string[] OldKeys = [@"SystemFileAssociations\.zip\shell\Zipper.Extract", @"SystemFileAssociations\.zip\shell\Zipper.ExtractHere", @"SystemFileAssociations\.zip\shell\Zipper.ExtractFolder",
        @"SystemFileAssociations\.zip\shell\Zipper", @"SystemFileAssociations\.7z\shell\Zipper"];

    // Subkey names start with a number because Explorer sorts submenu items by key name.
    static readonly (string Key, string Label, string Arg)[] AddVerbs =
    [
        ("01Add", "Adicionar para o arquivo...", "--add-dialog"),
        ("02AddZip", "Adicionar para .zip com o mesmo nome", "--add-zip"),
        ("03Add7z", "Adicionar para .7z com o mesmo nome", "--add-7z"),
    ];

    // Sent by ZipperShell.dll.
    static readonly string[] ExtractArgs = ["--extract", "--extract-here", "--extract-folder"];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out string path);

    // Asks the shell because Downloads can be moved to another drive.
    public static string DownloadsFolder() =>
        SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var path) == 0 && Directory.Exists(path)
            ? path
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Installed in Program Files by the MSI: the menu is written to HKLM for every user, which needs admin.
    // Anywhere else (a dev build) it stays per user in HKCU.
    static readonly bool Machine = Environment.ProcessPath!.StartsWith(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + '\\', StringComparison.OrdinalIgnoreCase);
    static RegistryKey Root => Machine ? Registry.LocalMachine : Registry.CurrentUser;

    // Options menu toggle. Machine-wide it reruns Zipper elevated (UAC prompt); throws Win32Exception if declined.
    public static void SetRegistered(bool on)
    {
        if (!Machine)
        {
            if (on) Register(); else Unregister();
            return;
        }
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, on ? "--register" : "--unregister") { UseShellExecute = true, Verb = "runas" })!;
        process.WaitForExit();
    }

    public static bool IsRegistered()
    {
        using var key = Root.OpenSubKey($@"{Classes}{Menus[1]}\shell\{AddVerbs[0].Key}\command");
        return key?.GetValue(null) is string command && command.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        Unregister();
        var exe = Environment.ProcessPath!;
        foreach (var path in Menus)
        {
            using var menu = Root.CreateSubKey(Classes + path);
            menu.SetValue("MUIVerb", "Zipper");
            menu.SetValue("Icon", $"\"{exe}\",0");
            menu.SetValue("SubCommands", "");
            menu.SetValue("MultiSelectModel", "Player"); // keep the menu when more than 15 items are selected
            if (path.StartsWith('*')) menu.SetValue("AppliesTo", "System.FileExtension:<>.zip AND System.FileExtension:<>.7z"); // archives have their own menu
            foreach (var (key, label, arg) in AddVerbs)
            {
                using var verb = menu.CreateSubKey($@"shell\{key}");
                verb.SetValue("MUIVerb", label);
                verb.SetValue("Icon", $"\"{exe}\",0");
                verb.SetValue("MultiSelectModel", "Player");
                using var command = verb.CreateSubKey("command");
                command.SetValue(null, $"\"{exe}\" {arg} \"%1\"");
            }
        }

        using (var server = Root.CreateSubKey($@"{Classes}CLSID\{HandlerClsid}\InprocServer32"))
        {
            server.SetValue(null, Path.Combine(Path.GetDirectoryName(exe)!, "ZipperShell.dll"));
            server.SetValue("ThreadingModel", "Apartment");
        }
        foreach (var path in ArchiveHandlers)
        {
            using var handler = Root.CreateSubKey(Classes + path);
            handler.SetValue(null, HandlerClsid);
        }
    }

    public static void Unregister()
    {
        foreach (var key in Menus.Concat(ArchiveHandlers).Concat(OldKeys).Append($@"CLSID\{HandlerClsid}"))
            Root.DeleteSubKeyTree(Classes + key, throwOnMissingSubKey: false);
    }

    // Handles --register/--unregister and the Explorer verbs; false means "open the main window".
    public static bool HandleCommand(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--register": Register(); return true;
            case "--unregister": Unregister(); return true;
        }
        if (args.Length < 2) return false;
        if (AddVerbs.Any(v => v.Arg == args[0])) Add(args[0], args[1..]);
        else if (ExtractArgs.Contains(args[0])) Extract(args[0], Path.GetFullPath(args[1]), args.Length > 2 ? Path.GetFullPath(args[2]) : null);
        else return false;
        return true;
    }

    static void Add(string verb, string[] paths)
    {
        var sources = CollectSelection(verb, paths.Select(Path.GetFullPath).ToArray());
        if (sources.Length == 0) return; // another Zipper process is handling this selection

        AddJob job;
        if (verb == "--add-dialog")
        {
            using var dialog = new AddDialog(sources, AddJob.DefaultName(sources, ".zip"), fixedName: false);
            if (dialog.ShowDialog() != DialogResult.OK) return;
            job = dialog.Job;
        }
        else job = AddJob.Quick(sources, AddJob.DefaultName(sources, verb == "--add-7z" ? ".7z" : ".zip"));

        var archive = Path.GetFileName(job.ArchivePath);
        try
        {
            string? existing = null;
            if (!job.SevenZip && File.Exists(job.ArchivePath) && ArchiveService.HasEncrypted(job.ArchivePath)
                && (existing = PasswordDialog.Ask(null, $"Senha de {archive}", confirm: false)) == null) return;
            var error = ProgressForm.Run(null, $"Criando arquivo {archive}", archive, "adicionando", job.Background, p => job.Execute("", existing, p));
            if (error is not null and not OperationCanceledException) Ui.Error(null, $"{archive}: {error.Message}");
        }
        catch (Exception ex)
        {
            Ui.Error(null, $"{archive}: {ex.Message}");
        }
    }

    // `target` is the folder the archive was right-dragged onto; otherwise extraction happens next to the archive.
    static void Extract(string verb, string zip, string? target) // .zip or .7z
    {
        var folder = target ?? Path.GetDirectoryName(zip)!;
        var ownFolder = Path.Combine(folder, Path.GetFileNameWithoutExtension(zip));
        var (destination, mode) = (verb == "--extract-here" ? folder : ownFolder, OverwriteMode.Ask);
        if (verb == "--extract")
        {
            using var dialog = new ExtractDialog(ownFolder);
            if (dialog.ShowDialog() != DialogResult.OK) return;
            (destination, mode) = (dialog.Destination, dialog.Mode);
        }

        try
        {
            string? password = null;
            var archive = Path.GetFileName(zip);
            if (AnyArchive.IsEncrypted(zip) && (password = PasswordDialog.Ask(null, $"Senha para {archive}", confirm: false)) == null) return;
            var error = ProgressForm.Run(null, $"Extraindo de {archive}", archive, "extraindo", false,
                p => AnyArchive.ExtractAll(zip, destination, password, Ui.OverwriteAsker(mode, p.Window, p.OnUi), p));
            if (error is not null and not OperationCanceledException) Ui.Error(null, $"{archive}: {error.Message}");
        }
        catch (Exception ex)
        {
            Ui.Error(null, $"{Path.GetFileName(zip)}: {ex.Message}");
        }
    }

    // Explorer starts one process per selected item. The first one collects the others' paths through
    // a pipe so the whole selection ends up in one archive; the others return an empty array.
    internal static string[] CollectSelection(string verb, string[] paths)
    {
        var name = $"Zipper{verb}.{Environment.UserName}";
        using var mutex = new Mutex(true, name, out var first);
        if (!first)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client);
                foreach (var p in paths) writer.WriteLine(p);
                return [];
            }
            catch (TimeoutException)
            {
                return paths; // the collector already finished: handle this item alone
            }
        }

        try
        {
            var all = new List<string>(paths);
            while (true)
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In);
                using var quiet = new CancellationTokenSource(800); // no new item for this long = selection complete
                try
                {
                    server.WaitForConnectionAsync(quiet.Token).Wait();
                }
                catch (AggregateException)
                {
                    break;
                }
                using var reader = new StreamReader(server);
                while (reader.ReadLine() is { } line) all.Add(line);
            }
            return all.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
