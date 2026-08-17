namespace MFlacDrop;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AudioConverter.CleanupStaleTempDirectories();
        if (args.Length > 0 && args[0].Equals("--diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = Diagnostics.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = CliMode.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0].Equals("--gui-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunGuiSmoke();
            return;
        }
        ApplicationConfiguration.Initialize();
        if (!RetailLicenseService.EnsureRetailLicenseInteractive()) return;
        Application.Run(new MainForm());
    }

    private static void RunGuiSmoke()
    {
        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        using var timer = new System.Windows.Forms.Timer { Interval = 250 };
        bool shown = false;
        form.Shown += (_, _) =>
        {
            shown = true;
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (!shown) throw new InvalidOperationException("MainForm.Shown was not raised.");
                if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                    throw new InvalidOperationException("GUI thread is not STA.");
                if (form.Text != AppInfo.WindowTitle)
                    throw new InvalidOperationException("Unexpected window title: " + form.Text);
                string[] expected = { "原始格式", "FLAC", "WAV", "MP3", "OGG" };
                if (!form.OutputFormats.SequenceEqual(expected, StringComparer.Ordinal))
                    throw new InvalidOperationException("Output format list is incomplete.");
                Environment.ExitCode = 0;
            }
            catch
            {
                Environment.ExitCode = 70;
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
        if (!shown && Environment.ExitCode == 0) Environment.ExitCode = 71;
    }
}
