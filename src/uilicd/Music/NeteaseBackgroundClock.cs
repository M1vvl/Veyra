using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GlassDock.Music;

/// <summary>Read-only native playback clock. Discovery samples advancing values and
/// confirms matching copies. No injection, target writes or fixed memory offsets.
/// Algorithm adapted from maker114/netease-lyric-api (MIT), see ThirdParty/NetEaseClock.</summary>
internal sealed class NeteaseBackgroundClock : IDisposable
{
    internal sealed record Sample(double Seconds,long At,int Revision);
    private readonly record struct Candidate(long Address,int Value,long At);
    private readonly CancellationTokenSource _stop=new();
    private readonly object _lifecycleGate=new();
    private SafeProcessHandle? _handle;
    private long[] _addresses=[];
    private long _progressAddress;
    private int _busy,_disposed;
    private int _revision,_observedRevision;
    private volatile bool _playing;
    private double? _previousTrackSeconds;
    private long _lastAdvance;
    private long _retryAfter,_processCheckAfter;
    private Sample? _sample;
    private string? _trackKey;
    internal void TrackChanged(string key)
    {
        if(_trackKey==key)return;
        bool previousTrack=_trackKey is not null;
        _trackKey=key;
        var previous=Volatile.Read(ref _sample);
        _previousTrackSeconds=previousTrack?previous?.Seconds:null;
        Interlocked.Increment(ref _revision);
    }

    internal Sample? Read(bool playing=true)
    {
        bool direct=false;
        lock(_lifecycleGate)
        {
            if(_disposed!=0)return null;
            _playing=playing;
            if(Interlocked.CompareExchange(ref _busy,1,0)==0)
            {
                direct=Volatile.Read(ref _progressAddress)!=0;
                if(!direct)_=Task.Run(UpdateSafely);
            }
        }
        // The media service already owns a background reader. Reading one verified
        // double here avoids another thread-pool hop and one polling-frame delay.
        if(direct)UpdateSafely();
        var sample=Volatile.Read(ref _sample);
        return sample is not null && sample.Revision==Volatile.Read(ref _revision)
            && Environment.TickCount64-sample.At<250?sample:null;
    }

