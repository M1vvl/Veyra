using System.Runtime.InteropServices;

namespace GlassDock.Services;

public sealed record AudioState(double Volume, bool Muted, bool Available, string? Error);

/// <summary>Direct controls for the current default multimedia output endpoint.</summary>
public sealed class AudioService : IDisposable
{
    private bool _disposed;

    public AudioState Read() => WithEndpoint(ReadEndpoint);

    public AudioState SetVolume(double volume)
    {
        if (!double.IsFinite(volume)) return Unavailable("音量数值无效。");
        return WithEndpoint(endpoint =>
        {
            Guid context = Guid.Empty;
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(volume, 0, 1), ref context));
            if(volume>0)
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
                if(muted) Marshal.ThrowExceptionForHR(endpoint.SetMute(false,ref context));
            }
            return ReadEndpoint(endpoint);
        });
    }

    public AudioState SetMuted(bool muted) => WithEndpoint(endpoint =>
    {
        Guid context = Guid.Empty;
        Marshal.ThrowExceptionForHR(endpoint.SetMute(muted, ref context));
        return ReadEndpoint(endpoint);
    });

    private AudioState WithEndpoint(Func<IAudioEndpointVolume, AudioState> operation)
    {
        if (_disposed) return Unavailable("音量控制已关闭。");
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? activated = null;
        int initialized = CoInitializeEx(nint.Zero, 2);
        try
        {
            // RPC_E_CHANGED_MODE means the caller already has a different valid apartment.
            if (initialized < 0 && initialized != unchecked((int)0x80010106))
                Marshal.ThrowExceptionForHR(initialized);
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
            Guid endpointId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref endpointId, 23, nint.Zero, out activated));
            return operation((IAudioEndpointVolume)activated);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return Unavailable("无法访问当前声音输出设备。" + ex.Message);
        }
        finally
        {
            Release(activated);
            Release(device);
            Release(enumerator);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static AudioState ReadEndpoint(IAudioEndpointVolume endpoint)
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float volume));
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
        return new AudioState(Math.Clamp(volume, 0, 1), muted, true, null);
    }

    private static AudioState Unavailable(string message) => new(0, false, false, message);
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    // Every call acquires and releases its own endpoint. The next call therefore observes
    // default-device changes, unplugged devices, and a restarted audio service.
    public void Dispose() => _disposed = true;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    // Declared in endpointvolume.h order. Unused slots remain present to preserve the ABI.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint callback);
        [PreserveSig] int UnregisterControlChangeNotify(nint callback);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
