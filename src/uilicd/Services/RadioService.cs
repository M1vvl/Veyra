using Windows.Devices.Radios;

namespace GlassDock.Services;

public sealed record BluetoothState(bool Available,bool Enabled,string Name,string? Error);
public static class RadioService
{
    private static RadioAccessStatus? _access;
    public static async Task<BluetoothState> ReadBluetoothAsync()
    {
        try
        {
            var radios=await Radio.GetRadiosAsync();
            var radio=radios.FirstOrDefault(r=>r.Kind==RadioKind.Bluetooth);
            return radio is null?new(false,false,"蓝牙", "没有检测到蓝牙无线设备"):new(true,radio.State==RadioState.On,radio.Name,radio.State==RadioState.Disabled?"蓝牙被硬件或系统策略禁用":null);
        }
        catch(Exception ex) { return new(false,false,"蓝牙",ex.Message); }
    }
    public static async Task<BluetoothState> SetBluetoothAsync(bool enabled)
    {
        try
        {
            _access??=await Radio.RequestAccessAsync();
            if(_access!=RadioAccessStatus.Allowed) return new(false,false,"蓝牙","Windows 未授予无线设备控制权限："+_access);
            var radios=await Radio.GetRadiosAsync(); var radio=radios.FirstOrDefault(r=>r.Kind==RadioKind.Bluetooth);
            if(radio is null) return new(false,false,"蓝牙","没有检测到蓝牙无线设备");
            var result=await radio.SetStateAsync(enabled?RadioState.On:RadioState.Off);
            if(result!=RadioAccessStatus.Allowed) return new(true,radio.State==RadioState.On,radio.Name,"蓝牙开关未被接受："+result);
            await Task.Delay(180);
            return await ReadBluetoothAsync();
        }
        catch(Exception ex) { return new(false,false,"蓝牙",ex.Message); }
    }
}
