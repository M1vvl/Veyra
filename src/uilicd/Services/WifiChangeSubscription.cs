using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace GlassDock.Services;

/// <summary>One panel-scoped ACM subscription. The callback only schedules a later read.</summary>
internal sealed class WifiChangeSubscription : IDisposable
{
    private readonly Action _changed;
    private readonly NotificationCallback _callback;
    private nint _handle;
    private int _disposed;

    internal WifiChangeSubscription(Action changed)
    {
        _changed=changed;
        _callback=(_,_)=>Notify();
        NetworkInformation.NetworkStatusChanged+=NetworkChanged;
        uint result=WlanOpenHandle(2,nint.Zero,out _,out _handle);
        if(result==0)result=WlanRegisterNotification(_handle,8,true,_callback,nint.Zero,nint.Zero,out _);
        if(result!=0 && _handle!=nint.Zero) {WlanCloseHandle(_handle,nint.Zero);_handle=nint.Zero;}
    }

    private void NetworkChanged(object sender)=>Notify();
    private void Notify() {if(Volatile.Read(ref _disposed)==0)_changed();}

    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        NetworkInformation.NetworkStatusChanged-=NetworkChanged;
        // WlanCloseHandle waits for callbacks. Keep it off the Dispatcher and retain
        // the delegate until unregistration/close has completed.
        _=Task.Run(()=> {
            if(_handle!=nint.Zero) {
                WlanRegisterNotification(_handle,0,true,null,nint.Zero,nint.Zero,out _);
                WlanCloseHandle(_handle,nint.Zero);_handle=nint.Zero;
            }
            GC.KeepAlive(_callback);
        });
    }

    private delegate void NotificationCallback(nint data,nint context);
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version,nint reserved,out uint negotiated,out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle,nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(nint handle,uint source,[MarshalAs(UnmanagedType.Bool)]bool ignoreDuplicate,NotificationCallback? callback,nint context,nint reserved,out uint previous);
}
