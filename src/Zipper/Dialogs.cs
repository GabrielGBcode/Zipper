using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Zipper;

// One palette for every window: graphite surfaces with a violet accent.
static class Theme
{
    public static readonly Color Window = Color.FromArgb(18, 20, 26);
    public static readonly Color Surface = Color.FromArgb(25, 28, 36);
    public static readonly Color Raised = Color.FromArgb(34, 38, 48);
    public static readonly Color Hover = Color.FromArgb(44, 49, 61);
    public static readonly Color Border = Color.FromArgb(54, 60, 74);
    public static readonly Color Text = Color.FromArgb(234, 236, 242);
    public static readonly Color Muted = Color.FromArgb(146, 153, 170);
    public static readonly Color Accent = Color.FromArgb(124, 108, 242);
    public static readonly Color AccentHover = Color.FromArgb(142, 128, 248);
    public static readonly Color AccentDown = Color.FromArgb(104, 88, 222);
    public static readonly Color Selection = Color.FromArgb(50, 46, 91); // accent at 30% over Window
    public static readonly Color RowHover = Color.FromArgb(28, 31, 40);

    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font Strong = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Caption = new("Segoe UI Semibold", 8f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);

    public static readonly string IconFontName = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public static readonly Font Icon = new(IconFontName, 11f);
    public static readonly Font BigIcon = new(IconFontName, 20f);
}

static class Ui
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    // Zipper.ico, embedded in the exe by <ApplicationIcon>.
    public static readonly Icon? AppIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

    // Application.SetColorMode leaves some title bars light and draws thick white borders around inputs; fix both.
    public static void ApplyTheme(Form form)
    {
        form.Icon = AppIcon;
        form.BackColor = Theme.Window;
        form.ForeColor = Theme.Text;
        form.Font = Theme.Body;
        form.HandleCreated += (_, _) =>
        {
            var on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = ColorTranslator.ToWin32(Theme.Window);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11, ignored before)
        };
        form.Load += (_, _) => FlatInputs(form);
    }

    public static void Round(Graphics g, Rectangle r, int radius, Color? fill, Color? border = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var corner = new Size(radius * 2, radius * 2);
        if (fill is { } f)
        {
            using var brush = new SolidBrush(f);
            g.FillRoundedRectangle(brush, r, corner);
        }
        if (border is { } b)
        {
            using var pen = new Pen(b);
            g.DrawRoundedRectangle(pen, r, corner);
        }
    }

    // A rounded card with a small caption at the top.
    public static void PaintFrame(Control frame, PaintEventArgs e)
    {
        e.Graphics.Clear(frame.Parent?.BackColor ?? Theme.Window);
        Round(e.Graphics, new Rectangle(0, 0, frame.Width - 1, frame.Height - 1), frame.LogicalToDeviceUnits(8), Theme.Surface, Theme.Border);
        TextRenderer.DrawText(e.Graphics, frame.Text.ToUpperInvariant(), Theme.Caption,
            new Point(frame.LogicalToDeviceUnits(12), frame.LogicalToDeviceUnits(7)), frame.Enabled ? Theme.Muted : Theme.Border);
    }

    static void FlatInputs(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Theme.Raised;
                combo.ForeColor = Theme.Text;
                if (combo.IsHandleCreated) ComboBorder.Attach(combo);
                else combo.HandleCreated += (_, _) => ComboBorder.Attach(combo); // pages not shown yet
            }
            if (c is TextBoxBase text)
            {
                if (text.BorderStyle != BorderStyle.None) text.BorderStyle = BorderStyle.FixedSingle;
                text.BackColor = Theme.Raised;
                text.ForeColor = Theme.Text;
            }
            if (c is RadioButton { Appearance: Appearance.Normal } radio) radio.FlatStyle = FlatStyle.Flat;
            FlatInputs(c);
        }
    }

    // Radio buttons drawn as a segmented switch (e.g. ZIP | 7Z).
    public static RadioButton Segment(string text) => new()
    {
        Text = text,
        Appearance = Appearance.Button,
        FlatStyle = FlatStyle.Flat,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Theme.Raised,
        Cursor = Cursors.Hand,
        FlatAppearance = { BorderColor = Theme.Border, CheckedBackColor = Theme.Accent, MouseOverBackColor = Theme.Hover },
    };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    public static Label Label(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 10, 3, 2) };

    public static TextBox Box(int width = 360) => new() { Width = width };

    public static void Build(Form form, string title, params Control[] rows)
    {
        form.Text = title;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = form.MinimizeBox = false;
        form.ShowInTaskbar = Form.ActiveForm == null;
        // from Explorer there is no main window, so center on the screen
        form.StartPosition = Form.ActiveForm == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ApplyTheme(form);

        var ok = new RoundButton { Text = "OK", DialogResult = DialogResult.OK, Primary = true, Margin = new Padding(3, 3, 6, 3) };
        var cancel = new RoundButton { Text = "Cancelar", DialogResult = DialogResult.Cancel };
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        var heading = new Label { Text = title, Font = Theme.Heading, AutoSize = true, Margin = new Padding(3, 0, 3, 4) };
        var buttons = Row(ok, cancel);
        buttons.Margin = new Padding(0, 18, 0, 0);
        var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(20, 18, 20, 18) };
        column.Controls.Add(heading);
        column.Controls.AddRange(rows);
        column.Controls.Add(buttons);
        form.Controls.Add(column);
    }

    public static void Error(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Zipper", MessageBoxButtons.OK, MessageBoxIcon.Error);

    // `onUi` runs the prompt on the UI thread, since the extraction itself may run on a worker thread.
    public static Func<string, bool> OverwriteAsker(OverwriteMode mode, IWin32Window? owner, Func<Func<bool>, bool> onUi)
    {
        bool? all = mode switch { OverwriteMode.Overwrite => true, OverwriteMode.Skip => false, _ => null };
        return file => all ?? onUi(() =>
        {
            TaskDialogButton yes = new("Sim"), yesAll = new("Sim para todos"), no = new("Não"), noAll = new("Não para todos");
            var page = new TaskDialogPage
            {
                Caption = "Confirmar substituição de arquivo",
                Heading = "O arquivo já existe. Deseja substituí-lo?",
                Text = file,
                Icon = TaskDialogIcon.Warning,
                Buttons = { yes, yesAll, no, noAll },
            };
            var answer = owner == null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
            if (answer == yesAll) all = true;
            if (answer == noAll) all = false;
            return answer == yes || answer == yesAll;
        });
    }
}

