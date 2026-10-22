using System.Text.RegularExpressions;

namespace Zipper;

// "Nome e parâmetros do arquivo": WinRAR's options grouped in sections; only options Zipper implements are shown.
sealed class AddDialog : Form
{
    static readonly (string Label, int Level)[] Methods =
        [("Armazenar", 0), ("Mais rápido", 1), ("Rápido", 3), ("Normal", 5), ("Bom", 7), ("Melhor", 9)];
    static readonly int[] Dictionaries = [0, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512];

    readonly string baseFolder;
    readonly bool fixedName;
    string? password;

    // Geral
    readonly ComboBox name = new() { DropDownStyle = ComboBoxStyle.DropDown };
    readonly ComboBox update = Combo("Adicionar e substituir arquivos", "Adicionar e atualizar arquivos");
    readonly RadioButton zip = Ui.Segment("ZIP");
    readonly RadioButton sevenZip = Ui.Segment("7Z");
    readonly ComboBox method = Combo(Methods.Select(m => m.Label).ToArray());
    readonly ComboBox dictionary = Combo(Dictionaries.Select(d => d == 0 ? "Automático" : $"{d} MB").ToArray());
    readonly TextBox volumeSize = new();
    readonly ComboBox volumeUnit = Combo("MB", "KB", "GB");
    readonly CheckBox deleteAfter = Check("Excluir arquivos após compressão");
    readonly CheckBox solid = Check("Criar arquivo sólido", on: true);
    readonly CheckBox testAfter = Check("Testar arquivos comprimidos");
    readonly RoundButton setPassword = new() { Text = "Definir senha...", Glyph = "\uE72E" };

    // Avançado
    readonly CheckBox streams = Check("Salvar fluxos alternativos (streams)");
    readonly CheckBox symLinks = Check("Armazenar links simbólicos como links");
    readonly CheckBox hardLinks = Check("Armazenar links físicos como links");
    readonly ComboBox finish = Combo("Manter funcionando", "Desligar o computador", "Reiniciar o computador", "Suspender", "Hibernar");
    readonly CheckBox background = Check("Comprimir em segundo plano");
    readonly CheckBox waitOthers = Check("Aguardar se o Zipper estiver ativo");

    readonly RadioButton deleteFiles = new() { Text = "Excluir arquivos", AutoSize = true };
    readonly RadioButton recycleFiles = new() { Text = "Mover arquivos para a Lixeira", AutoSize = true, Checked = true };
    readonly TextBox extraArgs = new();

    // Arquivos
    readonly TextBox files = new();
    readonly TextBox exclude = new();
    readonly TextBox store = new() { Text = "*.rar *.zip *.zipx *.cab *.7z *.ace *.arj *.bz2 *.gz *.lha *.lz *.lzh *.taz *.tgz *.xz *.txz *.zst" };
    readonly ComboBox paths = Combo("Armazenar caminhos relativos", "Armazenar caminhos completos", "Não armazenar caminhos");
    readonly CheckBox separate = Check("Adicionar cada arquivo em um arquivo comprimido separado");
    readonly CheckBox doubleExtension = Check("Usar extensões duplas para arquivos");

    // Segurança
    readonly CheckBox onlyArchiveAttribute = Check("Adicionar somente arquivos com atributo \"Arquivo\"");
    readonly CheckBox clearArchiveAttribute = Check("Limpar atributo \"Arquivo\" após compressão");
    readonly CheckBox openShared = Check("Abrir arquivos compartilhados");
    readonly CheckBox useMask = Check("Gerar nome do arquivo pela máscara");
    readonly TextBox mask = new() { Text = "yyyymmddhhmmss" };
    readonly CheckBox keepPrevious = Check("Manter versão anterior do arquivo (.bak)");

