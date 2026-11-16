namespace GlassDock.Rendering;

// A visible island survives fullscreen transitions while the other surfaces release
// capture ownership. Its remaining WGC sessions must also follow presentation changes.
internal sealed class CapturePresentationState
{
    private nint _window;
    private bool _covered;
    internal nint PreviousWindow {get;private set;}
    private (int X,int Y,int Width,int Height) _bounds;

    internal bool Observe(nint window,bool covered,int x,int y,int width,int height)
    {
        if(window==0 || width<=0 || height<=0)return false;
        var bounds=(x,y,width,height);
        bool changed=_window!=0 && (window!=_window || bounds!=_bounds || covered!=_covered);
        bool refresh=changed && (_covered || covered);
        if(window!=_window)PreviousWindow=_window;
        _window=window;
        _covered=covered;
        _bounds=bounds;
        return refresh;
    }
}
