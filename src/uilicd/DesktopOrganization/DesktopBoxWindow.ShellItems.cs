using System.Windows.Controls;

namespace GlassDock.DesktopOrganization;

internal sealed partial class DesktopBoxWindow
{
    private bool _readingShellIcon;
    private long _lastShellIconRead;
    private async Task RefreshShellIcons()
    {
        if(_readingShellIcon || _closed || !IsVisible || !SurfaceVisible || Environment.TickCount64-_lastShellIconRead<2000)return;
        var item=_box.Items.Find(item=>DesktopShellReference.IsRecycleBin(item.Path));
        if(item is null || !_itemViews.TryGetValue(item.Id,out var view) || !view.IsVisible)return;
        var image=Descendants<Image>(view).FirstOrDefault();
        if(image is null)return;
        _readingShellIcon=true; _lastShellIconRead=Environment.TickCount64;
        try
        {
            var icon=await Services.IconService.GetFileIconAsync(DesktopShellReference.RecycleBin,_iconPixels);
            if(_closed || !IsVisible || !SurfaceVisible)return;
            if(image.IsVisible && image.Source!=icon.Image)image.Source=icon.Image;
        }
        catch(Exception exception) { Program.Log("Recycle Bin icon: "+exception.Message); }
        finally { _readingShellIcon=false; }
    }
}