    private void UpdateSafely()
    {
        try {Update();}
        catch(Exception ex) {Program.Log("NetEase clock: "+ex.Message);_retryAfter=Environment.TickCount64+2000;}
        finally {Volatile.Write(ref _busy,0);}
    }
    private bool EnsureProcess()
    {
        long now=Environment.TickCount64;
        if(_handle is not null && !_handle.IsInvalid)
        {
            if(now<_processCheckAfter)return true;
            _processCheckAfter=now+1000;
            if(GetExitCodeProcess(_handle,out uint code) && code==259)return true;
        }
        var processes=Process.GetProcessesByName("cloudmusic");
        try
        {
            var process=processes.OrderBy(p=>p.StartTime).FirstOrDefault();
            if(process is null)return false;
            _handle?.Dispose();
            _handle=OpenProcess(0x410,false,process.Id);
            _addresses=[]; _retryAfter=0; _processCheckAfter=now+1000;
            _progressAddress=0;
            if(!_handle.IsInvalid)
            {
                var module=process.Modules.Cast<ProcessModule>().FirstOrDefault(item=>
                    item.ModuleName.Equals("cloudmusic.dll",StringComparison.OrdinalIgnoreCase));
                if(module is not null && NeteaseProgressSource.Locate(module.FileName) is long rva)
                    _progressAddress=module.BaseAddress.ToInt64()+rva;
            }
            return !_handle.IsInvalid;
        }
        finally {foreach(var process in processes)process.Dispose();}
    }
    private void Update()
    {
        if(_stop.IsCancellationRequested)return;
        int revision=Volatile.Read(ref _revision);
        if(_observedRevision!=revision)
        {
            _observedRevision=revision;
            _retryAfter=0;
            _lastAdvance=Environment.TickCount64;
            Volatile.Write(ref _sample,null);
        }
        if(!EnsureProcess()) {Volatile.Write(ref _sample,null); return;}
        if(_progressAddress!=0 && ReadProcessMemory(_handle!,new nint(_progressAddress),out double nativeSeconds,8,out nuint bytes)
            && bytes==8 && double.IsFinite(nativeSeconds) && nativeSeconds>=0 && nativeSeconds<=86400)
        {
            // This is the exact double written by the player's progress-event producer.
            // Its address remains stable when song-specific heap objects are replaced.
            if(revision!=Volatile.Read(ref _revision))return;
            if(_previousTrackSeconds is >2 && Math.Abs(nativeSeconds-_previousTrackSeconds.Value)<.5)return;
            _previousTrackSeconds=null;
            Volatile.Write(ref _sample,new Sample(nativeSeconds,Environment.TickCount64,revision));
            return;
        }
        var addresses=_addresses;
        Span<int> values=stackalloc int[8];
        int count=0;
        foreach(long address in addresses)
            if(count<values.Length && ReadInt(address) is int value && value>=0 && value<=3600000)values[count++]=value;
        if(_stop.IsCancellationRequested)return;
        var populated=values[..count];
        populated.Sort();
        if(count>=2 && populated[^1]-populated[0]<400)
        {
            double seconds=populated[count/2]/1000d;
            long now=Environment.TickCount64;
            var previous=Volatile.Read(ref _sample);
            bool oldTrack=_previousTrackSeconds is >2 && Math.Abs(seconds-_previousTrackSeconds.Value)<.5;
            if(previous is null || Math.Abs(seconds-previous.Seconds)>.005 || !_playing)_lastAdvance=now;
            if(!oldTrack && now-_lastAdvance<350)
            {
                _previousTrackSeconds=null;
                Volatile.Write(ref _sample,new Sample(seconds,now,revision));
                return;
            }
        }
        Volatile.Write(ref _sample,null);
        if(!_playing || Environment.TickCount64<_retryAfter)return;
        _retryAfter=Environment.TickCount64+2000;
        Discover(revision);
    }
    private int? ReadInt(long address)
    {
        return ReadProcessMemory(_handle!,new nint(address),out int value,4,out nuint read) && read==4?value:null;
    }
    private void Discover(int revision)
    {
        // Track objects are frequently replaced in the same native heap. Search the
        // already verified neighborhoods before scanning the process again.
        if(_addresses.Length>0 && TryNearby(revision))return;
        var candidates=new List<Candidate>();
        byte[] buffer=new byte[1024*1024];
        long cursor=0,total=0;
        var clock=Stopwatch.StartNew();
        while(!_stop.IsCancellationRequested && revision==Volatile.Read(ref _revision)
            && candidates.Count<400000 && total<512L*1024*1024 && clock.ElapsedMilliseconds<6000)
        {
            if(VirtualQueryEx(_handle!,new nint(cursor),out var region,(nuint)Marshal.SizeOf<MemoryRegion>())==0)break;
            long size=checked((long)region.Size),next=region.Address.ToInt64()+size;
            if(next<=cursor)break;
            if(region.State==0x1000 && region.Type==0x20000 && (region.Protect&0x100)==0 && (region.Protect&0xff) is 4 or 8 or 64 or 128)
            {
                for(long offset=0;offset<size && candidates.Count<400000 && total<512L*1024*1024;offset+=buffer.Length)
                {
                    if(_stop.IsCancellationRequested || revision!=Volatile.Read(ref _revision))return;
                    int length=(int)Math.Min(buffer.Length,size-offset);total+=length;
                    if(!ReadProcessMemory(_handle!,region.Address+(nint)offset,buffer,(nuint)length,out nuint read))continue;
                    long at=Environment.TickCount64;
                    for(int i=0;i+4<=(int)read;i+=4)
                    {
                        int value=BitConverter.ToInt32(buffer,i);
                        if(value>=1000 && value<=3600000)candidates.Add(new Candidate(region.Address.ToInt64()+offset+i,value,at));
                        if(candidates.Count>=400000)break;
                    }
                }
            }
            cursor=next;
        }
        Confirm(candidates,revision,300);
    }

