using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDock.Shell;

/// <summary>Includes non-desktop Start windows omitted by EnumWindows on Windows 8 and later.</summary>
internal static class NativeStartWindows
{
    internal static IReadOnlyList<nint> Enumerate(IReadOnlyCollection<uint> verifiedPids)
    {
        ArgumentNullException.ThrowIfNull(verifiedPids);
        if (verifiedPids.Count == 0) return Array.Empty<nint>();

        var allowed = new HashSet<uint>(verifiedPids);
        var found = new HashSet<nint>();
        var windows = new List<nint>();
        EnumWindowsProc collect = (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (allowed.Contains(processId) && found.Add(window)) windows.Add(window);
            return true;
        };

        foreach (uint processId in allowed)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                foreach (ProcessThread thread in process.Threads)
                {
                    using (thread) EnumThreadWindows((uint)thread.Id, collect, 0);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // A verified Start process or one of its threads may exit during the snapshot.
            }
        }

        EnumWindows(collect, 0);
        nint previous=0;
        while((previous=FindWindowEx(0,previous,null,null))!=0) collect(previous,0);
        GC.KeepAlive(collect);
        return windows;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint FindWindowEx(nint parent,nint after,string? className,string? title);
}