    // Horário
    readonly CheckBox storeCreation = Check("Armazenar horário de criação");
    readonly CheckBox storeAccess = Check("Armazenar horário do último acesso");
    readonly CheckBox highPrecision = Check("Alta precisão no formato do horário", on: true);
    readonly CheckBox preserveAccess = Check("Preservar último horário de acesso dos arquivos de origem");
    readonly ComboBox includeFiles = Combo("De qualquer horário", "Modificados depois de", "Modificados antes de");
    // a masked text box instead of DateTimePicker, which ignores the dark theme
    readonly MaskedTextBox includeDate = new() { Mask = "00/00/0000 00:00", Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm") };
    readonly ComboBox archiveTime = Combo("Horário atual do sistema", "Horário original do arquivo", "Horário do arquivo mais recente");

    // Comentários
    readonly TextBox commentFile = new() { ReadOnly = true };
    readonly TextBox comment = new() { Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true };
    readonly RoundButton commentBrowse = new() { Text = "Procurar..." };

    public AddDialog(string[] sources, string archivePath, bool fixedName)
    {
        this.fixedName = fixedName;
        baseFolder = Path.GetDirectoryName(sources[0].TrimEnd('\\')) ?? Environment.CurrentDirectory;
        name.Text = string.Equals(Path.GetDirectoryName(archivePath), baseFolder, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(archivePath) : archivePath;
        files.Text = JoinPaths(sources);
        method.SelectedIndex = 3;
        (archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? sevenZip : zip).Checked = true;

        Text = "Nome e parâmetros do arquivo";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        // from Explorer there is no main window, so center on the screen
        StartPosition = ActiveForm == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        ShowInTaskbar = ActiveForm == null;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(740, 500);
        Ui.ApplyTheme(this);

        var heading = At(this, new Label { Font = Theme.Title }, 216, 18, 500, 32);
        var nav = At(this, new Panel { BackColor = Theme.Surface }, 0, 0, 192, 500);
        At(nav, new Label { Text = fixedName ? "Adicionar ao arquivo" : "Novo arquivo", Font = Theme.Heading }, 18, 22, 160, 26);
        At(nav, new Label { Text = Path.GetFileName(archivePath), ForeColor = Theme.Muted, AutoEllipsis = true }, 18, 48, 160, 20);
        var pages = new (string Title, string Glyph, Panel Page)[]
        {
            ("Geral", "\uE713", General()), ("Arquivos", "\uE8B7", FilesPage()), ("Segurança", "\uE72E", SecurityPage()),
            ("Horário", "\uE823", TimePage()), ("Comentário", "\uE90A", CommentPage()), ("Avançado", "\uE9E9", Advanced()),
        };
        for (var i = 0; i < pages.Length; i++)
        {
            var (title, glyph, page) = pages[i];
            At(this, page, 216, 64, 500, 360).Visible = i == 0;
            var tab = At(nav, new RoundButton { Text = title, Glyph = glyph, GlyphColor = Theme.Muted, Ghost = true, Primary = i == 0 }, 10, 88 + i * 40, 172, 36);
            tab.Click += (_, _) =>
            {
                foreach (var (_, _, p) in pages) p.Visible = p == page;
                foreach (var b in nav.Controls.OfType<RoundButton>()) b.Primary = b == tab;
                nav.Invalidate(true);
                heading.Text = title;
            };
        }
        heading.Text = pages[0].Title;

        var ok = At(this, new RoundButton { Text = fixedName ? "Adicionar" : "Criar arquivo", DialogResult = DialogResult.OK, Primary = true }, 592, 446, 124, 34);
        var cancel = At(this, new RoundButton { Text = "Cancelar", DialogResult = DialogResult.Cancel }, 458, 446, 124, 34);
        AcceptButton = ok;
        CancelButton = cancel;

        zip.CheckedChanged += (_, _) => FormatChanged();
        foreach (var box in new[] { deleteAfter, separate, useMask }) box.CheckedChanged += (_, _) => UpdateState();
        includeFiles.SelectedIndexChanged += (_, _) => UpdateState();
        UpdateState();
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK || ValidationError() is not { } error) return;
            Ui.Error(this, error);
            e.Cancel = true;
        };
    }

