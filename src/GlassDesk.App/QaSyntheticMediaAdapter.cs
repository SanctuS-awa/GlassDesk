#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
using GlassDesk.Core;

namespace GlassDesk.App;

// Data fixture in the REAL widget renderer, never an optical input. This type
// is excluded from ordinary builds and cannot control the user's music player.
internal sealed class QaSyntheticMediaAdapter : IMediaAdapter
{
    const string PlaylistMode="playlist", DisconnectedMode="disconnected";
    const string PlaylistNotice="QA playlist · 上一首/下一首只切换本地合成元数据，不控制播放器。";
    readonly record struct PlaylistTrack(string Title,string Artist,string Album);
    static readonly PlaylistTrack[] Playlist=
    [
        new("QA · The Last Light Along an Endless Northern Coastline",
            "QA · The Glass Harbor Orchestra for Imaginary Night Drives",
            "QA · Long title and artist fixture"),
        new("QA · Hi",
            "QA · Li",
            "QA · Short title and artist fixture")
    ];

    bool disposed;
    readonly string? mode;
    readonly byte[]? cover;
    int trackIndex;
    public event Action<MediaSnapshot>? SnapshotChanged;
    public MediaSnapshot Current {get;private set;}

    public QaSyntheticMediaAdapter(string? mode=null)
    {
        if(mode is not null&&mode is not (PlaylistMode or DisconnectedMode))
            throw new ArgumentOutOfRangeException(nameof(mode),"Unsupported QA synthetic media mode.");
        this.mode=mode;
        cover=mode==DisconnectedMode?null:File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","GlassDesk-logo.png"));
        Current=mode switch
        {
            DisconnectedMode=>MediaSnapshot.Disconnected with {Notice="QA · 断连渲染样例，不代表真实媒体状态。"},
            PlaylistMode=>CreatePlaylistSnapshot(1,Playlist[0]),
            _=>new("qa-synthetic-media",1,true,"QA · Morning Light","合成数据 · 不控制播放器","Visual acceptance fixture",
                cover,PlaybackState.Playing,1,null,new(true,true,true,false),"仅检查真实渲染外观，不代表音乐连接验收。")
        };
    }

    MediaSnapshot CreatePlaylistSnapshot(long version,PlaylistTrack track)
        =>new("qa-synthetic-media",version,true,track.Title,track.Artist,track.Album,cover,
            PlaybackState.Playing,1,null,new(true,true,true,false),PlaylistNotice);
    public Task StartAsync(CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(disposed,this);
        SnapshotChanged?.Invoke(Current);return Task.CompletedTask;
    }
    public Task<CommandResult> ExecuteAsync(MediaCommand command,string sessionToken,TimeSpan? position=null,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(disposed,this);
        if(!Current.Connected)return Task.FromResult(new CommandResult(false,false,"QA disconnected fixture: no media commands are available."));
        if(sessionToken!=Current.SessionToken||command==MediaCommand.Seek)return Task.FromResult(new CommandResult(false,false,"QA fixture: unsupported command"));
        if(mode==PlaylistMode&&command is (MediaCommand.Previous or MediaCommand.Next))
        {
            trackIndex=(trackIndex+(command==MediaCommand.Next?1:Playlist.Length-1))%Playlist.Length;
            var track=Playlist[trackIndex];
            Current=Current with {ContentVersion=Current.ContentVersion+1,Title=track.Title,Artist=track.Artist,Album=track.Album,Cover=cover,Notice=PlaylistNotice};
        }
        else Current=Current with {ContentVersion=Current.ContentVersion+1,
            State=command==MediaCommand.TogglePlayPause?(Current.State==PlaybackState.Playing?PlaybackState.Paused:PlaybackState.Playing):Current.State};
        SnapshotChanged?.Invoke(Current);return Task.FromResult(new CommandResult(true,true));
    }
    public void Dispose(){disposed=true;SnapshotChanged=null;}
}
#endif
