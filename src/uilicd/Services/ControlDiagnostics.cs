using System.IO;
using System.Text.Json;

namespace GlassDock.Services;

internal static class ControlDiagnostics
{
    private sealed record Check(string Name, string Status, object? Details, string? Error);

    public static int Run(string reportPath)
    {
        var checks = new List<Check>();

        void Capture(string name, Func<Check> read)
        {
            try { checks.Add(read()); }
            catch (Exception exception)
            {
                checks.Add(new Check(name, "fault", null, exception.ToString()));
            }
        }

        Capture("WLAN native layouts", () =>
        {
            WifiService.ValidateLayouts();
            return new Check("WLAN native layouts", "supported", new { PointerBytes = IntPtr.Size }, null);
        });

        Capture("Default audio output", () =>
        {
            using var audio = new AudioService();
            var state = audio.Read();
            return new Check("Default audio output", state.Available ? "supported" : "unavailable",
                new { state.Available, state.Volume, state.Muted }, state.Error);
        });

        Capture("Wi-Fi", () =>
        {
            var state = WifiService.GetSnapshotAsync(false).WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            string status = !state.IsAvailable ? "unavailable" : state.Error is null ? "supported" : "partial";
            return new Check("Wi-Fi", status, new
            {
                state.IsAvailable,
                state.RadioEnabled,
                state.HardwareBlocked,
                InterfaceCount = state.Interfaces.Count,
                NetworkCount = state.Networks.Count,
                SavedProfileCount = state.Networks.Count(network => network.HasProfile),
                InRangeCount = state.Networks.Count(network => network.IsInRange),
                Connected = state.ConnectedName is not null,
                Interfaces = state.Interfaces.Select(item => new { item.RadioEnabled, item.HardwareBlocked, item.State })
            }, state.Error);
        });

        Capture("Bluetooth", () =>
        {
            var state = RadioService.ReadBluetoothAsync().WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            string status = !state.Available ? "unavailable" : state.Error is null ? "supported" : "partial";
            return new Check("Bluetooth", status, new { state.Available, state.Enabled }, state.Error);
        });

        Capture("Display brightness", () =>
        {
            using var brightness = new BrightnessService();
            var state = brightness.ReadAsync().WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            return new Check("Display brightness", state.Available ? "supported" : "unavailable",
                new { state.Available, state.Brightness, state.Method }, state.Error);
        });

        Capture("Input method", () =>
        {
            var state = InputMethodService.Read();
            string status = state.TargetWindow == 0 ? "unavailable" : state.Error is null ? "supported" : "partial";
            return new Check("Input method", status, new
            {
                state.LayoutName,
                state.IsChinese,
                state.NativeComposition,
                state.CanToggleComposition,
                state.Indicator,
                InstalledLayoutCount = InputMethodService.ListLayouts().Count
            }, state.Error);
        });

        Capture("Battery and power plans", () =>
        {
            var battery = PowerService.ReadBattery();
            var plans = PowerService.GetPlans();
            return new Check("Battery and power plans", plans.Count > 0 ? "supported" : "unavailable",
                new { Battery = battery, PlanCount = plans.Count, ActivePlanCount = plans.Count(plan => plan.Active) },
                plans.Count > 0 ? null : "Windows did not return any power plan.");
        });

        string output = JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.Now,
            Mode = "read-only",
            Checks = checks
        }, new JsonSerializerOptions { WriteIndented = true });
        string fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, output);
        Console.WriteLine(output);
        return checks.Any(check => check.Status == "fault") ? 1 : 0;
    }
}