    public AddJob Job => new(
        ArchivePath: ArchivePath,
        SevenZip: sevenZip.Checked,
        Sources: SplitPaths(files.Text),
        Options: new AddOptions(
            Level: Methods[method.SelectedIndex].Level,
            Password: password,
            OnlyNewer: update.SelectedIndex == 1,
            Exclude: SplitMasks(exclude.Text),
            Store: SplitMasks(store.Text),
            Paths: (PathMode)paths.SelectedIndex,
            Comment: comment.Text.Length > 0 ? comment.Text : null,
            OpenShared: openShared.Checked,
            PreserveAccessTime: preserveAccess.Checked,
            StoreCreationTime: storeCreation.Checked,
            StoreAccessTime: storeAccess.Checked,
            HighPrecision: highPrecision.Checked,
            OnlyArchiveAttribute: onlyArchiveAttribute.Checked,
            ModifiedAfter: includeFiles.SelectedIndex == 1 ? IncludeDate : null,
            ModifiedBefore: includeFiles.SelectedIndex == 2 ? IncludeDate : null),
        SevenZipOptions: new SevenZipOptions(Dictionaries[dictionary.SelectedIndex], solid.Checked, Volume, extraArgs.Text,
            streams.Checked, symLinks.Checked, hardLinks.Checked),
        Separate: separate.Checked,
        DoubleExtension: doubleExtension.Checked,
        TestAfter: testAfter.Checked,
        DeleteAfter: !deleteAfter.Checked ? DeleteMode.Keep : recycleFiles.Checked ? DeleteMode.Recycle : DeleteMode.Delete,
        Time: (ArchiveTime)archiveTime.SelectedIndex,
        NameMask: useMask.Checked ? mask.Text.Trim() : null,
        KeepPrevious: keepPrevious.Checked,
        ClearArchiveAttribute: clearArchiveAttribute.Checked,
        Background: background.Checked,
        WaitForOthers: waitOthers.Checked,
        Finish: (FinishAction)finish.SelectedIndex);

    string Extension => sevenZip.Checked ? ".7z" : ".zip";

    string ArchivePath
    {
        get
        {
            var text = name.Text.Trim();
            if (!Path.HasExtension(text)) text += Extension;
            return Path.GetFullPath(Path.Combine(baseFolder, text)); // a bare name goes next to the files, like WinRAR
        }
    }

