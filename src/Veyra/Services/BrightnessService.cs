using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace GlassDock.Services;

public sealed record BrightnessState(double Brightness, bool Available, string? Error, string Method);

/// <summary>Direct WMI panel brightness, with DDC/CI support for an attached external monitor.</summary>
public sealed class BrightnessService : IDisposable
{
    private readonly nint _ownerWindow;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _disposed;

    public BrightnessService(nint ownerWindow = default) => _ownerWindow = ownerWindow;

    public BrightnessState Read() => Execute(null);

    public BrightnessState SetBrightness(double brightness)
    {
        if (!double.IsFinite(brightness)) return Unavailable("亮度数值无效。");
        return Execute(Math.Clamp(brightness, 0, 1));
    }

    public Task<BrightnessState> ReadAsync() => ExecuteAsync(null);
    public Task<BrightnessState> SetBrightnessAsync(double brightness) => double.IsFinite(brightness)
        ? ExecuteAsync(Math.Clamp(brightness, 0, 1))
        : Task.FromResult(Unavailable("亮度数值无效。"));

    private async Task<BrightnessState> ExecuteAsync(double? value)
    {
        if (!await _operations.WaitAsync(0).ConfigureAwait(false))
            return Unavailable("显示器正在处理上一项亮度操作。");
        try { return await Task.Run(() => Execute(value)).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private BrightnessState Execute(double? value)
    {
        if (_disposed) return Unavailable("亮度控制已关闭。");
        var panel = TryPanelBrightness(value);
        if (panel.Available || panel.Method == "WMI") return panel;
        var external = TryExternalBrightness(value);
        if (external.Available) return external;
        return Unavailable("当前显示器未提供可用的系统亮度控制。" + panel.Error + " " + external.Error);
    }

    private static BrightnessState TryPanelBrightness(double? value)
    {
        object? locator = null;
        object? services = null;
        object? collection = null;
        object? monitor = null;
        bool panelFound = false;
        int initialized = CoInitializeEx(nint.Zero, 2);
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106))
                Marshal.ThrowExceptionForHR(initialized);
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return Unavailable("WMI 亮度提供程序不可用。");
            locator = Activator.CreateInstance(locatorType);
            services = ((dynamic)locator!).ConnectServer(".", @"root\wmi");
            // A synchronous query gives an indexable object set and no hidden COM enumerator.
            collection = ((dynamic)services).ExecQuery("SELECT * FROM WmiMonitorBrightness WHERE Active=TRUE", "WQL", 0);
            if (Convert.ToInt32(((dynamic)collection).Count) == 0)
                return Unavailable("没有活动的内置亮度面板。");
            panelFound = true;
            monitor = ((dynamic)collection).ItemIndex(0);
            string instanceName = Convert.ToString(((dynamic)monitor).InstanceName) ?? "";
            double current = Convert.ToDouble(((dynamic)monitor).CurrentBrightness) / 100d;
            if (value is null) return new BrightnessState(current, true, null, "WMI");
            Release(monitor); monitor = null;
            Release(collection); collection = null;
            collection = ((dynamic)services).ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE", "WQL", 0);
            int count = Convert.ToInt32(((dynamic)collection).Count);
            for (int i = 0; i < count; i++)
            {
                monitor = ((dynamic)collection).ItemIndex(i);
                if (string.Equals(Convert.ToString(((dynamic)monitor).InstanceName), instanceName, StringComparison.OrdinalIgnoreCase))
                {
                    uint result = Convert.ToUInt32(((dynamic)monitor).WmiSetBrightness(0u, (byte)Math.Round(value.Value * 100)));
                    if (result != 0) return Unavailable($"显示面板拒绝亮度修改，返回值 {result}。", "WMI");
                    return new BrightnessState(value.Value, true, null, "WMI");
                }
                Release(monitor); monitor = null;
            }
            return Unavailable("显示面板未提供亮度写入接口。", "WMI");
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException)
        {
            return Unavailable(ex.Message, panelFound ? "WMI" : "unavailable");
        }
        finally
        {
            Release(monitor);
            Release(collection);
            Release(services);
            Release(locator);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private BrightnessState TryExternalBrightness(double? value)
    {
        PhysicalMonitor[]? monitors = null;
        try
        {
            nint logicalMonitor = MonitorFromWindow(_ownerWindow, 1);
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out uint count) || count == 0)
                return Unavailable("未找到 DDC/CI 物理显示器。");
            monitors = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, monitors))
                return Unavailable(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            foreach (var monitor in monitors)
            {
                if (!GetMonitorCapabilities(monitor.Handle, out uint capabilities, out _) || (capabilities & 2) == 0)
                    continue;
                if (!GetMonitorBrightness(monitor.Handle, out uint minimum, out uint current, out uint maximum) || maximum <= minimum)
                    continue;
                if (value is null)
                    return new BrightnessState((current - minimum) / (double)(maximum - minimum), true, null, "DDC/CI");
                uint requested = minimum + (uint)Math.Round(value.Value * (maximum - minimum));
                if (!SetMonitorBrightness(monitor.Handle, requested))
                    return Unavailable(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return new BrightnessState(value.Value, true, null, "DDC/CI");
            }
            return Unavailable("显示器未报告 DDC/CI 亮度能力。");
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            return Unavailable(ex.Message);
        }
        finally
        {
            if (monitors is not null)
                foreach (var monitor in monitors)
                    if (monitor.Handle != nint.Zero) DestroyPhysicalMonitor(monitor.Handle);
        }
    }

    private static BrightnessState Unavailable(string message, string method = "unavailable") => new(0, false, message, method);
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public void Dispose() => _disposed = true;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorCapabilities(nint monitor, out uint capabilities, out uint colorTemperatures);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(nint monitor, out uint minimum, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(nint monitor, uint brightness);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(nint monitor);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
