using System.Diagnostics;

namespace Zipper;

// Shared between the worker thread (writes) and the progress window (reads every 200 ms).
public sealed class JobProgress
{
    public volatile string File = "";
    public long FileDone, FileTotal, Done, Total, Packed;
    public CancellationToken Token;
    public IWin32Window? Window;
    public Func<Func<bool>, bool> OnUi = ask => ask();
    public Action<bool>? Paused; // lets 7zr.exe be suspended too
    readonly ManualResetEventSlim running = new(true);

    public void Begin(string file, long size) => (File, FileDone, FileTotal) = (file, 0, size);

    // Called after each copied chunk: waits while paused and stops on cancel.
    public void Step(int bytes)
    {
        FileDone += bytes;
        Done += bytes;
        running.Wait(Token);
    }

    public void SetPaused(bool paused)
    {
        if (paused) running.Reset();
        else running.Set();
        Paused?.Invoke(paused);
    }
}

// Progress card: archive, current file, elapsed time, compression ratio and overall progress.
sealed class ProgressForm : Form
{
    readonly JobProgress progress = new();
    readonly CancellationTokenSource cancel = new();
    readonly Stopwatch watch = new();
    readonly Action<JobProgress> work;
    Task? task;
    Exception? error;

    readonly Label file = new() { AutoEllipsis = true };
    readonly Label filePercent = new() { TextAlign = ContentAlignment.TopRight, ForeColor = Theme.Muted };
    readonly Meter fileBar = new();
    readonly Label elapsed = new() { Font = Theme.Heading };
    readonly Label ratio = new() { Font = Theme.Heading };
    readonly Label processed = new() { Font = Theme.Heading };
    readonly Meter totalBar = new();
    readonly RoundButton pause = new() { Text = "Pausar" };
    readonly RoundButton cancelButton = new() { Text = "Cancelar" };

    ProgressForm(string title, string archive, string verb, Action<JobProgress> work)
    {
        this.work = work;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = ActiveForm == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 304);
        Ui.ApplyTheme(this);

        Add(this, new Label { Text = archive, Font = Theme.Title, AutoEllipsis = true }, 24, 18, 392, 32);
        Add(this, new Label { Text = verb, ForeColor = Theme.Muted }, 24, 50, 392);
        Add(this, file, 24, 86, 320);
        Add(this, filePercent, 344, 86, 72);
        Add(this, fileBar, 24, 110, 392, 6);

        Add(this, new Label { Text = "TEMPO DECORRIDO", Font = Theme.Caption, ForeColor = Theme.Muted }, 24, 140, 130);
        Add(this, elapsed, 24, 158, 130, 26);
        Add(this, new Label { Text = "COMPRESSÃO", Font = Theme.Caption, ForeColor = Theme.Muted }, 160, 140, 130);
        Add(this, ratio, 160, 158, 130, 26);
        Add(this, new Label { Text = "PROCESSADO", Font = Theme.Caption, ForeColor = Theme.Muted }, 296, 140, 120);
        Add(this, processed, 296, 158, 120, 26);
        Add(this, totalBar, 24, 200, 392, 10);

        var background = Add(this, new RoundButton { Text = "Segundo plano" }, 24, 248, 124, 34);
        Add(this, pause, 158, 248, 124, 34);
        Add(this, cancelButton, 292, 248, 124, 34);

        background.Click += (_, _) => WindowState = FormWindowState.Minimized;
        pause.Click += (_, _) =>
        {
            var paused = pause.Text == "Pausar";
            progress.SetPaused(paused);
            pause.Text = paused ? "Continuar" : "Pausar";
            if (paused) watch.Stop();
            else watch.Start();
        };
        cancelButton.Click += (_, _) => Close();

        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) => Refresh();
        Shown += (_, _) =>
        {
            progress.Token = cancel.Token;
            progress.Window = this;
            progress.OnUi = ask => Invoke(ask);
            watch.Start();
            timer.Start();
            task = Task.Run(() => work(progress));
            task.ContinueWith(t => BeginInvoke(() =>
            {
                error = t.Exception?.InnerException;
                timer.Stop();
                Close();
            }));
        };
        // closing while the job runs means "cancel": wait for the worker to stop, then close
        FormClosing += (_, e) =>
        {
            if (task == null || task.IsCompleted) return;
            e.Cancel = true;
            cancelButton.Enabled = pause.Enabled = false;
            cancel.Cancel();
            progress.SetPaused(false);
        };
    }

    // Shows the window while `work` runs on a worker thread. Returns the job's exception (OperationCanceledException if cancelled).
    public static Exception? Run(IWin32Window? owner, string title, string archive, string verb, bool minimized, Action<JobProgress> work)
    {
        using var form = new ProgressForm(title, archive, verb, work);
        form.ShowInTaskbar = owner == null;
        if (minimized) form.WindowState = FormWindowState.Minimized;
        form.ShowDialog(owner);
        return form.error;
    }

    new void Refresh()
    {
        file.Text = Path.GetFileName(progress.File);
        filePercent.Text = progress.FileTotal > 0 ? $"{Percent(progress.FileDone, progress.FileTotal)}%" : "";
        fileBar.Value = Percent(progress.FileDone, progress.FileTotal);
        elapsed.Text = watch.Elapsed.ToString(@"hh\:mm\:ss");
        ratio.Text = progress.Done > 0 && progress.Packed > 0 ? $"{Percent(progress.Packed, progress.Done)}%" : "";
        processed.Text = $"{Percent(progress.Done, progress.Total)}%";
        totalBar.Value = Percent(progress.Done, progress.Total);
    }

    static int Percent(long part, long whole) => whole <= 0 ? 0 : (int)Math.Clamp(part * 100 / whole, 0, 100);

    static T Add<T>(Control parent, T control, int x, int y, int width, int height = 0) where T : Control
    {
        control.SetBounds(x, y, width, height > 0 ? height : control.Height);
        parent.Controls.Add(control);
        return control;
    }
}