    DateTime? IncludeDate => DateTime.TryParseExact(includeDate.Text, "dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;

    string? Volume => long.TryParse(volumeSize.Text.Trim(), out var size) && size > 0
        ? $"{size}{volumeUnit.Text.ToLowerInvariant()[0]}"
        : null;

    string? ValidationError()
    {
        if (name.Text.Trim().Length == 0) return "Informe o nome do arquivo.";
        var sources = SplitPaths(files.Text);
        if (sources.Length == 0) return "Informe os arquivos para serem adicionados.";
        if (sources.FirstOrDefault(s => !File.Exists(s) && !Directory.Exists(s)) is { } missing) return $"Arquivo não encontrado: {missing}";
        if (sevenZip.Checked && paths.SelectedIndex == (int)PathMode.None) return "O formato 7Z não permite \"Não armazenar caminhos\".";
        if (volumeSize.Text.Trim().Length > 0 && Volume == null) return "Tamanho de volume inválido.";
        if (useMask.Checked && mask.Text.Trim().Length == 0) return "Informe a máscara do nome.";
        if (includeFiles.SelectedIndex > 0 && IncludeDate == null) return "Data inválida em \"Incluir arquivos\" (dd/mm/aaaa hh:mm).";
        return null;
    }

    void FormatChanged()
    {
        var text = name.Text.Trim();
        var ext = Path.GetExtension(text);
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            name.Text = text[..^ext.Length] + Extension;
        UpdateState();
    }

    // Enables what applies to the chosen format; RAR-only options are created disabled and stay that way.
    void UpdateState()
    {
        var is7z = sevenZip.Checked;
        foreach (var only7z in new Control[] { dictionary, volumeSize, volumeUnit, solid, extraArgs, streams, symLinks, hardLinks })
            only7z.Enabled = is7z && !fixedName;
        foreach (var onlyZip in new Control[] { store, comment, commentFile, commentBrowse, highPrecision, onlyArchiveAttribute, includeFiles })
            onlyZip.Enabled = !is7z;
        includeDate.Enabled = !is7z && includeFiles.SelectedIndex > 0;
        deleteFiles.Enabled = recycleFiles.Enabled = deleteAfter.Checked;
        doubleExtension.Enabled = separate.Checked && !fixedName;
        mask.Enabled = useMask.Checked;
        name.Enabled = zip.Enabled = sevenZip.Enabled = separate.Enabled = files.Enabled = useMask.Enabled = !fixedName;
    }

    Panel General()
    {
        var page = new Panel();
        Label(page, "Nome do arquivo", 0, 0);
        At(page, name, 0, 22, 384);
        var browse = At(page, new RoundButton { Text = "Procurar...", Enabled = !fixedName }, 394, 18, 106, 30);
        browse.Click += (_, _) =>
        {
            using var d = new SaveFileDialog { Filter = "Arquivo ZIP (*.zip)|*.zip|Arquivo 7Z (*.7z)|*.7z", FilterIndex = sevenZip.Checked ? 2 : 1, OverwritePrompt = false, FileName = Path.GetFileName(ArchivePath), InitialDirectory = Path.GetDirectoryName(ArchivePath) };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            name.Text = d.FileName;
            (d.FileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? sevenZip : zip).Checked = true;
        };

        Label(page, "Formato", 0, 62);
        At(page, zip, 0, 84, 90, 30);
        At(page, sevenZip, 94, 84, 90, 30);
        Label(page, "Método de atualização", 260, 62);
        At(page, update, 260, 86, 240);

        Label(page, "Método de compressão", 0, 126);
        At(page, method, 0, 148, 240);
        Label(page, "Tamanho do dicionário (7Z)", 260, 126);
        At(page, dictionary, 260, 148, 240);

        Label(page, "Dividir em volumes (7Z)", 0, 186);
        At(page, volumeSize, 0, 208, 166);
        At(page, volumeUnit, 172, 208, 68);
        Label(page, "Proteção", 260, 186);
        At(page, setPassword, 260, 204, 240, 32);
        setPassword.Click += (_, _) =>
        {
            if (PasswordDialog.Ask(this, "Definir senha", confirm: true) is not { } p) return;
            password = p.Length > 0 ? p : null;
            setPassword.Text = password == null ? "Definir senha..." : "Senha definida (AES-256)";
            setPassword.GlyphColor = password == null ? Theme.Text : Theme.AccentHover;
        };

        var options = At(page, new Frame { Text = "Opções de compressão" }, 0, 250, 500, 110);
        At(options, solid, 14, 30);
        At(options, testAfter, 14, 56);
        At(options, deleteAfter, 14, 82);
        At(options, recycleFiles, 270, 56);
        At(options, deleteFiles, 270, 82);
        return page;
    }

    Panel Advanced()
    {
        var page = new Panel();
        var ntfs = At(page, new Frame { Text = "Opções NTFS (7Z)" }, 0, 0, 500, 110);
        At(ntfs, streams, 14, 30);
        At(ntfs, symLinks, 14, 56);
        At(ntfs, hardLinks, 14, 82);

        var system = At(page, new Frame { Text = "Sistema" }, 0, 124, 500, 84);
        At(system, background, 14, 30);
        At(system, waitOthers, 14, 56);

        Label(page, "Ao concluir", 0, 222);
        At(page, finish, 0, 244, 240);
        Label(page, "Comandos adicionais (7Z)", 0, 284);
        At(page, extraArgs, 0, 306, 500);
        return page;
    }

    Panel FilesPage()
    {
        var page = new Panel();
        Label(page, "Arquivos para serem adicionados", 0, 0);
        At(page, files, 0, 22, 384);
        var append = At(page, new RoundButton { Text = "Anexar...", Enabled = !fixedName }, 394, 18, 106, 30);
        append.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Multiselect = true, InitialDirectory = baseFolder };
            if (d.ShowDialog(this) == DialogResult.OK) files.Text = JoinPaths(SplitPaths(files.Text).Concat(d.FileNames).Distinct());
        };

