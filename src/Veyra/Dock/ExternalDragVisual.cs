using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GlassDock.Models;
using GlassDock.Services;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace GlassDock.Docking;

/// <summary>Continues Explorer's drag image over the WPF target and warms the landing icon.</summary>
internal sealed class ExternalDragVisual : IDisposable
{
    private IDropTargetHelper? _helper;
    private bool _entered;
    private bool _unavailable;
    private int _generation;
    private readonly Dictionary<string,PinnedApp> _prepared=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requested=new(StringComparer.OrdinalIgnoreCase);

    internal void Update(Window target, FrameworkElement coordinates, DragEventArgs args,bool prepareIcons=true)
    {
        if(PresentationSource.FromVisual(coordinates) is null) return;
        var point=coordinates.PointToScreen(args.GetPosition(coordinates));
        var native=new NativePoint { X=(int)Math.Round(point.X),Y=(int)Math.Round(point.Y) };
        try
        {
            if(!_entered && !_unavailable)
            {
                _helper??=(IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"))!)!;
                if(args.Data is not ComDataObject data) { _unavailable=true; return; }
                int result=_helper.DragEnter(new WindowInteropHelper(target).Handle,data,ref native,(uint)args.Effects);
                _entered=result>=0;
                if(result<0) { _unavailable=true; Program.Log("Drag image enter HRESULT: "+result.ToString("X8")); }
            }
            else if(_entered)
            {
                int result=_helper!.DragOver(ref native,(uint)args.Effects);
                if(result<0) { _helper.DragLeave(); _entered=false; _unavailable=true; Program.Log("Drag image over HRESULT: "+result.ToString("X8")); }
            }
        }
        catch(COMException exception) { _unavailable=true; Program.Log("Drag image: "+exception.Message); }
        if(prepareIcons && _requested.Count==0 && args.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach(var path in paths.Take(64)) if(_requested.Add(path)) _=PrepareAsync(path,_generation);
    }

    private async Task PrepareAsync(string path,int generation)
    {
        try
        {
            var prepared=await IconService.GetFileIconAsync(path,96);
            if(generation==_generation) _prepared[path]=prepared.App;
        }
        catch(Exception exception) { Program.Log("Drag icon: "+exception.Message); }
    }

    internal PinnedApp Resolve(string path)=>_prepared.TryGetValue(path,out var app)?app:PinStore.CreatePin(path);

    internal void Drop(FrameworkElement coordinates,DragEventArgs args)
    {
        bool completed=false;
        try
        {
            if(_entered && PresentationSource.FromVisual(coordinates) is not null && args.Data is ComDataObject data)
            {
                var point=coordinates.PointToScreen(args.GetPosition(coordinates));
                var native=new NativePoint { X=(int)Math.Round(point.X),Y=(int)Math.Round(point.Y) };
                int result=_helper!.Drop(data,ref native,(uint)args.Effects);
                completed=result>=0;
                if(result<0) Program.Log("Drag image drop HRESULT: "+result.ToString("X8"));
            }
        }
        catch(COMException exception) { Program.Log("Drag drop image: "+exception.Message); }
        finally
        {
            if(_entered && !completed)
                try { _helper?.DragLeave(); } catch(COMException) { }
            _entered=false; Reset();
        }
    }

    internal void Leave()
    {
        try { if(_entered) _helper?.DragLeave(); }
        catch(COMException exception) { Program.Log("Drag leave image: "+exception.Message); }
        finally { _entered=false; Reset(); }
    }
    private void Reset() { _generation++; _unavailable=false; _requested.Clear(); _prepared.Clear(); }
    public void Dispose()
    {
        Leave();
        if(_helper is not null) Marshal.FinalReleaseComObject(_helper);
        _helper=null;
    }

    internal static void ValidateInterop()
    {
        // Exercise QI on the actual shipped WPF data object without sending input or changing files.
        var data=new DataObject(DataFormats.FileDrop,new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)});
        if(data is not ComDataObject) throw new InvalidOperationException("WPF file data cannot marshal as native IDataObject.");
        object helper=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"))!)!;
        try { if(helper is not IDropTargetHelper) throw new InvalidOperationException("Windows drag-image helper unavailable."); }
        finally { Marshal.FinalReleaseComObject(helper); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [ComImport,Guid("4657278B-411B-11D2-839A-00C04FD918D0"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(nint target,[MarshalAs(UnmanagedType.Interface)] ComDataObject data,ref NativePoint point,uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref NativePoint point,uint effect);
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComDataObject data,ref NativePoint point,uint effect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }
}