// Rounded flat button. Primary = accent action (or the selected item); Ghost = no fill until hovered (sidebar, header);
// Glyph = Segoe icon drawn before the text, or alone when there is no text.
sealed class RoundButton : Button
{
    bool hover, down;

    public bool Primary { get; set; }
    public bool Ghost { get; set; }
    public string? Glyph { get; set; }
    public Color GlyphColor { get; set; } = Theme.Text;

    public RoundButton()
    {
        FlatStyle = FlatStyle.Flat; // in dark mode a Standard button is drawn by the OS and OnPaint never runs
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(104, 32);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        Color? fill = !Enabled ? (Ghost ? null : Theme.Surface)
            : Primary ? (down ? Theme.AccentDown : hover ? Theme.AccentHover : Theme.Accent)
            : down ? Theme.Border : hover ? Theme.Hover : Ghost ? null : Theme.Raised;
        Color? border = Focused && ShowFocusCues ? Theme.AccentHover : Primary || Ghost ? null : Theme.Border;
        Ui.Round(g, new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(6), fill, border);

        var color = !Enabled ? Theme.Muted : Primary ? Color.White : Theme.Text;
        var glyphColor = !Enabled ? Theme.Muted : Primary ? Color.White : GlyphColor;
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        const TextFormatFlags centered = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        if (Glyph == null) TextRenderer.DrawText(g, Text, Font, ClientRectangle, color, flags | TextFormatFlags.HorizontalCenter);
        else if (Text.Length == 0) TextRenderer.DrawText(g, Glyph, Theme.Icon, ClientRectangle, glyphColor, centered);
        else
        {
            int pad = LogicalToDeviceUnits(12), icon = LogicalToDeviceUnits(20);
            TextRenderer.DrawText(g, Glyph, Theme.Icon, new Rectangle(pad, 0, icon, Height), glyphColor, centered);
            var left = pad + icon + LogicalToDeviceUnits(10);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Width - left - pad, Height), color, flags);
        }
    }
}

