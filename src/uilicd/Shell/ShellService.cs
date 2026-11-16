using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDock.Shell;

/// <summary>Delegates system UI to Windows, retaining the user's actual network, audio and IME controls.</summary>
public static class ShellService
{
    public static void Start()
    {
        // SC_TASKLIST is the documented native Start command. Dispatching it to
        // Explorer avoids injecting a modifier key into the foreground app.
        var taskbar=TaskbarLease.FindExplorerTaskbar();
        if(taskbar==0 || !PostMessage(taskbar,0x0112,0xF130,0))
            throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows 未接收开始菜单命令。");
    }
    public static void Search() => Shortcut(0x5B, 0x53);
    public static void QuickSettings() => Shortcut(0x5B, 0x41);
    public static void Notifications() => Shortcut(0x5B, 0x4E);
    public static void Calendar() => Notifications();
    public static void InputSwitcher() => Shortcut(0x5B, 0x20);
    public static void Desktop() => Shortcut(0x5B, 0x44);
    public static void Settings() => OpenUri("ms-settings:");
    public static void Network() => QuickSettings();
    public static void Volume() => QuickSettings();
    public static void NetworkSettings() => OpenUri("ms-settings:network");
    public static void SoundSettings() => OpenUri("ms-settings:sound");
    public static void BluetoothSettings() => OpenUri("ms-settings:bluetooth");

    /// <summary>Checks interop layout without sending input.</summary>
    public static bool HasValidInputLayout => Marshal.SizeOf<Input>() == (IntPtr.Size == 8 ? 40 : 28);

    public static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    internal static void Shortcut(params ushort[] keys)
    {
        var inputs = new Input[keys.Length * 2];
        for (int i = 0; i < keys.Length; ++i)
        {
            inputs[i] = Key(keys[i], false);
            inputs[keys.Length + i] = Key(keys[keys.Length - 1 - i], true);
        }
        uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (inserted != inputs.Length)
        {
            // Do not leave injected modifier keys down after a partial SendInput call.
            foreach (ushort key in keys) SendInput(1, new[] { Key(key, true) }, Marshal.SizeOf<Input>());
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未接收完整的系统快捷键。请使用与资源管理器相同的权限运行 Dock。");
        }
    }

    private static Input Key(ushort key, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window,uint message,nint wParam,nint lParam);
}
