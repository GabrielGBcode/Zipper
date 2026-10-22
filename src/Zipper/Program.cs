namespace Zipper;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        if (ShellIntegration.HandleCommand(args)) return;
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
