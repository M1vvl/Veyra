using System.Runtime.InteropServices;
using System.Text;

namespace GlassDock.Services;

public sealed record PowerPlan(Guid Id,string Name,bool Active);
public sealed record BatteryState(bool Present,int? Percent,bool PluggedIn,bool Saving);
public static class PowerService
{
    public static BatteryState ReadBattery()
    {
        if(!GetSystemPowerStatus(out var s)) return new(false,null,false,false);
        return new(s.Flag!=128,s.Percent<=100?s.Percent:null,s.Ac==1,s.Saving==1);
    }
    public static IReadOnlyList<PowerPlan> GetPlans()
    {
        Guid active=Guid.Empty;
        if(PowerGetActiveScheme(0,out var ptr)==0) { try { active=Marshal.PtrToStructure<Guid>(ptr); } finally { LocalFree(ptr); } }
        var plans=new List<PowerPlan>();
        for(uint i=0;i<32;i++)
        {
            uint bytes=16; var id=Guid.Empty;
            if(PowerEnumerate(0,0,0,16,i,ref id,ref bytes)!=0) break;
            uint size=0; PowerReadFriendlyName(0,ref id,0,0,null,ref size);
            if(size==0 || size>4096) continue;
            byte[] buffer=new byte[size];
            if(PowerReadFriendlyName(0,ref id,0,0,buffer,ref size)!=0) continue;
            string name=Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            plans.Add(new(id,name,id==active));
        }
        return plans;
    }
    public static void SetPlan(PowerPlan plan)
    {
        var id=plan.Id; uint error=PowerSetActiveScheme(0,ref id);
        if(error!=0) throw new System.ComponentModel.Win32Exception((int)error,"无法切换电源计划");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public byte Ac,Flag,Percent,Saving; public uint LifeTime,FullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out Status s);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root,out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root,ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(nint root,nint scheme,nint subgroup,uint access,uint index,ref Guid buffer,ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(nint root,ref Guid scheme,nint subgroup,nint setting,byte[]? buffer,ref uint size);
}
