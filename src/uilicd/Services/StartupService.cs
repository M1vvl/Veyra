using System.IO;
using Microsoft.Win32;

namespace GlassDock.Services;

public sealed record StartupStatus(bool Registered,bool Enabled,string? Command,string? Error);

/// <summary>Owns only this application's current-user startup entry.</summary>
public static class StartupService
{
    private const string RunPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalPath=@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName="uilicd";
    private static readonly string[] LegacyValueNames=["Veyra","GlassDock"];

    internal static void MigrateOwnedRegistration()
    {
        if(!string.Equals(Path.GetFileName(Environment.ProcessPath),"uilicd.exe",StringComparison.OrdinalIgnoreCase))return;
        try
        {
            using var run=Registry.CurrentUser.OpenSubKey(RunPath,true);
            if(run is null)return;
            string? current=run.GetValue(ValueName) as string;
            if(current is not null && !IsOwnedCommand(current))return;
            var legacyNames=LegacyValueNames.Where(name=>run.GetValue(name) is string command && IsOwnedCommand(command)).ToArray();
            if(current is null && legacyNames.Length==0)return;
            using var approval=Registry.CurrentUser.CreateSubKey(ApprovalPath,true);
            // Preserve Windows' enabled/disabled choice while changing the product name/path.
            if(current is null && approval.GetValue(legacyNames[0]) is byte[] state)
                approval.SetValue(ValueName,state,RegistryValueKind.Binary);
            run.SetValue(ValueName,BuildCommand(),RegistryValueKind.String);
            foreach(string legacyName in legacyNames)
            {
                run.DeleteValue(legacyName,false); approval.DeleteValue(legacyName,false);
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { Program.Log("Startup migration: "+ex.Message); }
    }

    public static StartupStatus Read()
    {
        try
        {
            using var run=Registry.CurrentUser.OpenSubKey(RunPath);
            string? command=run?.GetValue(ValueName) as string;
            if(string.IsNullOrWhiteSpace(command)) return new(false,false,null,null);
            bool ours=string.Equals(command,BuildCommand(),StringComparison.OrdinalIgnoreCase);
            if(!ours) return new(true,false,command,"启动路径已变化");
            using var approval=Registry.CurrentUser.OpenSubKey(ApprovalPath);
            var state=approval?.GetValue(ValueName) as byte[];
            bool disabled=state is { Length:>0 } && (state[0]&1)!=0;
            return new(true,!disabled,command,disabled?"已被 Windows 禁用":null);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { return new(false,false,null,ex.Message); }
    }

    public static StartupStatus SetEnabled(bool enabled)
    {
        try
        {
            if(enabled)
            {
                using var run=Registry.CurrentUser.CreateSubKey(RunPath,true);
                run.SetValue(ValueName,BuildCommand(),RegistryValueKind.String);
                // This key is our own startup item. A previous explicit user disable may leave
                // an approval record behind; enabling from this app resets only that record.
                using var approval=Registry.CurrentUser.OpenSubKey(ApprovalPath,true);
                approval?.DeleteValue(ValueName,false);
            }
            else
            {
                using var run=Registry.CurrentUser.OpenSubKey(RunPath,true);
                if(run?.GetValue(ValueName) is string current && IsOwnedCommand(current)) run.DeleteValue(ValueName,false);
            }
            return Read();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { return new(false,false,null,ex.Message); }
    }

    internal static string BuildCommand()
    {
        string executable=Environment.ProcessPath??throw new InvalidOperationException("无法定位启动程序");
        executable=Path.GetFullPath(executable);
        if(!Path.GetFileName(executable).Equals("uilicd.exe",StringComparison.OrdinalIgnoreCase) || !File.Exists(executable) || executable.Contains('"'))
            throw new InvalidOperationException("请使用发布目录中的 uilicd.exe 设置自启");
        return '"'+executable+"\" --background";
    }

    private static bool IsOwnedCommand(string command)
    {
        // Allow removing this app's former location after the user moves the installation.
        if(!command.StartsWith('"')) return false;
        int end=command.IndexOf('"',1);
        if(end<2 || command[(end+1)..].Trim()!="--background") return false;
        string file=Path.GetFileName(command[1..end]);
        return file.Equals("uilicd.exe",StringComparison.OrdinalIgnoreCase) || LegacyValueNames.Any(name=>file.Equals(name+".exe",StringComparison.OrdinalIgnoreCase));
    }
}
