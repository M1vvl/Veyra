using System.Diagnostics;
using System.IO;

namespace GlassDock.Services;

public static class WallpaperService
{
    public static string? FindRunningExecutable()
    {
        foreach(string name in new[]{"wallpaper64","wallpaper32"})
            foreach(var process in Process.GetProcessesByName(name))
                using(process) { try { if(process.MainModule?.FileName is { } path && File.Exists(path)) return path; } catch { } }
        return null;
    }
    public static void Control(string command)
    {
        if(command is not ("pause" or "play" or "mute" or "unmute")) throw new ArgumentException("不支持的壁纸命令");
        string executable=FindRunningExecutable()??throw new InvalidOperationException("Wallpaper Engine 当前没有运行");
        var info=new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("-control"); info.ArgumentList.Add(command);
        Process.Start(info)?.Dispose();
    }
}