// Slim rounded progress bar.
sealed class Meter : Control
{
    int value;

    public int Value
    {
        get => value;
        set
        {
            if (this.value == value) return;
            this.value = value;
            Invalidate();
        }
    }

    public Meter() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Window);
        var radius = Height / 2;
        Ui.Round(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), radius, Theme.Raised);
        if (value > 0)
            Ui.Round(e.Graphics, new Rectangle(0, 0, Math.Max(Height, (Width - 1) * value / 100), Height - 1), radius, Theme.Accent);
    }
}

sealed class PasswordDialog : Form
{
    readonly TextBox password = Ui.Box(300);
    readonly TextBox again = Ui.Box(300);

    PasswordDialog(string prompt, bool confirm)
    {
        password.UseSystemPasswordChar = again.UseSystemPasswordChar = true;
        var show = new CheckBox { Text = "Mostrar senha", AutoSize = true, Margin = new Padding(3, 10, 3, 0) };
        show.CheckedChanged += (_, _) => password.UseSystemPasswordChar = again.UseSystemPasswordChar = !show.Checked;

        Control[] rows = confirm
            ? [Ui.Label("Senha"), password, Ui.Label("Confirme a senha"), again, show]
            : [Ui.Label("Senha"), password, show];
        Ui.Build(this, prompt, rows);

        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && confirm && password.Text != again.Text)
            {
                Ui.Error(this, "As senhas não coincidem.");
                e.Cancel = true;
            }
        };
    }

    public static string? Ask(IWin32Window? owner, string prompt, bool confirm)
    {
        using var dialog = new PasswordDialog(prompt, confirm);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.password.Text : null;
    }
}

enum OverwriteMode { Ask, Overwrite, Skip }

sealed class ExtractDialog : Form
{
    readonly TextBox destination = Ui.Box(380);
    readonly RadioButton ask = new() { Text = "Perguntar antes de substituir", AutoSize = true, Checked = true };
    readonly RadioButton overwrite = new() { Text = "Substituir sem perguntar", AutoSize = true };
    readonly RadioButton skip = new() { Text = "Ignorar arquivos existentes", AutoSize = true };

    public string Destination => destination.Text.Trim();
    public OverwriteMode Mode => overwrite.Checked ? OverwriteMode.Overwrite : skip.Checked ? OverwriteMode.Skip : OverwriteMode.Ask;

    public ExtractDialog(string defaultDestination)
    {
        destination.Text = defaultDestination;
        destination.Margin = new Padding(3, 5, 3, 3);
        var browse = new RoundButton { Text = "Procurar...", Height = 30 };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { InitialDirectory = Destination };
            if (d.ShowDialog(this) == DialogResult.OK) destination.Text = d.SelectedPath;
        };

        Ui.Build(this, "Extrair arquivos",
            Ui.Label("Pasta de destino (será criada se não existir)"), Ui.Row(destination, browse),
            Ui.Label("Se o arquivo já existir"), ask, overwrite, skip);
    }
}

// A Panel instead of a GroupBox: in dark mode a GroupBox holding radio buttons keeps redrawing a white frame.
sealed class Frame : Panel
{
    public Frame()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    protected override void OnPaint(PaintEventArgs e) => Ui.PaintFrame(this, e);

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate(); // caption color follows Enabled
    }
}

// Redraws a thin border after the ComboBox paints: the flat style flashes a white border on mouse hover in dark mode.
sealed class ComboBorder : NativeWindow
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, ComboBorder> Attached = [];
    readonly ComboBox combo;

    ComboBorder(ComboBox combo)
    {
        this.combo = combo;
        AssignHandle(combo.Handle);
        combo.HandleDestroyed += (_, _) => ReleaseHandle();
    }

    public static void Attach(ComboBox combo) => Attached.AddOrUpdate(combo, new ComboBorder(combo));

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F) return; // WM_PAINT
        using var g = Graphics.FromHwnd(combo.Handle);
        using var border = new Pen(Theme.Border);
        using var inner = new Pen(combo.Enabled ? combo.BackColor : SystemColors.Control);
        g.DrawRectangle(border, 0, 0, combo.Width - 1, combo.Height - 1);
        g.DrawRectangle(inner, 1, 1, combo.Width - 3, combo.Height - 3);
    }
}
