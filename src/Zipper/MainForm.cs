using System.ComponentModel;
using System.Diagnostics;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualBasic.FileIO;

namespace Zipper;

// Path is the full disk path in folder mode, or the entry name ("a/b.txt") inside an archive.
sealed record Row(string Name, string Path, bool IsDir, long Size, bool IsUp = false);

sealed record Location(string Dir, string? Archive, string Folder);

// ListView exposes DoubleBuffered only to subclasses; without it the owner-drawn rows flicker on hover.
sealed class SmoothList : ListView
{
    public SmoothList() => DoubleBuffered = true;

    // The dark theme draws column lines down the empty part of the list; paint that area plain.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || Items.Count == 0) return; // WM_PAINT
        var top = Items[^1].Bounds.Bottom;
        if (top >= ClientSize.Height) return;
        using var g = CreateGraphics();
        using var brush = new SolidBrush(BackColor);
        g.FillRectangle(brush, 0, top, ClientSize.Width, ClientSize.Height - top);
    }
}

public sealed class MainForm : Form
{
    readonly SmoothList list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, AllowDrop = true, OwnerDraw = true, BorderStyle = BorderStyle.None, BackColor = Theme.Window, ForeColor = Theme.Text };
    readonly TextBox address = new() { Dock = DockStyle.Top, BorderStyle = BorderStyle.None };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, AutoEllipsis = true };
    readonly Panel banner = new() { Dock = DockStyle.Top, Visible = false };
    readonly ImageList icons = new() { ColorDepth = ColorDepth.Depth32Bit };
    readonly ToolTip tips = new();
    string bannerDetail = "";
    ListViewItem? hot; // row under the mouse

    string currentDir = ShellIntegration.DownloadsFolder();
    string? archivePath;
    string archiveFolder = ""; // "" or "a/b/"
    List<ArchiveItem> items = [];
    string? password; // password of the open archive, asked once
    readonly Stack<Location> back = new(), forward = new();
    Location? shown; // location currently on screen, recorded into `back` when it changes

    Location Here => new(currentDir, archivePath, archiveFolder);

    public MainForm(string? start)
    {
        Text = "Zipper";
        Size = new Size(LogicalToDeviceUnits(1120), LogicalToDeviceUnits(700));
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(460));
        StartPosition = FormStartPosition.CenterScreen;
        Ui.ApplyTheme(this);

        icons.ImageSize = new Size(LogicalToDeviceUnits(16), LogicalToDeviceUnits(16));
        list.SmallImageList = new ImageList { ImageSize = new Size(1, LogicalToDeviceUnits(32)) }; // only sets the row height; icons come from `icons`
        list.Columns.Add("Nome", LogicalToDeviceUnits(320));
        list.Columns.Add("Tamanho", LogicalToDeviceUnits(100), HorizontalAlignment.Right);
        list.Columns.Add("Compactado", LogicalToDeviceUnits(110), HorizontalAlignment.Right);
        list.Columns.Add("Tipo", LogicalToDeviceUnits(170));
        list.Columns.Add("Modificado", LogicalToDeviceUnits(140));
        list.Columns.Add("CRC32", LogicalToDeviceUnits(90));
        list.DrawColumnHeader += DrawHeader;
        list.DrawItem += (_, _) => { }; // DrawSubItem paints every cell
        list.DrawSubItem += DrawCell;
        list.MouseMove += (_, e) => SetHot(list.GetItemAt(e.X, e.Y));
        list.MouseLeave += (_, _) => SetHot(null);
        list.Resize += (_, _) => FitNameColumn();
        list.ColumnWidthChanged += (_, e) =>
        {
            if (e.ColumnIndex > 0) FitNameColumn();
        };
        list.DoubleClick += (_, _) => OpenSelected();
        list.SelectedIndexChanged += (_, _) => UpdateStatus();
        list.KeyDown += ListKeyDown;
        list.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        list.DragDrop += (_, e) =>
        {
            // after the drop returns, so Explorer is not blocked while the dialogs are open
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) BeginInvoke(() => AddPaths(files));
        };

        address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            NavigateTo(address.Text.Trim());
        };

        banner.Height = LogicalToDeviceUnits(84);
        banner.Paint += PaintBanner;
        banner.Resize += (_, _) => banner.Invalidate();

        var gutter = LogicalToDeviceUnits(20);
        var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(gutter, 0, gutter, 0) };
        main.Controls.Add(list);
        main.Controls.Add(banner);
        main.Controls.Add(BuildHeader());
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(30), BackColor = Theme.Surface, Padding = new Padding(gutter, 0, gutter, 0) };
        statusBar.Controls.Add(status);

        Controls.Add(main);
        Controls.Add(statusBar);
        Controls.Add(BuildSidebar());

        if (start != null) NavigateTo(Path.GetFullPath(start));
        else RefreshList();
    }

    Control BuildSidebar()
    {
        int width = LogicalToDeviceUnits(220), pad = LogicalToDeviceUnits(14);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Left, Width = width, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(pad, LogicalToDeviceUnits(18), pad, pad) };
        var logoSize = LogicalToDeviceUnits(32);
        var logo = new PictureBox { Size = new Size(logoSize, logoSize), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(LogicalToDeviceUnits(6), 0, LogicalToDeviceUnits(6), 0) };
        if (Ui.AppIcon != null) logo.Image = new Icon(Ui.AppIcon, logoSize, logoSize).ToBitmap();
        var brand = new Label { Text = "Zipper", Font = Theme.Title, AutoSize = true, Margin = new Padding(0, LogicalToDeviceUnits(2), 0, 0) };
        var header = Ui.Row(logo, brand);
        header.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(22));
        bar.Controls.Add(header);

        void Caption(string text) => bar.Controls.Add(new Label { Text = text, Font = Theme.Caption, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(10), 0, LogicalToDeviceUnits(6)) });
        void Command(string text, string glyph, Color color, Action action, string keys)
        {
            var b = new RoundButton { Text = text, Glyph = glyph, GlyphColor = color, Ghost = true, TabStop = false, Size = new Size(width - pad * 2, LogicalToDeviceUnits(38)), Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(2)) };
            b.Click += (_, _) =>
            {
                list.Focus(); // keep keyboard focus on the files, like a toolbar
                action();
            };
            tips.SetToolTip(b, keys);
            bar.Controls.Add(b);
        }

        Caption("COMANDOS");
        // light tints so the glyphs stay readable on the dark theme
        Command("Adicionar", "\uE710", Color.FromArgb(70, 200, 120), AddFiles, "Adicionar arquivos ao arquivo (Alt+A)");
        Command("Extrair para", "\uE896", Color.FromArgb(90, 160, 245), Extract, "Extrair para a pasta especificada (Alt+E)");
        Command("Testar", "\uE9D5", Color.FromArgb(180, 140, 240), Test, "Testar arquivos compactados (Alt+T)");
        Command("Visualizar", "\uE890", Color.FromArgb(245, 170, 60), ViewSelected, "Visualizar arquivo (Alt+V)");
        Command("Excluir", "\uE74D", Color.FromArgb(240, 95, 95), Delete, "Excluir arquivos (Del)");
        Command("Informações", "\uE946", Color.FromArgb(60, 200, 220), Info, "Mostrar informações (Alt+I)");
        Caption("ARQUIVO");
        Command("Abrir arquivo...", "\uE8E5", Theme.Muted, OpenArchiveDialog, "Abrir um arquivo .zip (Ctrl+O)");
        Command("Selecionar tudo", "\uE8B3", Theme.Muted, SelectAll, "Ctrl+A");
        return bar;
    }

    Control BuildHeader()
    {
        var square = LogicalToDeviceUnits(38);
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(66), ColumnCount = 5, RowCount = 1, Padding = new Padding(0, LogicalToDeviceUnits(14), 0, LogicalToDeviceUnits(14)) };
        for (var i = 0; i < 3; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, square));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, square));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        RoundButton Nav(string glyph, string tip, Action action)
        {
            var b = new RoundButton { Glyph = glyph, Ghost = true, TabStop = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, LogicalToDeviceUnits(2), 0) };
            b.Click += (_, _) => action();
            tips.SetToolTip(b, tip);
            return b;
        }
        header.Controls.Add(Nav("\uE72B", "Voltar (Alt+Esquerda)", () => Travel(back, forward)), 0, 0);
        header.Controls.Add(Nav("\uE72A", "Avançar (Alt+Direita)", () => Travel(forward, back)), 1, 0);
        header.Controls.Add(Nav("\uE74A", "Pasta acima (Backspace)", GoUp), 2, 0);

        // the address is a borderless TextBox inside a rounded pill
        var pill = new Panel { Dock = DockStyle.Fill, Margin = new Padding(LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(8), 0), Cursor = Cursors.IBeam };
        address.BackColor = Theme.Raised;
        address.ForeColor = Theme.Text;
        pill.Controls.Add(address);
        pill.Resize += (_, _) =>
        {
            pill.Padding = new Padding(LogicalToDeviceUnits(14), Math.Max(0, (pill.Height - address.Height) / 2), LogicalToDeviceUnits(14), 0);
            pill.Invalidate();
        };
        pill.Paint += (_, e) => Ui.Round(e.Graphics, new Rectangle(0, 0, pill.Width - 1, pill.Height - 1), LogicalToDeviceUnits(8), Theme.Raised, address.Focused ? Theme.Accent : Theme.Border);
        pill.Click += (_, _) => address.Focus();
        address.GotFocus += (_, _) => pill.Invalidate();
        address.LostFocus += (_, _) => pill.Invalidate();
        header.Controls.Add(pill, 3, 0);

        var menu = BuildMenu();
        RoundButton? more = null;
        more = Nav("\uE712", "Mais opções", () => menu.Show(more!, new Point(0, more!.Height)));
        header.Controls.Add(more, 4, 0);
        return header;
    }

    ContextMenuStrip BuildMenu()
    {
        static ToolStripMenuItem Item(string text, string? keys, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
            item.Click += (_, _) => action();
            return item;
        }

        var integrate = new ToolStripMenuItem("&Integrar ao menu de contexto do Explorer") { Checked = ShellIntegration.IsRegistered() };
        integrate.Click += (_, _) =>
        {
            try { ShellIntegration.SetRegistered(!integrate.Checked); }
            catch (Win32Exception) { } // UAC prompt declined
            integrate.Checked = ShellIntegration.IsRegistered();
        };

        return new ContextMenuStrip
        {
            Items =
            {
                Item("&Abrir arquivo...", "Ctrl+O", OpenArchiveDialog),
                Item("Selecionar &tudo", "Ctrl+A", SelectAll),
                new ToolStripSeparator(),
                integrate,
                new ToolStripSeparator(),
                Item("&Sobre o Zipper...", null, () => MessageBox.Show(this,
                    "Zipper 1.0\nCompactador ZIP livre e gratuito.\nCompactação com SharpZipLib (licença MIT).", "Sobre o Zipper")),
                Item("Sai&r", null, Close),
            },
        };
    }

    // The keyboard shortcuts the old menu bar had, plus Alt+arrows for history. Del and Ctrl+A are handled by the list
    // so they keep working in the address box.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Action? action = keyData switch
        {
            Keys.Control | Keys.O => OpenArchiveDialog,
            Keys.Alt | Keys.A => AddFiles,
            Keys.Alt | Keys.E => Extract,
            Keys.Alt | Keys.T => Test,
            Keys.Alt | Keys.V => ViewSelected,
            Keys.Alt | Keys.I => Info,
            Keys.Alt | Keys.Left => () => Travel(back, forward),
            Keys.Alt | Keys.Right => () => Travel(forward, back),
            _ => null,
        };
        if (action == null) return base.ProcessCmdKey(ref msg, keyData);
        action();
        return true;
    }

    // ---------- drawing ----------

    // "Nome" takes the width the other columns leave, so there is no horizontal scroll bar.
    void FitNameColumn()
    {
        var rest = list.Columns.Cast<ColumnHeader>().Skip(1).Sum(c => c.Width);
        list.Columns[0].Width = Math.Max(LogicalToDeviceUnits(160), list.ClientSize.Width - rest);
    }

    void SetHot(ListViewItem? item)
    {
        if (item == hot) return;
        if (hot != null && hot.ListView == list) list.Invalidate(hot.Bounds);
        hot = item;
        if (hot != null) list.Invalidate(hot.Bounds);
    }

    void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var background = new SolidBrush(Theme.Window)) e.Graphics.FillRectangle(background, e.Bounds);
        using (var line = new Pen(Theme.Border)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var align = e.Header!.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Theme.Caption, Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(10), 0), Theme.Muted,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        var item = e.Item!;
        var g = e.Graphics;
        var back = item.Selected ? Theme.Selection : item == hot ? Theme.RowHover : Theme.Window;
        using (var brush = new SolidBrush(back)) g.FillRectangle(brush, e.Bounds);

        var pad = LogicalToDeviceUnits(10);
        var r = Rectangle.Inflate(e.Bounds, -pad, 0);
        if (e.ColumnIndex == 0)
        {
            if (item.Selected)
                using (var accent = new SolidBrush(Theme.Accent)) g.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top + LogicalToDeviceUnits(7), LogicalToDeviceUnits(3), e.Bounds.Height - LogicalToDeviceUnits(14));
            var index = icons.Images.IndexOfKey(item.ImageKey);
            if (index >= 0) icons.Draw(g, r.Left, r.Top + (r.Height - icons.ImageSize.Height) / 2, index);
            var shift = icons.ImageSize.Width + pad;
            r = new Rectangle(r.Left + shift, r.Top, r.Width - shift, r.Height);
        }
        var align = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(g, e.SubItem!.Text, list.Font, r, e.ColumnIndex == 0 ? Theme.Text : Theme.Muted,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // Summary card shown above the list while an archive is open.
    void PaintBanner(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var gap = LogicalToDeviceUnits(14);
        var card = new Rectangle(0, 0, banner.Width - 1, banner.Height - gap);
        Ui.Round(g, card, LogicalToDeviceUnits(10), Theme.Surface, Theme.Border);
        var icon = LogicalToDeviceUnits(58);
        TextRenderer.DrawText(g, "\uE7B8", Theme.BigIcon, new Rectangle(LogicalToDeviceUnits(4), 0, icon, card.Height), Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var left = icon + LogicalToDeviceUnits(6);
        var half = card.Height / 2;
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, Path.GetFileName(archivePath), Theme.Heading, new Rectangle(left, 0, card.Width - left - gap, half + LogicalToDeviceUnits(2)), Theme.Text, flags | TextFormatFlags.Bottom);
        TextRenderer.DrawText(g, bannerDetail, Theme.Body, new Rectangle(left, half + LogicalToDeviceUnits(4), card.Width - left - gap, half), Theme.Muted, flags | TextFormatFlags.Top);
    }

    IEnumerable<Row> Selected => list.SelectedItems.Cast<ListViewItem>().Select(i => (Row)i.Tag!).Where(r => !r.IsUp);

    static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    static string Bytes(long n) => n.ToString("N0");

    static string Human(long n)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = n;
        var unit = 0;
        for (; value >= 1024 && unit < units.Length - 1; unit++) value /= 1024;
        return unit == 0 ? $"{n:N0} bytes" : $"{value:N1} {units[unit]}";
    }

    // Files, folder count (including folders implied by entry paths) and totals of an archive listing.
    static (List<ArchiveItem> Files, int Folders, long Size, long Packed) Summary(List<ArchiveItem> all)
    {
        var files = all.Where(i => !i.IsDirectory).ToList();
        var folders = all.Where(i => i.IsDirectory).Select(i => i.Name)
            .Concat(all.SelectMany(i => Enumerable.Range(1, i.Name.Count(c => c == '/')).Select(k => string.Join('/', i.Name.Split('/').Take(k)))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return (files, folders, files.Sum(f => f.Size), files.Sum(f => f.PackedSize));
    }

    // ---------- navigation ----------

    void RefreshList()
    {
        if (shown != null && shown != Here)
        {
            back.Push(shown);
            forward.Clear();
        }
        shown = Here;
        hot = null;
        list.BeginUpdate();
        list.Items.Clear();
        banner.Visible = archivePath != null;
        if (archivePath == null)
        {
            Text = "Zipper";
            address.Text = currentDir;
            if (Directory.GetParent(currentDir) != null) AddRow(new Row("..", "", true, 0, IsUp: true), "..", "", null, "");
            try
            {
                var dir = new DirectoryInfo(currentDir);
                foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                    AddRow(new Row(d.Name, d.FullName, true, 0), d.Name, "", d.LastWriteTime, "");
                foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                    AddRow(new Row(f.Name, f.FullName, false, f.Length), f.Name, "", f.LastWriteTime, "");
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }
        else
        {
            Text = $"{Path.GetFileName(archivePath)} - Zipper";
            address.Text = archivePath + (archiveFolder.Length > 0 ? "\\" + archiveFolder.TrimEnd('/').Replace('/', '\\') : "");
            var (all, folderCount, size, packed) = Summary(items);
            bannerDetail = $"{all.Count} arquivo(s)  ·  {folderCount} pasta(s)  ·  {Human(size)} → {Human(packed)}"
                + (size > 0 ? $" ({packed * 100 / size}%)" : "") + (all.Any(f => f.IsCrypted) ? "  ·  Criptografado" : "");
            banner.Invalidate();
            AddRow(new Row("..", "", true, 0, IsUp: true), "..", "", null, "");

            var folders = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var files = new List<ArchiveItem>();
            foreach (var item in items)
            {
                if (item.Name.Length <= archiveFolder.Length || !item.Name.StartsWith(archiveFolder, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = item.Name[archiveFolder.Length..];
                var slash = rest.IndexOf('/');
                if (slash >= 0) folders.Add(rest[..slash]);
                else if (item.IsDirectory) folders.Add(rest);
                else files.Add(item);
            }
            foreach (var f in folders)
                AddRow(new Row(f, archiveFolder + f, true, 0), f, "", null, "");
            foreach (var f in files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var name = f.Name[archiveFolder.Length..];
                AddRow(new Row(name, f.Name, false, f.Size), name + (f.IsCrypted ? "*" : ""), Human(f.PackedSize), f.Modified, f.Crc < 0 ? "" : f.Crc.ToString("X8"));
            }
        }
        list.EndUpdate();
        UpdateStatus();
    }

    void AddRow(Row row, string display, string packed, DateTime? modified, string crc)
    {
        var (key, type) = ShellIcons.Get(row.Name, row.IsDir, icons);
        list.Items.Add(new ListViewItem([display, row.IsDir ? "" : Human(row.Size), packed, row.IsUp ? "" : type, modified?.ToString("g") ?? "", crc])
        {
            Tag = row,
            ImageKey = key,
        });
    }

    void UpdateStatus()
    {
        var selected = Selected.ToList();
        var rows = selected.Count > 0 ? selected : list.Items.Cast<ListViewItem>().Select(i => (Row)i.Tag!).Where(r => !r.IsUp).ToList();
        var files = rows.Where(r => !r.IsDir).ToList();
        var folders = rows.Count - files.Count;
        status.Text = selected.Count > 0
            ? $"{selected.Count} selecionado(s)  ·  {folders} pasta(s) e {files.Count} arquivo(s)  ·  {Human(files.Sum(f => f.Size))}"
            : $"{folders} pasta(s) e {files.Count} arquivo(s)  ·  {Human(files.Sum(f => f.Size))}";
    }

    void NavigateTo(string path)
    {
        if (Directory.Exists(path))
        {
            archivePath = null;
            currentDir = Path.GetFullPath(path);
            RefreshList();
        }
        else if (File.Exists(path) && IsZip(path)) OpenArchive(path);
        else if (File.Exists(path) && AnyArchive.Is7z(path))
        {
            // shortcut: .7z contents cannot be browsed yet, so show it selected in its folder (Extrair/Testar work on it)
            archivePath = null;
            currentDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            RefreshList();
            var item = list.Items.Cast<ListViewItem>().FirstOrDefault(i => string.Equals(((Row)i.Tag!).Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.Selected = item.Focused = true;
                item.EnsureVisible();
            }
        }
        else Ui.Error(this, $"Caminho não encontrado: {path}");
    }

    void OpenArchive(string zip)
    {
        try
        {
            items = ArchiveService.List(zip);
        }
        catch (Exception ex)
        {
            Ui.Error(this, $"Não foi possível abrir {Path.GetFileName(zip)}: {ex.Message}");
            return;
        }
        archivePath = Path.GetFullPath(zip);
        archiveFolder = "";
        currentDir = Path.GetDirectoryName(archivePath)!;
        password = null;
        RefreshList();
    }

    void ReloadArchive()
    {
        if (archivePath == null)
        {
            RefreshList();
            return;
        }
        try
        {
            items = ArchiveService.List(archivePath);
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex.Message);
        }
        RefreshList();
    }

    void GoUp()
    {
        if (archivePath != null)
        {
            if (archiveFolder.Length == 0)
            {
                archivePath = null;
                items = [];
            }
            else
            {
                var trimmed = archiveFolder.TrimEnd('/');
                archiveFolder = trimmed.Contains('/') ? trimmed[..(trimmed.LastIndexOf('/') + 1)] : "";
            }
        }
        else if (Directory.GetParent(currentDir) is { } parent) currentDir = parent.FullName;
        RefreshList();
    }

    void Travel(Stack<Location> from, Stack<Location> to)
    {
        if (!from.TryPop(out var target)) return;
        if (target.Archive != null && target.Archive != archivePath)
        {
            try
            {
                items = ArchiveService.List(target.Archive);
                password = null;
            }
            catch (Exception ex)
            {
                Ui.Error(this, ex.Message);
                return;
            }
        }
        to.Push(Here);
        (currentDir, archivePath, archiveFolder) = (target.Dir, target.Archive, target.Folder);
        if (archivePath == null) items = [];
        shown = Here; // a history move is not a new navigation
        RefreshList();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_APPCOMMAND = 0x319, APPCOMMAND_BROWSER_BACKWARD = 1, APPCOMMAND_BROWSER_FORWARD = 2;
        // mouse side buttons become WM_APPCOMMAND, which child controls pass up to the form
        if (m.Msg == WM_APPCOMMAND && ((m.LParam.ToInt64() >> 16) & 0x0FFF) is var command
            && command is APPCOMMAND_BROWSER_BACKWARD or APPCOMMAND_BROWSER_FORWARD)
        {
            if (command == APPCOMMAND_BROWSER_BACKWARD) Travel(back, forward);
            else Travel(forward, back);
            m.Result = 1;
            return;
        }
        base.WndProc(ref m);
    }

    void OpenSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        var row = (Row)list.SelectedItems[0].Tag!;
        if (row.IsUp) GoUp();
        else if (archivePath != null && row.IsDir)
        {
            archiveFolder = row.Path + "/";
            RefreshList();
        }
        else if (archivePath != null) ViewSelected();
        else if (row.IsDir)
        {
            currentDir = row.Path;
            RefreshList();
        }
        else if (IsZip(row.Path)) OpenArchive(row.Path);
        else ShellOpen(row.Path);
    }

    void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex.Message);
        }
    }

    void ListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: OpenSelected(); break;
            case Keys.Back: GoUp(); break;
            case Keys.Delete: Delete(); break;
            case Keys.A when e.Control: SelectAll(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    void SelectAll()
    {
        foreach (ListViewItem item in list.Items) item.Selected = !((Row)item.Tag!).IsUp;
    }

    void OpenArchiveDialog()
    {
        using var d = new OpenFileDialog { Filter = "Arquivos ZIP (*.zip)|*.zip|Todos os arquivos (*.*)|*.*", InitialDirectory = currentDir };
        if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
    }

    // ---------- commands ----------

    // Runs archive work off the UI thread; returns false (after showing the error) if it failed.
    async Task<bool> Run(Action work)
    {
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            await Task.Run(work);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is ZipException) password = null; // most likely a wrong password: ask again next time
            Ui.Error(this, ex.Message);
            return false;
        }
        finally
        {
            Enabled = true;
            UseWaitCursor = false;
        }
    }

    // Runs a compress/extract job with the progress window; returns false (after showing the error) if it failed or was cancelled.
    bool WithProgress(string title, string archive, string verb, bool minimized, Action<JobProgress> work)
    {
        var error = ProgressForm.Run(this, title, archive, verb, minimized, work);
        if (error == null) return true;
        if (error is ZipException) password = null; // most likely a wrong password: ask again next time
        if (error is not OperationCanceledException) Ui.Error(this, error.Message);
        return false;
    }

    // False when the archive is encrypted and the user cancelled the password prompt.
    bool TryGetPassword(string zip, out string? pw)
    {
        pw = null;
        try
        {
            if (!AnyArchive.IsEncrypted(zip)) return true;
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex.Message);
            return false;
        }
        if (zip == archivePath)
            pw = password ??= PasswordDialog.Ask(this, $"Senha para {Path.GetFileName(zip)}", confirm: false);
        else
            pw = PasswordDialog.Ask(this, $"Senha para {Path.GetFileName(zip)}", confirm: false);
        return pw != null;
    }

    string[] SelectedArchives() =>
        archivePath != null ? [archivePath] : Selected.Where(r => !r.IsDir && (IsZip(r.Path) || AnyArchive.Is7z(r.Path))).Select(r => r.Path).ToArray();

    void AddFiles()
    {
        var files = archivePath == null ? Selected.Select(r => r.Path).ToArray() : [];
        if (files.Length == 0)
        {
            using var d = new OpenFileDialog { Multiselect = true, Title = "Selecione os arquivos para adicionar", InitialDirectory = currentDir };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            files = d.FileNames;
        }
        AddPaths(files);
    }

    void AddPaths(string[] files)
    {
        if (files.Length == 0) return;
        using var dialog = new AddDialog(files, archivePath ?? AddJob.DefaultName(files, ".zip"), fixedName: archivePath != null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var job = dialog.Job;
        string? existing = null;
        if (!job.SevenZip && File.Exists(job.ArchivePath) && !TryGetPassword(job.ArchivePath, out existing)) return;
        var folder = job.ArchivePath == archivePath ? archiveFolder : "";
        var name = Path.GetFileName(job.ArchivePath);
        WithProgress($"Criando arquivo {name}", name, "adicionando", job.Background, p => job.Execute(folder, existing, p));
        ReloadArchive();
    }

    void Extract()
    {
        var zips = SelectedArchives();
        if (zips.Length == 0)
        {
            Ui.Error(this, "Selecione um arquivo .zip ou .7z para extrair.");
            return;
        }
        var defaultDest = zips.Length == 1
            ? Path.Combine(Path.GetDirectoryName(zips[0])!, Path.GetFileNameWithoutExtension(zips[0]))
            : currentDir;
        using var dialog = new ExtractDialog(defaultDest);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string[]? names = archivePath != null && Selected.Any() ? Selected.Select(r => r.Path).ToArray() : null;
        var prefix = archivePath != null ? archiveFolder : "";
        foreach (var zip in zips)
        {
            if (!TryGetPassword(zip, out var pw)) return;
            var dest = zips.Length == 1 ? dialog.Destination : Path.Combine(dialog.Destination, Path.GetFileNameWithoutExtension(zip));
            var (name, mode) = (Path.GetFileName(zip), dialog.Mode);
            if (!WithProgress($"Extraindo de {name}", name, "extraindo", false,
                p =>
                {
                    var overwrite = Ui.OverwriteAsker(mode, p.Window, p.OnUi);
                    if (names == null && prefix == "") AnyArchive.ExtractAll(zip, dest, pw, overwrite, p);
                    else ArchiveService.Extract(zip, names, prefix, dest, pw, overwrite, p); // inside an open .zip
                })) return;
        }
        status.Text = "Extração concluída.";
    }

    async void Test()
    {
        foreach (var zip in SelectedArchives())
        {
            if (!TryGetPassword(zip, out var pw)) return;
            List<string> errors = [];
            if (!await Run(() => errors = AnyArchive.Test(zip, pw))) return;
            if (errors.Count == 0)
                MessageBox.Show(this, $"{Path.GetFileName(zip)}: nenhum erro encontrado.", "Testar arquivos", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
            {
                if (archivePath == zip) password = null;
                Ui.Error(this, $"{Path.GetFileName(zip)}: {errors.Count} erro(s)\n\n" + string.Join("\n", errors.Take(20)));
            }
        }
    }

    void ViewSelected()
    {
        var row = Selected.FirstOrDefault();
        if (row == null) return;
        if (archivePath == null || row.IsDir)
        {
            OpenSelected();
            return;
        }
        if (!TryGetPassword(archivePath, out var pw)) return;
        // shortcut: viewed files stay in %TEMP%\Zipper, add cleanup on exit if that folder grows
        var temp = Path.Combine(Path.GetTempPath(), "Zipper", Guid.NewGuid().ToString("N"));
        var (zip, prefix) = (archivePath, archiveFolder);
        if (WithProgress($"Extraindo de {Path.GetFileName(zip)}", Path.GetFileName(zip), "extraindo", false,
            p => ArchiveService.Extract(zip, [row.Path], prefix, temp, pw, _ => true, p)))
            ShellOpen(Path.Combine(temp, row.Name));
    }

    async void Delete()
    {
        var rows = Selected.ToArray();
        if (rows.Length == 0) return;
        var where = archivePath == null ? "para a Lixeira" : "do arquivo";
        if (MessageBox.Show(this, $"Mover {rows.Length} item(ns) {where}?", "Excluir", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        if (archivePath == null)
        {
            try
            {
                foreach (var r in rows)
                {
                    if (r.IsDir) FileSystem.DeleteDirectory(r.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else FileSystem.DeleteFile(r.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
            }
            catch (OperationCanceledException) { }
            RefreshList();
            return;
        }
        var zip = archivePath;
        var names = rows.Select(r => r.Path).ToArray();
        if (await Run(() => ArchiveService.Delete(zip, names))) ReloadArchive();
    }

    void Info()
    {
        var zip = SelectedArchives().FirstOrDefault(IsZip);
        if (zip == null) return;
        List<ArchiveItem> all;
        try
        {
            all = zip == archivePath ? items : ArchiveService.List(zip);
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex.Message);
            return;
        }
        var (files, folders, size, packed) = Summary(all);
        MessageBox.Show(this,
            $"Arquivo: {zip}\n\n" +
            $"Tamanho no disco: {Bytes(new FileInfo(zip).Length)} bytes\n" +
            $"Arquivos: {files.Count}\nPastas: {folders}\n" +
            $"Tamanho total: {Bytes(size)} bytes\nCompactado: {Bytes(packed)} bytes\n" +
            $"Taxa de compressão: {(size == 0 ? 0 : packed * 100 / size)}%\n" +
            $"Criptografado: {(files.Any(f => f.IsCrypted) ? "Sim" : "Não")}",
            "Informações do arquivo", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
