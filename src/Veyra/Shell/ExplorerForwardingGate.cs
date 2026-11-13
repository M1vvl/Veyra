using System.Runtime.InteropServices;

namespace GlassDock.Shell;

/// <summary>Never repeatedly wait for an unresponsive Explorer during a notification burst.</summary>
internal sealed class ExplorerForwardingGate
{
    internal const uint TimeoutMilliseconds = 40;
    private long _retryAfter;
    internal bool CanSend(long now) => now >= _retryAfter;
    internal void Observe(bool completed, long now) => _retryAfter = completed ? 0 : now + 2000;
    internal bool TryForward(nint window,uint message,nuint wParam,nint lParam,out nuint result)
    {
        result=0;
        if(!CanSend(Environment.TickCount64))return false;
        // BLOCK prevents dispatching nested sent messages while a foreign WndProc is running.
        bool completed=SendMessageTimeout(window,message,wParam,lParam,0x0023,TimeoutMilliseconds,out result)!=0;
        Observe(completed,Environment.TickCount64);
        return completed;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern nint SendMessageTimeout(nint window,uint message,nuint wParam,nint lParam,uint flags,uint timeout,out nuint result);
}
