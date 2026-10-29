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
            using var hp = new Telemetry.HpBios();
            File.WriteAllLines(args[1] + ".hp.txt", new[] { hp.Probe(0x23, 4, 1), hp.Probe(0x23, 4, 0), hp.Probe(0x23, 128, 1), hp.Probe(0x2D, 128, 0), hp.Probe(0x2C, 128, 0), hp.Probe(0x10, 4, 0) });
            return;
        }
        Application.ThreadException += (_, e) => MainForm.Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) MainForm.Log(ex); };
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