    private bool TryNearby(int revision)
    {
        var candidates=new List<Candidate>();
        var visited=new HashSet<long>();
        byte[] buffer=new byte[65536];
        foreach(long address in _addresses)
        {
            for(int offset=-4;offset<=4;offset++)
            {
                if(_stop.IsCancellationRequested || revision!=Volatile.Read(ref _revision))return false;
                long page=(address&~65535L)+offset*65536L;
                if(page<0 || !visited.Add(page))continue;
                if(!ReadProcessMemory(_handle!,new nint(page),buffer,(nuint)buffer.Length,out nuint read))continue;
                long at=Environment.TickCount64;
                for(int i=0;i+4<=(int)read;i+=4)
                {
                    int value=BitConverter.ToInt32(buffer,i);
                    if(value>0 && value<10000)candidates.Add(new Candidate(page+i,value,at));
                    if(candidates.Count>=60000)break;
                }
                if(candidates.Count>=60000)break;
            }
            if(candidates.Count>=60000)break;
        }
        return Confirm(candidates,revision,150);
    }

    private bool Confirm(List<Candidate> candidates,int revision,int interval)
    {
        // Two advancing observations exclude frozen objects left by the previous song.
        for(int pass=0;pass<2;pass++)
        {
            if(candidates.Count<2 || _stop.Token.WaitHandle.WaitOne(interval)
                || revision!=Volatile.Read(ref _revision))return false;
            var refined=new List<Candidate>();
            foreach(var item in candidates)
            {
                if(_stop.IsCancellationRequested || revision!=Volatile.Read(ref _revision))return false;
                int? value=ReadInt(item.Address);long now=Environment.TickCount64;
                if(value.HasValue && value.Value>item.Value && Math.Abs(value.Value-item.Value-(now-item.At))<85)
                    refined.Add(new Candidate(item.Address,value.Value,now));
            }
            candidates=refined;
        }
        var clusters=candidates.GroupBy(item=>item.Value/250).OrderByDescending(group=>group.Count());
        var best=clusters.FirstOrDefault(group=>group.Count()>=2);
        if(best is null)return false;
        if(_stop.IsCancellationRequested || revision!=Volatile.Read(ref _revision))return false;
        _addresses=best.Take(8).Select(item=>item.Address).ToArray();
        var readings=_addresses.Select(ReadInt).Where(v=>v.HasValue).Select(v=>v!.Value).Order().ToArray();
        if(readings.Length>=2)
        {
            _previousTrackSeconds=null;
            _lastAdvance=Environment.TickCount64;
            Volatile.Write(ref _sample,new Sample(readings[readings.Length/2]/1000d,_lastAdvance,revision));
            return true;
        }
        return false;
    }
    public void Dispose()
    {
        lock(_lifecycleGate)
        {
            if(_disposed!=0)return;
            _disposed=1;
        }
        _stop.Cancel();
        Volatile.Write(ref _sample,null);
        _addresses=[];
        if(Volatile.Read(ref _busy)==0) {_handle?.Dispose(); _handle=null; _stop.Dispose(); return;}
        // A cancelled discovery owns its native handle until its current OS read returns.
        _=Task.Run(async()=> {while(Volatile.Read(ref _busy)!=0)await Task.Delay(20); _handle?.Dispose(); _handle=null; _stop.Dispose();});
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryRegion
    {
        internal nint Address,AllocationBase;
        internal uint AllocationProtect;
        internal nuint Size;
        internal uint State,Protect,Type;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadProcessMemory(SafeProcessHandle handle,nint address,byte[] buffer,nuint length,out nuint read);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadProcessMemory(SafeProcessHandle handle,nint address,out int value,nuint length,out nuint read);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadProcessMemory(SafeProcessHandle handle,nint address,out double value,nuint length,out nuint read);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(SafeProcessHandle handle,out uint code);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(SafeProcessHandle handle,nint address,out MemoryRegion region,nuint length);
}
