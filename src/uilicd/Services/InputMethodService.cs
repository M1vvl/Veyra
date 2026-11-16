using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassDock.Services;

public sealed record InputLayout(nint Handle, string Name, string ShortName);

public sealed record InputMethodState(nint TargetWindow, nint LayoutHandle, string LayoutName,
    bool IsChinese, bool? NativeComposition, bool CanToggleComposition, string? Error)
{
    public string Indicator => TargetWindow == 0 || LayoutHandle == 0
        ? "—"
        : IsChinese
            ? NativeComposition switch { true => "中", false => "英", null => "—" }
            : ShortLanguage(LayoutHandle);

    private static string ShortLanguage(nint layout)
    {
        ushort language = unchecked((ushort)(long)layout);
        if ((language & 0x03ff) == 0x0009) return "英";
        try { return CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch (CultureNotFoundException) { return "—"; }
    }
}

public sealed record InputMethodOperation(bool Succeeded, bool Verified, string? Error);

/// <summary>
/// Explicit input actions target the application captured before GlassDock's own panel opens.
/// Keyboard-layout requests use the application's focused window. Composition changes use
/// its default IME window, avoiding another thread's private input-context handle.
/// Modern TSF-only input methods can reject these compatibility messages.
/// </summary>
public static class InputMethodService
{
    private const uint WmInputLanguageChangeRequest = 0x0050;
    private const uint WmImeControl = 0x0283;
    private const nuint GetConversionMode = 0x0001;
    private const nuint SetConversionMode = 0x0002;
    private const nuint GetOpenStatus = 0x0005;
    private const nuint SetOpenStatus = 0x0006;
    private const uint NativeConversionMode = 0x0001;
    private const uint MessageTimeoutMilliseconds = 100;
    private sealed record ApplicationTarget(nint Window, uint Thread, uint Process);
    private static ApplicationTarget? _lastApplication;

    public static InputMethodState Read(nint target = 0)
    {
        var window = ResolveTarget(target);
        if (window == 0)
            return new(0, 0, "未选择输入应用", false, null, false, "请先在需要输入的应用中点击文本框。");

        var thread = GetWindowThreadProcessId(window, out uint process);
        var layout = GetKeyboardLayout(thread);
        if (thread == 0 || layout == 0)
            return new(window, layout, "输入状态不可用", false, null, false, "无法读取当前输入应用。");
        bool chinese = (unchecked((ushort)(long)layout) & 0x03ff) == 0x0004;
        string name = DescribeLayout(layout).Name;
        if (!chinese)
            return new(window, layout, name, false, null, false, null);

        var ime = ImmGetDefaultIMEWnd(window);
        if (ime == 0)
            return new(window, layout, name, true, null, false, "当前应用未提供可控制的输入法兼容窗口。");
        if (!TryImeMessage(ime, GetOpenStatus, 0, out var open) ||
            !TryImeMessage(ime, GetConversionMode, 0, out var mode))
            return new(window, layout, name, true, null, false, "当前输入法的中英文状态不可读，可能由 TSF 或应用权限管理。");
        if (!SameContext(window, thread, process, layout, ime))
            return new(window, layout, name, true, null, false, "输入目标已变化。");

        bool native = open != 0 && (unchecked((uint)mode) & NativeConversionMode) != 0;
        return new(window, layout, name, true, native, true, null);
    }

    public static IReadOnlyList<InputLayout> ListLayouts()
    {
        int count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return Array.Empty<InputLayout>();
        var layouts = new nint[count];
        int received = GetKeyboardLayoutList(layouts.Length, layouts);
        return layouts.Take(Math.Max(0, received)).Where(layout => layout != 0)
            .Distinct().Select(DescribeLayout).ToArray();
    }

    public static InputMethodOperation SetLayout(nint layout, nint target = 0)
    {
        var window = ResolveTarget(target);
        if (window == 0)
            return new(false, false, "请先选择需要切换输入语言的应用。");
        if (!ListLayouts().Any(item => item.Handle == layout))
            return new(false, false, "所选输入语言已不可用，请刷新列表。");
        if (!PostMessage(window, WmInputLanguageChangeRequest, 0, layout))
            return new(false, false, "应用未接收输入语言请求，错误代码 " + Marshal.GetLastWin32Error() + "。");

        var current = GetKeyboardLayout(GetWindowThreadProcessId(window, out _));
        // Posted language requests can be accepted or rejected by the target application.
        return new(true, current == layout, null);
    }

    public static InputMethodOperation ToggleComposition(nint target = 0)
    {
        var state = Read(target);
        if (!state.CanToggleComposition || state.NativeComposition is null)
            return new(false, false, state.Error ?? "当前输入语言未提供中英文组合模式。");

        var thread = GetWindowThreadProcessId(state.TargetWindow, out uint process);
        var ime = ImmGetDefaultIMEWnd(state.TargetWindow);
        if (!TryImeMessage(ime, GetOpenStatus, 0, out var originalOpen) ||
            !TryImeMessage(ime, GetConversionMode, 0, out var currentMode) ||
            !SameContext(state.TargetWindow, thread, process, state.LayoutHandle, ime) || !IsCurrentInputThread(thread))
            return new(false, false, "输入法状态已变化，请重试。");

        bool currentlyNative = originalOpen != 0 && (unchecked((uint)currentMode) & NativeConversionMode) != 0;
        bool desiredNative = !currentlyNative;
        uint mode = unchecked((uint)currentMode);
        mode = desiredNative ? mode | NativeConversionMode : mode & ~NativeConversionMode;
        bool opened = desiredNative && originalOpen == 0;
        if (opened && (!TryImeMessage(ime, SetOpenStatus, 1, out var openResult) || openResult != 0))
            return new(false, false, "当前输入法未接收开启请求。");
        if (!TryImeMessage(ime, SetConversionMode, unchecked((nint)mode), out var conversionResult) || conversionResult != 0)
        {
            if (opened && SameContext(state.TargetWindow, thread, process, state.LayoutHandle, ime))
                TryImeMessage(ime, SetOpenStatus, 0, out _);
            return new(false, false, "当前输入法未接收中英文模式请求。");
        }

        int confirmations = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt != 0) Thread.Sleep(30);
            if (!SameContext(state.TargetWindow, thread, process, state.LayoutHandle, ime) || !IsCurrentInputThread(thread))
                return new(false, false, "输入目标已变化。");
            var updated = Read(state.TargetWindow);
            if (updated.TargetWindow != state.TargetWindow || updated.LayoutHandle != state.LayoutHandle)
                return new(false, false, "输入焦点已变化。");
            confirmations = updated.CanToggleComposition && updated.NativeComposition == desiredNative
                ? confirmations + 1
                : 0;
            if (confirmations == 2) return new(true, true, null);
        }
        return new(false, false, "输入法未确认切换。");
    }

    private static nint ResolveTarget(nint supplied)
    {
        var candidate = supplied;
        if (candidate == 0)
        {
            candidate = GetForegroundWindow();
            if (IsApplicationWindow(candidate))
            {
                uint candidateThread = GetWindowThreadProcessId(candidate, out uint process);
                Volatile.Write(ref _lastApplication, new ApplicationTarget(candidate, candidateThread, process));
            }
            else
            {
                GetWindowThreadProcessId(candidate, out uint foregroundProcess);
                if (foregroundProcess != Environment.ProcessId) return 0;
                var previous = Volatile.Read(ref _lastApplication);
                if (previous is null || GetWindowThreadProcessId(previous.Window, out uint process) != previous.Thread || process != previous.Process)
                    return 0;
                candidate = previous.Window;
            }
        }
        if (!IsApplicationWindow(candidate)) return 0;

        var thread = GetWindowThreadProcessId(candidate, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(thread, ref info) && IsApplicationWindow(info.Focus) &&
            GetWindowThreadProcessId(info.Focus, out _) == thread)
            return info.Focus;
        return candidate;
    }

    private static bool IsApplicationWindow(nint window)
    {
        if (window == 0 || !IsWindow(window)) return false;
        if (GetWindowThreadProcessId(window, out uint process) == 0 || process == Environment.ProcessId)
            return false;
        var name = new StringBuilder(64);
        GetClassName(GetAncestor(window, 2), name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    private static bool SameContext(nint window, uint thread, uint process, nint layout, nint ime) =>
        IsApplicationWindow(window) && thread != 0 &&
        GetWindowThreadProcessId(window, out uint currentProcess) == thread && currentProcess == process &&
        GetKeyboardLayout(thread) == layout && ImmGetDefaultIMEWnd(window) == ime;

    private static bool IsCurrentInputThread(uint thread)
    {
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
        return foregroundThread == thread || process == Environment.ProcessId;
    }

    private static bool TryImeMessage(nint window, nuint command, nint argument, out nuint result)
    {
        result = 0;
        if (window == 0 || !IsWindow(window)) return false;
        return SendMessageTimeout(window, WmImeControl, command, argument,
            0x0002 | 0x0020, MessageTimeoutMilliseconds, out result) != 0;
    }

    private static InputLayout DescribeLayout(nint layout)
    {
        ushort language = unchecked((ushort)(long)layout);
        string languageName;
        string shortName;
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            languageName = culture.DisplayName;
            shortName = culture.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            languageName = "输入语言 " + language.ToString("X4", CultureInfo.InvariantCulture);
            shortName = "文";
        }
        var description = new StringBuilder(256);
        if (ImmGetDescription(layout, description, (uint)description.Capacity) > 0)
            languageName += " · " + description;
        return new(layout, languageName, shortName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRectangle;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("imm32.dll")] private static extern nint ImmGetDefaultIMEWnd(nint window);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern uint ImmGetDescription(nint layout, StringBuilder text, uint count);
}
