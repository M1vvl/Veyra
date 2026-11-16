using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDock.Models;

namespace GlassDock.Services;

/// <summary>Native shell icons without shortcut arrows. Cache contains pixels, never HICONs.</summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> CacheOrder=new();
    private static readonly ConcurrentDictionary<string,Task<BitmapSource>> Pending=new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string,PinnedApp> Metadata=new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> MetadataOrder=new();
    private static readonly ConcurrentDictionary<string,Task<(PinnedApp App,BitmapSource Image)>> FilePending=new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(int Index,int Size),WeakReference<BitmapSource>> ShellPixels=new();
    private static readonly BlockingCollection<Action> IconRequests = new();
    private static readonly Lazy<Thread> IconThread = new(() =>
    {
        var worker = new Thread(() =>
        {
            foreach (var request in IconRequests.GetConsumingEnumerable()) request();
        }) { IsBackground = true, Name = "GlassDock native icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return worker;
    });
    private static readonly Guid ImageListId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    /// <summary>Use from the UI so a slow shell extension or disconnected disk cannot block input.</summary>
    public static Task<BitmapSource> GetIconAsync(PinnedApp app, int size = 96)
    {
        size = Math.Clamp(size,16,256);
        if(!DesktopOrganization.DesktopShellReference.IsRecycleBin(app.Path) && Cache.TryGetValue(CacheKey(app,size),out var cached)) return Task.FromResult(cached);
        string key=CacheKey(app,size);
        if(Pending.TryGetValue(key,out var pending))return pending;
        var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        while(!Pending.TryAdd(key,completion.Task)) { if(Pending.TryGetValue(key,out pending))return pending; }
        _ = IconThread.Value;
        IconRequests.Add(() =>
        {
            try { completion.SetResult(GetIcon(app, size)); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Pending.TryRemove(key,out _); }
        });
        return completion.Task;
    }

    /// <summary>Resolve shortcut metadata and warm its pixels on the existing STA icon worker.</summary>
    internal static Task<(PinnedApp App,BitmapSource Image)> GetFileIconAsync(string path,int size=96)
    {
        size=Math.Clamp(size,16,256);
        if(DesktopOrganization.DesktopShellReference.IsRecycleBin(path))
        {
            var shell=new PinnedApp { Path=DesktopOrganization.DesktopShellReference.RecycleBin,Target=DesktopOrganization.DesktopShellReference.RecycleBin,Name="回收站" };
            return Complete(shell,GetIconAsync(shell,size));
        }
        if(Metadata.TryGetValue(path,out var known))
        {
            var task=GetIconAsync(known,size);
            if(task.IsCompletedSuccessfully)return Task.FromResult((known,task.Result));
            return Complete(known,task);
        }
        string key=path+"|"+size;
        if(FilePending.TryGetValue(key,out var pending))return pending;
        var completion=new TaskCompletionSource<(PinnedApp,BitmapSource)>(TaskCreationOptions.RunContinuationsAsynchronously);
        while(!FilePending.TryAdd(key,completion.Task)) { if(FilePending.TryGetValue(key,out pending))return pending; }
        _=IconThread.Value;
        IconRequests.Add(()=>
        {
            try {
                var app=PinStore.CreatePin(path);
                if(Metadata.TryAdd(path,app))MetadataOrder.Enqueue(path);
                while(Metadata.Count>240 && MetadataOrder.TryDequeue(out var old))Metadata.TryRemove(old,out _);
                completion.SetResult((app,GetIcon(app,size)));
            }
            catch(Exception exception) { completion.SetException(exception); }
            finally { FilePending.TryRemove(key,out _); }
        });
        return completion.Task;
    }
    private static async Task<(PinnedApp App,BitmapSource Image)> Complete(PinnedApp app,Task<BitmapSource> task)=>(app,await task.ConfigureAwait(false));

    private static string CacheKey(PinnedApp app,int size)=>$"{app.Path}|{app.Target}|{app.IconPath}|{app.IconIndex}|{app.AppUserModelId}|{size}";

    public static BitmapSource GetIcon(PinnedApp app, int size = 96)
    {
        size = Math.Clamp(size, 16, 256);
        if(DesktopOrganization.DesktopShellReference.IsRecycleBin(app.Path))return NamespaceImage(DesktopOrganization.DesktopShellReference.RecycleBin,size)??EmptyIcon(size);
        var key = CacheKey(app,size);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        BitmapSource? image = null;
        if (!string.IsNullOrWhiteSpace(app.IconPath)) image = ExtractResource(app.IconPath, app.IconIndex, size);
        if (image is null && !string.IsNullOrWhiteSpace(app.AppUserModelId)) image = AppFolderImage(app.AppUserModelId, size);
        if (image is null && IsIconResource(app.Target)) image = ExtractResource(app.Target, 0, size);
        // Fetch only the image index, then ILD_TRANSPARENT. SHGFI_ICON on the .lnk itself
        // would let Explorer add the shortcut-arrow overlay. Neither LINKOVERLAY nor
        // ADDOVERLAYS is requested anywhere in this implementation.
        image ??= ShellImage(app.Path, size);
        image ??= ShellImage(app.Target, size);
        image ??= ExtractResource(System.IO.Path.Combine(Environment.SystemDirectory, "shell32.dll"), 0, size);
        image ??= EmptyIcon(size);
        if(Cache.TryAdd(key,image))
        {
            CacheOrder.Enqueue(key);
            while(Cache.Count>240 && CacheOrder.TryDequeue(out var old))Cache.TryRemove(old,out _);
            return image;
        }
        return Cache.TryGetValue(key,out var retained)?retained:image;
    }

    public static void ClearCache()
    {
        Cache.Clear(); Metadata.Clear();
        ShellPixels.Clear();
        while(CacheOrder.TryDequeue(out _)) { }
        while(MetadataOrder.TryDequeue(out _)) { }
    }

    private static bool IsIconResource(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapSource? ExtractResource(string path, int index, int size)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        nint large = nint.Zero, small = nint.Zero;
        try
        {
            var result = SHDefExtractIcon(path, index, 0, out large, out small, (uint)size);
            return result == 0 && large != nint.Zero ? FromOwnedIcon(large) : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or IOException) { return null; }
        finally
        {
            if (large != nint.Zero) DestroyIcon(large);
            if (small != nint.Zero && small != large) DestroyIcon(small);
        }
    }

    private static BitmapSource? ShellImage(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        const uint sysIconIndex = 0x4000;
        const uint useFileAttributes = 0x10;
        var exists = File.Exists(path) || Directory.Exists(path);
        var flags = sysIconIndex | (exists ? 0u : useFileAttributes);
        var attributes = Directory.Exists(path) ? 0x10u : 0x80u;
        if (SHGetFileInfo(path, attributes, out var info, (uint)Marshal.SizeOf<SHFileInfo>(), flags) == nint.Zero) return null;
        return SharedShellImage(info.IconIndex,size);
    }

    private static BitmapSource? AppFolderImage(string appId, int size)
        =>NamespaceImage("shell:AppsFolder\\"+appId,size);
    private static BitmapSource? NamespaceImage(string parsingName,int size)
    {
        nint pidl = nint.Zero;
        try
        {
            // Resolve the installed application's native logo, including packaged apps
            // whose executable is a generic host. Parsing does not launch the app.
            if (SHParseDisplayName(parsingName, nint.Zero, out pidl, 0, out _) < 0 || pidl == nint.Zero) return null;
            if (SHGetFileInfoPidl(pidl, 0, out var info, (uint)Marshal.SizeOf<SHFileInfo>(), 0x4000 | 0x8) == nint.Zero) return null;
            return SharedShellImage(info.IconIndex,size);
        }
        finally { if (pidl != nint.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    private static BitmapSource? SharedShellImage(int index,int size)
    {
        if(ShellPixels.TryGetValue((index,size),out var weak) && weak.TryGetTarget(out var existing))return existing;
        var image=ImageFromIndex(index,size);
        if(image is not null)
        {
            if(ShellPixels.Count>512)ShellPixels.Clear();
            ShellPixels[(index,size)]=new WeakReference<BitmapSource>(image);
        }
        return image;
    }
    internal static object InspectCache()
    {
        var images=new HashSet<BitmapSource>(Cache.Values,ReferenceEqualityComparer.Instance);
        return new { paths=Cache.Count,uniquePixelImages=images.Count,metadata=Metadata.Count,
            pending=Pending.Count+FilePending.Count,queued=IconRequests.Count,
            estimatedPixelBytes=images.Sum(image=>(long)image.PixelWidth*image.PixelHeight*((image.Format.BitsPerPixel+7)/8)) };
    }

    private static BitmapSource? ImageFromIndex(int index, int size)
    {
        IImageList? imageList = null;
        nint icon = nint.Zero;
        try
        {
            var iid = ImageListId;
            if (SHGetImageList(size > 48 ? 4 /* SHIL_JUMBO */ : 2 /* SHIL_EXTRALARGE */, ref iid, out imageList) < 0 || imageList is null)
                return null;
            if (imageList.GetIcon(index, 1 /* ILD_TRANSPARENT, no overlays */, out icon) < 0 || icon == nint.Zero) return null;
            var bitmap = FromOwnedIcon(icon);
            if (bitmap.PixelWidth == size && bitmap.PixelHeight == size) return bitmap;
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform(
                size / (double)bitmap.PixelWidth, size / (double)bitmap.PixelHeight));
            // Materialize at the requested size. Caching a TransformedBitmap would retain
            // its 256px jumbo source and unnecessarily multiply the resident icon memory.
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
            var stride = size * 4;
            var pixels = new byte[stride * size];
            converted.CopyPixels(pixels, stride, 0);
            var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            if (icon != nint.Zero) DestroyIcon(icon);
            // SHGetImageList returns an AddRef'd IImageList interface, not a private
            // list: release the COM reference and never ImageList_Destroy it.
            if (imageList is not null) Marshal.FinalReleaseComObject(imageList);
        }
    }

    private static BitmapSource FromOwnedIcon(nint icon)
    {
        var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource EmptyIcon(int size)
    {
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, new byte[size * size * 4], size * 4);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    private interface IImageList
    {
        [PreserveSig] int Add(nint image, nint mask, out int index);
        [PreserveSig] int ReplaceIcon(int index, nint icon, out int newIndex);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int index, nint image, nint mask);
        [PreserveSig] int AddMasked(nint image, uint maskColor, out int index);
        [PreserveSig] int Draw(nint drawParameters);
        [PreserveSig] int Remove(int index);
        [PreserveSig] int GetIcon(int index, uint flags, out nint icon);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHDefExtractIconW")]
    private static extern int SHDefExtractIcon(string file, int index, uint flags, out nint largeIcon, out nint smallIcon, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern nint SHGetFileInfo(string path, uint attributes, out SHFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static extern nint SHGetFileInfoPidl(nint pidl, uint attributes, out SHFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributeMask, out uint attributes);
    [DllImport("shell32.dll", EntryPoint = "SHGetImageList")]
    private static extern int SHGetImageList(int imageList, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageList? result);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
