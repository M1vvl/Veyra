using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GlassDock.Music;

/// <summary>Read-only native playback clock. Discovery samples advancing values and
/// confirms matching copies. No injection, target writes or fixed memory offsets.
/// Algorithm adapted from maker114/netease-lyric-api (MIT), see ThirdParty/NetEaseClock.</summary>
internal sealed class NeteaseBackgroundClock : IDisposable
{
    internal sealed record Sample(double Seconds,long At);
    private readonly record struct Candidate(long Address,int Value,long At);
    private readonly CancellationTokenSource _stop=new();
    private readonly object _lifecycleGate=new();
    private SafeProcessHandle? _handle;
    private long[] _addresses=[];
    private int _busy,_disposed;
    private long _retryAfter,_processCheckAfter;
    private Sample? _sample;
    internal Sample? Read()
    {
        lock(_lifecycleGate)
        {
            if(_disposed!=0)return null;
            if(Interlocked.CompareExchange(ref _busy,1,0)==0)
                _=Task.Run(()=> {try {Update();} catch(Exception ex) {Program.Log("NetEase clock: "+ex.Message); _retryAfter=Environment.TickCount64+15000;} finally {Volatile.Write(ref _busy,0);}});
        }
        var sample=Volatile.Read(ref _sample);
        return sample is not null && Environment.TickCount64-sample.At<1800?sample:null;
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
            return !_handle.IsInvalid;
        }
        finally {foreach(var process in processes)process.Dispose();}
    }
    private void Update()
    {
        if(_stop.IsCancellationRequested)return;
        if(!EnsureProcess()) {Volatile.Write(ref _sample,null); return;}
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
            Volatile.Write(ref _sample,new Sample(populated[count/2]/1000d,Environment.TickCount64)); return;
        }
        Volatile.Write(ref _sample,null);
        if(Environment.TickCount64<_retryAfter)return;
        _retryAfter=Environment.TickCount64+15000;
        Discover();
    }
    private int? ReadInt(long address)
    {
        return ReadProcessMemory(_handle!,new nint(address),out int value,4,out nuint read) && read==4?value:null;
    }
    private void Discover()
    {
        var candidates=new List<Candidate>();
        byte[] buffer=new byte[1024*1024];
        long cursor=0,total=0;
        var clock=Stopwatch.StartNew();
        while(!_stop.IsCancellationRequested && candidates.Count<400000 && total<512L*1024*1024 && clock.ElapsedMilliseconds<6000)
        {
            if(VirtualQueryEx(_handle!,new nint(cursor),out var region,(nuint)Marshal.SizeOf<MemoryRegion>())==0)break;
            long size=checked((long)region.Size),next=region.Address.ToInt64()+size;
            if(next<=cursor)break;
            if(region.State==0x1000 && region.Type==0x20000 && (region.Protect&0x100)==0 && (region.Protect&0xff) is 4 or 8 or 64 or 128)
            {
                for(long offset=0;offset<size && candidates.Count<400000 && total<512L*1024*1024;offset+=buffer.Length)
                {
                    if(_stop.IsCancellationRequested)return;
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
        // Require the same advancing time across two separated observations.
        for(int pass=0;pass<2;pass++)
        {
            if(_stop.Token.WaitHandle.WaitOne(900))return;
            var refined=new List<Candidate>();
            foreach(var item in candidates)
            {
                if(_stop.IsCancellationRequested)return;
                int? value=ReadInt(item.Address);long now=Environment.TickCount64;
                if(value.HasValue && Math.Abs(value.Value-item.Value-(now-item.At))<260)
                    refined.Add(new Candidate(item.Address,value.Value,now));
            }
            candidates=refined;
        }
        var clusters=candidates.GroupBy(item=>item.Value/250).OrderByDescending(group=>group.Count());
        var best=clusters.FirstOrDefault(group=>group.Count()>=2);
        if(best is null)return;
        if(_stop.IsCancellationRequested)return;
        _addresses=best.Take(8).Select(item=>item.Address).ToArray();
        var readings=_addresses.Select(ReadInt).Where(v=>v.HasValue).Select(v=>v!.Value).Order().ToArray();
        if(readings.Length>=2)Volatile.Write(ref _sample,new Sample(readings[readings.Length/2]/1000d,Environment.TickCount64));
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
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(SafeProcessHandle handle,out uint code);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(SafeProcessHandle handle,nint address,out MemoryRegion region,nuint length);
}
