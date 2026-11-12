using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlassDock.DesktopOrganization;

internal readonly record struct DesktopWindowMove(Window Window,Point Origin);

/// <summary>Commits one native position change per window, preserving focus, size and stacking.</summary>
internal static class DesktopWindowMotion
{
    internal static bool SetVisible(Window[] windows,bool visible)
    {
        if(windows.Length==0)return true;
        nint batch=BeginDeferWindowPos(windows.Length); if(batch==0)return false;
        foreach(var window in windows)
        {
            var handle=new WindowInteropHelper(window).Handle; if(handle==0)continue;
            batch=DeferWindowPos(batch,handle,0,0,0,0,0,0x0217u|(visible?0x40u:0x80u)); if(batch==0)return false;
        }
        return EndDeferWindowPos(batch);
    }
    internal static bool Apply(ReadOnlySpan<DesktopWindowMove> moves)
    {
        nint batch=BeginDeferWindowPos(moves.Length);
        if(batch==0)return false;
        foreach(var move in moves)
        {
            batch=DeferWindowPos(batch,new WindowInteropHelper(move.Window).Handle,0,(int)Math.Round(move.Origin.X),(int)Math.Round(move.Origin.Y),0,0,0x0215);
            if(batch==0)return false;
        }
        return EndDeferWindowPos(batch);
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint DeferWindowPos(nint batch,nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool EndDeferWindowPos(nint batch);
}
