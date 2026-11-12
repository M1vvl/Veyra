using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using GlassDock.Models;

namespace GlassDock.Services;

/// <summary>Atomic, user-local pin storage. Order in the list is the Dock order.</summary>
public sealed class PinStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }
    public string? LastLoadError { get; private set; }

    public PinStore(string? filePath = null) => FilePath = filePath ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassDock", "pins.json");

    public List<PinnedApp> Load()
    {
        LastLoadError = null;
        if (!File.Exists(FilePath)) return [];
        try
        {
            var pins = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
            // Retain temporarily unavailable targets (unmounted disks, disconnected shares).
            return pins.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Path))
                .Select(p => p with
                {
                    Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                    Name = string.IsNullOrWhiteSpace(p.Name) ? System.IO.Path.GetFileNameWithoutExtension(p.Path) : p.Name,
                    Target = p.Target ?? "", Arguments = p.Arguments ?? "", WorkingDirectory = p.WorkingDirectory ?? "",
                    IconPath = p.IconPath ?? "", AppUserModelId = p.AppUserModelId ?? ""
                })
                .Select(p => string.IsNullOrWhiteSpace(p.AppUserModelId)
                    && File.Exists(p.Path) && Path.GetExtension(p.Path).Equals(".lnk",StringComparison.OrdinalIgnoreCase) ? ReadShortcut(p) : p)
                .DistinctBy(p => p.Identity, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastLoadError = ex.Message;
            // Never overwrite or delete a malformed file merely because it could not be read.
            return [];
        }
    }

    public void Save(IReadOnlyList<PinnedApp> pins)
    {
        var directory = System.IO.Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, pins.DistinctBy(p => p.Identity, StringComparer.OrdinalIgnoreCase)
                    .ToArray(), JsonOptions);
                stream.Flush(true);
            }
            // Same-directory rename is atomic; readers see the previous or next complete JSON file.
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static PinnedApp CreatePin(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a file or folder.", nameof(path));
        path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("The dropped item no longer exists.", path);
        var pin = new PinnedApp
        {
            Path = path,
            Target = path,
            Name = Directory.Exists(path) ? new DirectoryInfo(path).Name : System.IO.Path.GetFileNameWithoutExtension(path),
            WorkingDirectory = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? ""
        };
        if (System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return ReadShortcut(pin);
        if (System.IO.Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
            return ReadInternetShortcut(pin);
        return pin;
    }

    private static PinnedApp ReadInternetShortcut(PinnedApp pin)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(pin.Path).Take(512))
        {
            var equals = line.IndexOf('=');
            if (equals > 0) values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        values.TryGetValue("URL", out var target);
        values.TryGetValue("IconFile", out var icon);
        values.TryGetValue("IconIndex", out var index);
        return pin with
        {
            Target = !string.IsNullOrWhiteSpace(target) ? target : pin.Path,
            IconPath = Environment.ExpandEnvironmentVariables(icon ?? ""),
            IconIndex = int.TryParse(index, out var parsedIndex) ? parsedIndex : 0
        };
    }

    private static PinnedApp ReadShortcut(PinnedApp pin)
    {
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            ((IPersistFile)shellLink).Load(pin.Path, 0);
            var link = (IShellLinkW)shellLink;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var directory = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            // Do not Resolve(): no UI, network searching, target execution, or shortcut mutation.
            link.GetPath(target, target.Capacity, nint.Zero, 4 /* SLGP_RAWPATH */);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(directory, directory.Capacity);
            link.GetIconLocation(icon, icon.Capacity, out var index);
            var appId = ReadAppId(shellLink);
            if(string.IsNullOrWhiteSpace(appId)) appId=ReadShellTargetAppId(link);
            return pin with
            {
                Target = Environment.ExpandEnvironmentVariables(target.ToString()),
                Arguments = arguments.ToString(),
                WorkingDirectory = Environment.ExpandEnvironmentVariables(directory.ToString()),
                IconPath = Environment.ExpandEnvironmentVariables(icon.ToString()),
                IconIndex = index,
                AppUserModelId = appId
            };
        }
        catch (COMException)
        {
            // Shell namespace/MSI shortcuts can have no filesystem target. The original link
            // remains launchable and its unoverlaid shell image is still obtainable.
            return pin;
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink)) Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private static string ReadShellTargetAppId(IShellLinkW link)
    {
        nint pidl=0,name=0;
        try
        {
            link.GetIDList(out pidl);
            if(pidl==0 || SHGetNameFromIDList(pidl,0x80028000,out name)<0) return "";
            string parsingName=Marshal.PtrToStringUni(name)??"";
            string identity=parsingName[(parsingName.LastIndexOf('\\')+1)..];
            return identity.Contains('!')?identity:"";
        }
        finally
        {
            if(name!=0) Marshal.FreeCoTaskMem(name);
            if(pidl!=0) Marshal.FreeCoTaskMem(pidl);
        }
    }
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(nint pidl,uint kind,out nint name);

    private static string ReadAppId(object link)
    {
        if (link is not IPropertyStore store) return "";
        var key = new PropertyKey { FormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
        var value = new PropVariant();
        try
        {
            if (store.GetValue(ref key, out value) < 0 || value.Type != 31 || value.Pointer == nint.Zero) return "";
            return Marshal.PtrToStringUni(value.Pointer) ?? "";
        }
        finally { PropVariantClear(ref value); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, nint findData, uint flags);
        void GetIDList(out nint pidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
    // PROPVARIANT is 24 bytes on x64, 16 on x86. The application publishes x64.
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