        Label(page, "Arquivos para serem excluídos (máscaras, ex.: *.tmp)", 0, 62);
        At(page, exclude, 0, 84, 384);
        var appendExclude = At(page, new RoundButton { Text = "Anexar..." }, 394, 80, 106, 30);
        appendExclude.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Multiselect = true, InitialDirectory = baseFolder };
            if (d.ShowDialog(this) == DialogResult.OK)
                exclude.Text = string.Join(" ", SplitMasks(exclude.Text).Concat(d.FileNames.Select(f => Path.GetFileName(f))));
        };

        Label(page, "Armazenar sem compressão (ZIP)", 0, 124);
        At(page, store, 0, 146, 500);

        Label(page, "Caminho dos arquivos", 0, 186);
        At(page, paths, 0, 208, 280);

        var split = At(page, new Frame { Text = "Arquivos separados" }, 0, 250, 500, 84);
        At(split, separate, 14, 30);
        At(split, doubleExtension, 32, 56);
        return page;
    }

    Panel SecurityPage()
    {
        var page = new Panel();
        var security = At(page, new Frame { Text = "Opções de segurança" }, 0, 0, 500, 162);
        At(security, onlyArchiveAttribute, 14, 30);
        At(security, clearArchiveAttribute, 14, 56);
        At(security, openShared, 14, 82);
        At(security, useMask, 14, 108);
        At(security, mask, 330, 106, 150);
        At(security, keepPrevious, 14, 134);
        return page;
    }

    Panel TimePage()
    {
        var page = new Panel();
        var times = At(page, new Frame { Text = "Horários armazenados" }, 0, 0, 500, 136);
        At(times, storeCreation, 14, 30);
        At(times, storeAccess, 14, 56);
        At(times, highPrecision, 14, 82);
        At(times, preserveAccess, 14, 108);

        Label(page, "Incluir arquivos (ZIP)", 0, 152);
        At(page, includeFiles, 0, 174, 240);
        At(page, includeDate, 250, 174, 150);

        Label(page, "Horário do arquivo comprimido", 0, 214);
        At(page, archiveTime, 0, 236, 280);
        return page;
    }

    Panel CommentPage()
    {
        var page = new Panel();
        Label(page, "Carregar um comentário de arquivo", 0, 0);
        At(page, commentFile, 0, 22, 384);
        At(page, commentBrowse, 394, 18, 106, 30);
        commentBrowse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            commentFile.Text = d.FileName;
            comment.Text = File.ReadAllText(d.FileName);
        };
        Label(page, "Comentário (ZIP)", 0, 62);
        At(page, comment, 0, 84, 500, 270);
        return page;
    }

    static string JoinPaths(IEnumerable<string> list) => string.Join(" ", list.Select(s => s.Contains(' ') ? $"\"{s}\"" : s));

    static string[] SplitPaths(string text) =>
        Regex.Matches(text, "\"([^\"]+)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();

    static string[] SplitMasks(string text) => text.Split([' ', ';'], StringSplitOptions.RemoveEmptyEntries);

    static ComboBox Combo(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items);
        combo.SelectedIndex = 0;
        return combo;
    }

    static CheckBox Check(string text, bool on = false) => new() { Text = text, AutoSize = true, Checked = on };

    static void Label(Control parent, string text, int x, int y) => At(parent, new Label { Text = text, AutoSize = true, ForeColor = Theme.Muted }, x, y);

    static T At<T>(Control parent, T control, int x, int y, int width = 0, int height = 0) where T : Control
    {
        control.Location = new Point(x, y);
        if (width > 0) control.Width = width;
        if (height > 0) control.Height = height;
        parent.Controls.Add(control);
        return control;
    }
}
