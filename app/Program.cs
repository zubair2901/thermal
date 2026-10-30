namespace Thermal;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Thermal.exe --dump <file>: take a few readings and write them as JSON, for diagnosing sensor support.
        if (args.Length == 2 && args[0] == "--dump")
        {
            using var c = new Telemetry.Collector();
            Telemetry.Snapshot? s = null;
            for (var i = 0; i < 3; i++) { s = c.Collect(); Thread.Sleep(1000); }
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
            return;
        }

        // One instance only: a second launch asks the running one to show its window.
        using var mutex = new Mutex(true, @"Local\Thermal.SingleInstance", out var first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Thermal.Show");
        if (!first)
        {
            showSignal.Set();
            return;
        }

        Application.ThreadException += (_, e) => MainForm.Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) MainForm.Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => MainForm.Log(e.Exception);
        MainForm.Trace($"start {Application.ProductVersion} args={string.Join(" ", args)}");
        ApplicationConfiguration.Initialize();
        var form = new MainForm(startHidden: args.Contains("--tray"));
        new Thread(() => { while (showSignal.WaitOne()) form.ShowMain(); }) { IsBackground = true }.Start();
        Application.Run(form);
    }
}
