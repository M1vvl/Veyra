using Windows.Media.Control;
using System.Runtime.InteropServices;

namespace GlassDock.Music;

internal static class MediaSessionSelection
{
    internal static bool Same(GlobalSystemMediaTransportControlsSession? left,GlobalSystemMediaTransportControlsSession? right)
    {
        if(ReferenceEquals(left,right))return true;
        if(left is null || right is null)return false;
        nint first=0,second=0,firstIdentity=0,secondIdentity=0;
        try
        {
            first=WinRT.MarshalInspectable<GlobalSystemMediaTransportControlsSession>.FromManaged(left);
            second=WinRT.MarshalInspectable<GlobalSystemMediaTransportControlsSession>.FromManaged(right);
            var unknown=new Guid("00000000-0000-0000-C000-000000000046");
            return Marshal.QueryInterface(first,ref unknown,out firstIdentity)>=0
                && Marshal.QueryInterface(second,ref unknown,out secondIdentity)>=0 && firstIdentity==secondIdentity;
        }
        finally
        {
            if(firstIdentity!=0)Marshal.Release(firstIdentity);
            if(secondIdentity!=0)Marshal.Release(secondIdentity);
            if(first!=0)Marshal.Release(first);
            if(second!=0)Marshal.Release(second);
        }
    }
    private static readonly string[] MusicPlayers=["cloudmusic","qqmusic","kugou","kuwo","spotify",
        "applemusic","appleinc.applemusic","sonara","sonora","agent-pulse","musicbee","foobar2000",
        "aimp","zunemusic","mediaplayer"];

    internal static int Rank(string source,bool playing,bool systemCurrent,bool selected)
    {
        bool music=MusicPlayers.Any(name=>source.Contains(name,StringComparison.OrdinalIgnoreCase));
        return (playing?(music?300:100):(music?200:0))+(systemCurrent?4:0)+(selected?(playing?2:8):0);
    }

    internal static GlobalSystemMediaTransportControlsSession? Select(
        GlobalSystemMediaTransportControlsSessionManager manager,GlobalSystemMediaTransportControlsSession? selected)
    {
        var current=manager.GetCurrentSession();
        GlobalSystemMediaTransportControlsSession? best=null;
        int bestRank=-1;
        foreach(var session in manager.GetSessions())
        {
            try
            {
                var status=session.GetPlaybackInfo().PlaybackStatus;
                if(status==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)continue;
                if(status==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped && !Same(session,selected))continue;
                int rank=Rank(session.SourceAppUserModelId,
                    status==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    Same(session,current),Same(session,selected));
                if(rank>bestRank) {best=session;bestRank=rank;}
            }
            catch(Exception) { }
        }
        if(best is not null)return best;
        return current?.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed?null:current;
    }
}
