using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;

namespace GlassDesk.App;

/// <summary>Owns the widget's WUC content, in DIP, independently of WinUI composition.</summary>
public sealed partial class WidgetScene : IDisposable
{
    readonly NativeWidgetHost _host;
    readonly ContainerVisual _root;
    readonly ContainerVisual _bodyRoot;
    readonly Dictionary<WidgetForm, CrossfadeSurfaceVisual> _metadata = new();
    readonly Dictionary<WidgetForm, (string Title, string Artist, float Width, uint Text, uint Secondary,
        TypographyIdentity Typography, float OffsetX, float OffsetY, float ArtistOffset)> _metadataKeys = new();
    readonly Dictionary<WidgetForm, SurfaceOp[]> _metadataOperations = new();
    readonly Dictionary<string, SurfaceOp[]> _typographyLayers = new(StringComparer.Ordinal);
    public bool CaptureTypographyDiagnostics { get; set; }
    readonly SpriteVisual _content;
    readonly SpriteVisual _capsuleContent;
    readonly SpriteVisual _peekContent;
    readonly SpriteVisual _artwork;
    readonly SpriteVisual _previousArtwork;
    readonly ContainerVisual _artworkViewport;
    readonly ContainerVisual _artworkMotion, _previousArtworkMotion;
    readonly InsetClip _artworkViewportClip;
    readonly SpriteVisual _timeline;
    readonly SpriteVisual _feedback;
    readonly ContainerVisual _audioBars;
    readonly InsetClip _audioClip;
    readonly SpriteVisual[] _bands = new SpriteVisual[8];
    readonly ControlDepthVisual[] _waveDepth = new ControlDepthVisual[8];
    readonly ControlDepthVisual _timelineTrack, _timelineThumb;
    readonly float[] _bandTargets = new float[8];
    readonly CompanionVisual _companion;
    readonly CompanionPlacementController _placement = new();
    readonly DetailsChromeVisual _chrome;
    readonly PeekCompanionVisual _peekCompanion;
    double _motionNow;
    long _openSequence;
    bool _sceneVisible = true;
    string? _placementKey;
    PointerCaptureOwner _captureOwner;
    // -1 = no dock press, -2 = cancelled (slid off the destination), otherwise the
    // pressed destination action. Release only activates when the action still matches.
    int _dockPressedAction = -1;
    public double? NextCompanionWake => _form == WidgetForm.Capsule ? _placement.NextWakeTime : null;
    public bool SceneMotionActive => PresentationMotionActive || _chrome.NeedsTick || CompanionMotionActive || _ownedControlMoving || ReadingSupportMotionActive
        || _homeVisual?.MotionActive == true || _buttons.Values.Any(button => button.MotionActive);
    public PointerCaptureOwner CaptureOwner => _captureOwner;
    readonly ContainerVisual _peekButtons;
    readonly ContainerVisual _detailsButtons;
    readonly Dictionary<(WidgetForm Form, int Action), TransportVisual> _buttons = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _artworkTimer;
    bool _artworkUpdatePending;
    bool _artworkShown;
    int _requestedTrackDirection, _activeTrackDirection;
    long _trackRequestedAt;
    string? _trackRequestedSession;
    public event Action<Exception>? RenderingFailed;
    readonly ContainerVisual _detailsViewport;
    readonly SpriteVisual _detailsBody;
    readonly CompositionSurfaceBrush _contentBrush;
    readonly CompositionSurfaceBrush _capsuleBrush;
    readonly CompositionSurfaceBrush _peekBrush;
    readonly CompositionSurfaceBrush _artworkBrush;
    readonly CompositionSurfaceBrush _previousArtworkBrush;
    readonly CompositionRoundedRectangleGeometry _artworkClip;
    readonly CompositionRoundedRectangleGeometry _previousArtworkClip;
    readonly CompositionSurfaceBrush _timelineBrush;
    readonly CompositionSurfaceBrush _feedbackBrush;
    readonly CompositionSurfaceBrush _detailsBrush;
    CompositionDrawingSurface? _contentSurface;
    CompositionDrawingSurface? _capsuleSurface;
    CompositionDrawingSurface? _peekSurface;
    CompositionDrawingSurface? _artworkSurface;
    CompositionDrawingSurface? _previousArtworkSurface;
    CompositionDrawingSurface? _timelineSurface;
    CompositionDrawingSurface? _feedbackSurface;
    CompositionDrawingSurface? _detailsSurface;
    IReadOnlyList<AccessibleControl> _controls = Array.Empty<AccessibleControl>();
    readonly Dictionary<int, WidgetForm> _controlForms = new();
    readonly AccessibilitySettings _accessibility = new();
    readonly UISettings _uiSettings = new();
    readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    MediaSnapshot _media = MediaSnapshot.Disconnected;
    WidgetPreferences _preferences = new();
    CapsuleContentMode _capsuleMode;
    CompanionVisualState? _companionState;
    float _companionX=18, _companionY=10, _companionSize=32;
    Vector2 _gazePointer;
    bool _gazeInside;
    public bool CompanionMotionActive => _form == WidgetForm.Capsule && CompanionVisible && (_placement.NeedsTick || _placement.LiquidMotionActive);
    IReadOnlyList<PluginContentPage> _pluginPages = Array.Empty<PluginContentPage>();
    IReadOnlyDictionary<string, IReadOnlyList<PluginDrawOp>> _pluginVisuals = new Dictionary<string, IReadOnlyList<PluginDrawOp>>();
    string? _widthTitle, _widthArtist;
    bool _widthCompanion, _widthAudio;
    TypographyIdentity? _widthTypography;
    float _musicCapsuleWidth = 256;
    WidgetForm _form;
    DateTimeOffset _now;
    string? _notice;
    ContentKey? _lastContent;
    TimelineKey? _lastTimeline;
    TimeSpan? _seekPreview;
    bool _disposed;
    int _feedbackId = -1;
    bool _feedbackPressed;
    bool _feedbackDirty;
    float _detailsHeight = 512;
    float _detailsScroll;
    string? _lastRootName;
    uint _text = 0xFF182338;
    uint _secondary = 0xFF37465F;
    uint _disabled = 0xFF738197;
    uint _accent = 0xFF246CE3;
    uint _button = 0x81FFFFFF;
    uint _track = 0x293B526B;
    Vector2 _presentedSize = new(200, 52);
    Vector2 _transitionEndSize = new(200, 52);
    Vector3 _contentWeights = new(1, 0, 0);
    readonly SpringValue[] _layerOpacity = [new(1), new(0), new(0)];
    readonly SpringValue[] _layerShift = [new(0), new(0), new(0)];
    readonly SpringValue[] _layerScale = [new(1), new(1), new(1)];
    readonly SpringValue _detailsBodyOpacity = new(0), _peekTransportOpacity = new(0), _detailsTransportOpacity = new(0);
    readonly SpringValue _artworkClipTop = new(0), _artworkClipBottom = new(52);
    readonly MotionController _artworkTransition = new(new(12, 14, 24, 24, 6));
    public bool PresentationMotionActive => _layerOpacity.Concat(_layerShift).Concat(_layerScale)
        .Concat([_detailsBodyOpacity, _peekTransportOpacity, _detailsTransportOpacity, _artworkClipTop, _artworkClipBottom])
        .Any(channel => channel.Active(_motionNow)) || _artworkTransition.Active;
    WidgetPose _artworkPose = new(12, 14, 24, 24, 6);
    byte[]? _lastArtwork;
    bool _artworkDirty = true;
    bool _drawingInvalidated = true;
    bool _lightText;
    bool _promoteSecondary;
    bool _clockPlateLightText;
    bool _clockPlatePromoteSecondary;
    public float TextBackgroundY => _form == WidgetForm.Details
        ? ResponsiveDetails() is { } details
            ? Math.Clamp(details.Artist.Bottom + 4 - _detailsScroll, 64, Math.Max(64, _detailsHeight - 72))
            : Math.Clamp(304 - _detailsScroll, 88, _detailsHeight - 72)
        : _form == WidgetForm.Peek ? _media.Connected ? 84 : 76 : 26;
    public float ClockPlateBackgroundY => _media.Connected ? 28 : 76;
    public bool ClockHasWhiteplate => !_media.Connected;
    public bool BodyUsesLargeClock => _form == WidgetForm.Capsule && !MusicFor(_form) && _preferences.ClockShowTime;
    public bool BodyHasRequiredSecondary => !BodyUsesLargeClock || ClockSecondary.Length > 0;
    public bool ClockPlateUsesLargeClock => !_media.Connected && _preferences.ClockShowTime;
    public bool ClockPlateHasRequiredSecondary => ClockSecondary.Length > 0;
    public bool SetTextTone(bool light, bool promoteSecondary = false,
        bool clockPlateLight = false, bool clockPlatePromoteSecondary = false)
    {
        if (_lightText == light && _promoteSecondary == promoteSecondary
            && _clockPlateLightText == clockPlateLight && _clockPlatePromoteSecondary == clockPlatePromoteSecondary) return false;
        _lightText = light;
        _chrome.SetLightInk(light);
        _promoteSecondary = promoteSecondary;
        _clockPlateLightText = clockPlateLight;
        _clockPlatePromoteSecondary = clockPlatePromoteSecondary;
        _drawingInvalidated = true;
        return true;
    }
    SpriteVisual ActiveContent => _form switch { WidgetForm.Capsule => _capsuleContent, WidgetForm.Peek => _peekContent, _ => _content };
    TypographyIdentity CurrentTypography => new(_host.TypographyProfile, _host.TypographyRevision,
        CultureInfo.CurrentUICulture.Name, _host.Context.Dpi);

    public WidgetScene(NativeWidgetHost host)
    {
        _host = host;
        _artworkTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _artworkTimer.Interval = TimeSpan.FromMilliseconds(320);
        _artworkTimer.IsRepeating = false;
        _artworkTimer.Tick += OnArtworkTransitionCompleted;
        _root = host.Compositor.CreateContainerVisual();
        _bodyRoot = host.Compositor.CreateContainerVisual();
        // The native host clips content and glass with one shared silhouette.
        _root.Children.InsertAtTop(_bodyRoot);
        _content = host.Compositor.CreateSpriteVisual();
        _capsuleContent = host.Compositor.CreateSpriteVisual();
        _peekContent = host.Compositor.CreateSpriteVisual();
        _artwork = host.Compositor.CreateSpriteVisual();
        _previousArtwork = host.Compositor.CreateSpriteVisual();
        _artworkViewport = host.Compositor.CreateContainerVisual();
        _artworkViewportClip = host.Compositor.CreateInsetClip();
        _artworkViewport.Clip = _artworkViewportClip;
        _artworkMotion = host.Compositor.CreateContainerVisual();
        _previousArtworkMotion = host.Compositor.CreateContainerVisual();
        _artworkMotion.Children.InsertAtTop(_artwork); _previousArtworkMotion.Children.InsertAtTop(_previousArtwork);
        _artworkViewport.Children.InsertAtTop(_artworkMotion);
        _artworkViewport.Children.InsertAtTop(_previousArtworkMotion);
        _timeline = host.Compositor.CreateSpriteVisual();
        _feedback = host.Compositor.CreateSpriteVisual();
        _audioBars = host.Compositor.CreateContainerVisual();
        _audioBars.Size = new(40,24);
        _audioClip = host.Compositor.CreateInsetClip();
        _audioBars.Clip = _audioClip;
        _companion = new CompanionVisual(host);
        _timelineTrack=new(host.Compositor,ControlDepthKind.InsetTrack);
        _timelineThumb=new(host.Compositor,ControlDepthKind.Thumb);
        _timeline.Children.InsertAtBottom(_timelineTrack.Visual);
        _timeline.Children.InsertAtTop(_timelineThumb.Visual);
        _chrome = new DetailsChromeVisual(host);
        _peekCompanion = new PeekCompanionVisual(host.Compositor);
        for (int index = 0; index < _bands.Length; index++)
        {
            var bar = host.Compositor.CreateSpriteVisual();
            _waveDepth[index]=new(host.Compositor,ControlDepthKind.SoftBar);
            _waveDepth[index].SetBounds(0,0,3,24,1.5f);
            bar.Children.InsertAtTop(_waveDepth[index].Visual);
            bar.Size = new(3, 24);
            bar.CenterPoint = new(1.5f, 12, 0);
            bar.Offset = new(index * 5, 0, 0);
            bar.Scale = new(1, 2f / 24, 1);
            _audioBars.Children.InsertAtTop(bar);
            _bands[index] = bar;
        }
        _peekButtons = host.Compositor.CreateContainerVisual();
        _detailsButtons = host.Compositor.CreateContainerVisual();
        _peekButtons.Opacity = _detailsButtons.Opacity = 0;
        _detailsViewport = host.Compositor.CreateContainerVisual();
        _detailsViewport.Clip = host.Compositor.CreateInsetClip(0, 0, 0, 0);
        _detailsBody = host.Compositor.CreateSpriteVisual();
        _contentBrush = host.Compositor.CreateSurfaceBrush();
        _capsuleBrush = host.Compositor.CreateSurfaceBrush();
        _peekBrush = host.Compositor.CreateSurfaceBrush();
        _artworkBrush = host.Compositor.CreateSurfaceBrush();
        _previousArtworkBrush = host.Compositor.CreateSurfaceBrush();
        _artworkClip = host.Compositor.CreateRoundedRectangleGeometry();
        _previousArtworkClip = host.Compositor.CreateRoundedRectangleGeometry();
        _artworkClip.Size = _previousArtworkClip.Size = new(208, 208);
        _artwork.Clip = host.Compositor.CreateGeometricClip(_artworkClip);
        _previousArtwork.Clip = host.Compositor.CreateGeometricClip(_previousArtworkClip);
        _timelineBrush = host.Compositor.CreateSurfaceBrush();
        _feedbackBrush = host.Compositor.CreateSurfaceBrush();
        _detailsBrush = host.Compositor.CreateSurfaceBrush();
        _contentBrush.Stretch = _timelineBrush.Stretch = _feedbackBrush.Stretch = _detailsBrush.Stretch = CompositionStretch.Fill;
        _content.Brush = _contentBrush;
        _capsuleContent.Brush = _capsuleBrush;
        _peekContent.Brush = _peekBrush;
        _artworkBrush.Stretch = _previousArtworkBrush.Stretch = _capsuleBrush.Stretch = _peekBrush.Stretch = CompositionStretch.Fill;
        _artwork.Brush = _artworkBrush;
        _previousArtwork.Brush = _previousArtworkBrush;
        _artwork.Size = _previousArtwork.Size = new(208, 208);
        _content.Opacity = _peekContent.Opacity = _artwork.Opacity = _previousArtwork.Opacity = 0;
        _capsuleContent.Size = SizeFor(WidgetForm.Capsule);
        _peekContent.Size = SizeFor(WidgetForm.Peek);
        _timeline.Brush = _timelineBrush;
        _feedback.Brush = _feedbackBrush;
        _detailsBody.Brush = _detailsBrush;
        _detailsViewport.Children.InsertAtTop(_detailsBody);
        _detailsViewport.Opacity = 0;
        _feedback.Opacity = 0;
        _bodyRoot.Children.InsertAtTop(_content);
        _bodyRoot.Children.InsertAtTop(_peekContent);
        _bodyRoot.Children.InsertAtTop(_capsuleContent);
        _bodyRoot.Children.InsertAtTop(_detailsViewport);
        _bodyRoot.Children.InsertAtTop(_timeline);
        _bodyRoot.Children.InsertAtTop(_peekButtons);
        _bodyRoot.Children.InsertAtTop(_detailsButtons);
        _bodyRoot.Children.InsertAtTop(_artworkViewport);
        _bodyRoot.Children.InsertAtTop(_feedback);
        _bodyRoot.Children.InsertAtTop(_audioBars);
        _root.Children.InsertAtTop(_chrome.Visual);
        _root.Children.InsertAtTop(_peekCompanion.Visual);
        _root.Children.InsertAtTop(_companion.Visual);
        foreach (var form in Enum.GetValues<WidgetForm>())
        {
            var metadata = new CrossfadeSurfaceVisual(host.Compositor);
            _metadata.Add(form, metadata);
            (form == WidgetForm.Capsule ? _capsuleContent : form == WidgetForm.Peek ? _peekContent : _detailsBody).Children.InsertAtTop(metadata.Visual);
        }
        host.ContentRoot.Children.InsertAtTop(_root);
        InitializeBoardVisuals();
        InitializeHomeVisual();
        host.WindowContextChanged += OnWindowContextChanged;
        host.GraphicsDeviceRestored += OnGraphicsDeviceRestored;
        _lastContentDpi = host.Context.Dpi;
    }

    public static Vector2 SizeFor(WidgetForm form)
    {
        var metrics = WidgetLayoutMetrics.Resolve(form, CapsuleContentMode.Clock);
        return new(metrics.Width, metrics.Height);
    }

    CapsuleContentMode ModeFor(WidgetForm form) => form == WidgetForm.Capsule ? _capsuleMode :
        _media.Connected ? CapsuleContentMode.Music : CapsuleContentMode.Clock;
    bool CompanionVisible => _preferences.CompanionEnabled && _companionState is not null;
    bool MusicFor(WidgetForm form) => ModeFor(form) == CapsuleContentMode.Music;
    Vector2 LayoutSize(WidgetForm form)
    {
        var metrics = WidgetLayoutMetrics.Resolve(form, ModeFor(form), CompanionVisible, _musicCapsuleWidth);
        float natural = form==WidgetForm.Details ? (HomePage ? HomeLayout.Height : WidgetsPage ? BoardNaturalHeight : _media.Connected ? 476 : 300) : metrics.Height;
        return new(form==WidgetForm.Details ? _detailsWidth : metrics.Width, form==WidgetForm.Details?Math.Min(_detailsHeight,natural):natural);
    }

    public IReadOnlyList<AccessibleControl> GetAccessibleControls() => _controls;
    public float DetailsHeight => _detailsHeight;
    public Vector2 CurrentSize => LayoutSize(_form);
    public void SetMonotonicTime(double now)=>_motionNow=now;

    /// <summary>Measure real system-font content once per title/artist or slot change.</summary>
    public float MeasureMusicCapsuleWidth(MediaSnapshot media, WidgetPreferences preferences)
    {
        EnsureUiThread();
        bool companion = preferences.CompanionEnabled;
        var typography = CurrentTypography;
        if (_widthTitle == media.Title && _widthArtist == media.Artist && _widthCompanion == companion &&
            _widthAudio == preferences.AudioVisualizationEnabled && _widthTypography == typography)
            return _musicCapsuleWidth;
        _widthTitle = media.Title; _widthArtist = media.Artist;
        _widthCompanion = companion; _widthAudio = preferences.AudioVisualizationEnabled;
        _widthTypography = typography;
        float Measure(string value, TextRole role)
        {
            // Natural-width layout uses the same role, locale, shaping and metrics as
            // the final constrained draw. Only the width constraint is relaxed here.
            var token = TypographyCatalog.Get(role);
            var operation = SurfaceOp.Typography(value, role, 0, 0, 32768, token.LineHeightDip,
                TypographyCatalog.Ink(InkRole.Primary), locale: typography.Locale);
            return _host.MeasureText(operation).Advance;
        }
        float width = Math.Max(Measure(string.IsNullOrWhiteSpace(media.Title) ? "歌曲信息暂不可用" : media.Title, TextRole.MediaCapsuleTitle),
            Measure(string.IsNullOrWhiteSpace(media.Artist) ? "歌手信息暂不可用" : media.Artist, TextRole.MediaCapsuleArtist));
        _musicCapsuleWidth = Math.Clamp(MathF.Ceiling((width + 60 + (preferences.AudioVisualizationEnabled ? 52 : 12)) / 4) * 4, 208, 296);
        return _musicCapsuleWidth;
    }

    public void SetCompanionState(CompanionVisualState? state)
    {
        EnsureUiThread();
        if (_disposed) return;
        bool changedPresence = (_companionState is null) != (state is null);
        _companionState = state;
        _chrome.SetCompanionColor(state?.Color ?? _preferences.CompanionColor);
        ConfigurePlacement();
        ApplyCompanionState(_preferences.ReducedMotion || !_uiSettings.AnimationsEnabled);
        if (changedPresence) _drawingInvalidated = true;
        ApplyContentWeights(_contentWeights);
        UpdateAuxiliaryPlacement();
    }

    public void PreviewCompanionAppearance(uint color,string appearanceId)
    {if(_companionState is { } state)SetCompanionState(state with {Color=color|0xff000000,AppearanceId=CompanionAppearanceIds.Normalize(appearanceId)});}

    void ApplyCompanionState(bool reducedMotion)
    {
        var state = _companionState;
        _companion.SetState(state, reducedMotion);
    }

    public void SetPluginPages(IReadOnlyList<PluginContentPage> pages)
    {
        EnsureUiThread();
        bool availabilityChanged = (_pluginPages.Count == 0) != (pages.Count == 0);
        _pluginPages = pages;
        if (availabilityChanged) { _drawingInvalidated = true; _placementKey = null; ConfigurePlacement(); }
    }

    public void SetPluginVisuals(IReadOnlyDictionary<string, IReadOnlyList<PluginDrawOp>> visuals)
    {
        EnsureUiThread();
        var bounded = visuals.Take(4).ToDictionary(item => item.Key,
            item => (IReadOnlyList<PluginDrawOp>)item.Value.Take(128).ToArray());
        if (_pluginVisuals.Count == bounded.Count && _pluginVisuals.All(item =>
            bounded.TryGetValue(item.Key, out var operations) && item.Value.SequenceEqual(operations))) return;
        _pluginVisuals = bounded;
        _drawingInvalidated = true;
        _placementKey = null; ConfigurePlacement();
    }

    /// <summary>Only real FFT bands reach this renderer; unavailable and silent sources settle to zero.</summary>
    public void UpdateAudio(AudioVisualizationFrame frame)
    {
        EnsureUiThread();
        if (_disposed) return;
        bool active = frame.State == AudioVisualizationState.Capturing && _preferences.AudioVisualizationEnabled &&
            frame.SessionToken == _media.SessionToken && _media.State == PlaybackState.Playing;
        for (int index = 0; index < _bands.Length; index++)
        {
            float value = active && index < frame.Bands.Length && float.IsFinite(frame.Bands[index]) ? Math.Clamp(frame.Bands[index], 0, 1) : 0;
            float target = (2 + value * 22) / 24;
            if (Math.Abs(target - _bandTargets[index]) < .002f) continue;
            _bandTargets[index] = target;
            var bar = _bands[index];
            if (_preferences.ReducedMotion || !_uiSettings.AnimationsEnabled)
            { bar.StopAnimation("Scale"); bar.Scale = new(1, target, 1); }
            else
            {
                using var easing = _host.Compositor.CreateCubicBezierEasingFunction(new(.22f, 1), new(.36f, 1));
                using var animation = _host.Compositor.CreateVector3KeyFrameAnimation();
                animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, new Vector3(1, target, 1), easing);
                animation.Duration = TimeSpan.FromMilliseconds(65); bar.StartAnimation("Scale", animation);
            }
        }
    }

    public void SetDetailsViewport(float heightDip, float widthDip = 360)
    {
        EnsureUiThread();
        float height = Math.Clamp(heightDip, 160, 512);
        float width = Math.Clamp(widthDip, 64, HomeLayout.DashboardWidth);
        if (Math.Abs(height - _detailsHeight) < .1f && Math.Abs(width - _detailsWidth) < .1f) return;
        _detailsHeight = height;
        _detailsWidth = width;
        _drawingInvalidated = true;
        _lastTimeline = null;
    }

    public bool ScrollDetails(float deltaDip)
    {
        EnsureUiThread();
        if (_disposed || _form != WidgetForm.Details) return false;
        if (HomePage) return false;
        if (WidgetsPage) return ScrollBoard(deltaDip);
        try
        {
            float maximum = Math.Max(0, _detailsBody.Size.Y - _detailsViewport.Size.Y);
            if (maximum <= 0) return false;
            float previous = _detailsScroll;
            _detailsScroll = Math.Clamp(_detailsScroll + deltaDip, 0, maximum);
            _detailsBody.Offset = new(0, -_detailsScroll, 0);
            AdjustArtworkScroll(_detailsScroll - previous);
            _placementKey = null;
            ConfigurePlacement();
            UpdateAuxiliaryPlacement();
            UpdateReadingSupport();
            return true;
        }
        catch { InvalidateDrawingCaches(); throw; }
    }

    public void Render(WidgetForm form, MediaSnapshot media, DateTimeOffset now,
        WidgetPreferences preferences, string? notice = null, CapsuleContentMode? capsuleMode = null)
    {
        EnsureUiThread();
        if (_disposed) return;
        BeginSystemTransportPresentation();
        try { RenderCore(form, media, now, preferences, notice, capsuleMode); }
        catch { InvalidateDrawingCaches(); throw; }
    }

    void RenderCore(WidgetForm form, MediaSnapshot media, DateTimeOffset now,
        WidgetPreferences preferences, string? notice, CapsuleContentMode? capsuleMode)
    {
        bool enteringForm = _lastContent is null || _form != form;
        bool connectionChanged = _media.Connected != media.Connected;
        _form = form;
        _media = media;
        _now = now;
        if (_preferences.LiquidGlassEnabled != preferences.LiquidGlassEnabled) _drawingInvalidated = true;
        _preferences = preferences;
        _capsuleMode = capsuleMode ?? (media.Connected ? CapsuleContentMode.Music : CapsuleContentMode.Clock);
        MeasureMusicCapsuleWidth(media, preferences);
        _notice = notice ?? media.Notice;
        SetPalette();
        UpdateHomeVisual();
        ApplyCompanionState(preferences.ReducedMotion || !_uiSettings.AnimationsEnabled);
        if (enteringForm || connectionChanged) _placementKey=null;
        _chrome.SetReducedMotion(preferences.ReducedMotion,_motionNow);
        ConfigurePlacement();
        var size = CurrentSize;
        ActiveContent.Size = size;
        if (!preferences.CompanionFollowMouse || preferences.ReducedMotion || enteringForm) ClearCompanionPointerGaze();
        var clockKey = form == WidgetForm.Details || MusicFor(form) && form == WidgetForm.Capsule ? string.Empty : now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + $"/{preferences.ClockShowTime}/{preferences.ClockShowDate}/{preferences.ClockShowWeekday}/{preferences.ClockFontId}";
        var key = new ContentKey(form, media.SessionToken, media.ContentVersion, media.Connected,
            media.Title, media.Artist, media.Album, media.Cover, media.State, media.Capabilities,
            media.Timeline?.Valid == true, _notice, clockKey, _text, _secondary, _accent, preferences.TintOpacity, preferences.ReducedMotion,
            _capsuleMode, CompanionVisible, preferences.AudioVisualizationEnabled, CurrentTypography);
        bool changed = _drawingInvalidated || _lastContent != key;
        if (changed)
        {
            var priorContent = _lastContent;
            bool trackChanged = priorContent is not null && priorContent.Form == form &&
                (priorContent.Session != media.SessionToken || priorContent.Title != media.Title || priorContent.Artist != media.Artist || priorContent.Album != media.Album);
            if (trackChanged)
            {
                _activeTrackDirection = _trackRequestedSession == media.SessionToken && Stopwatch.GetElapsedTime(_trackRequestedAt).TotalSeconds < 5 ? _requestedTrackDirection : 0;
                _requestedTrackDirection = 0;
            }
            bool formChanged = priorContent is not null && (priorContent.Form != form || _transitionEndSize != size);
            if (formChanged)
            {
                RetargetContent(form, preferences.ReducedMotion);
                var artworkTarget = ArtworkPoseFor(form);
                _transitionEndSize = size;
                var artworkClip = ArtworkViewportFor(form, size);
                _artworkTransition.Retarget(artworkTarget, _motionNow, 300, preferences.ReducedMotion);
                _artworkClipTop.Retarget(artworkClip.X, _motionNow, 280, preferences.ReducedMotion);
                _artworkClipBottom.Retarget(artworkClip.Y, _motionNow, 280, preferences.ReducedMotion);
            }
            else if (priorContent is null || preferences.ReducedMotion)
            {
                _contentWeights = WeightsFor(form);
                RetargetContent(form, immediate: true);
                _artworkPose = ArtworkPoseFor(form);
                _transitionEndSize = size;
                var artworkClip = ArtworkViewportFor(form, size);
                _artworkTransition.Jump(_artworkPose);
                _artworkClipTop.Jump(artworkClip.X); _artworkClipBottom.Jump(artworkClip.Y);
            }
            if (_drawingInvalidated && priorContent is not null) RedrawOutgoingLayers(form);
            var operations = new List<SurfaceOp>();
            switch (form)
            {
                case WidgetForm.Capsule: DrawCapsule(operations); break;
                case WidgetForm.Peek: DrawPeek(operations); break;
                case WidgetForm.Details: DrawDetails(operations); break;
            }
            var surface = RenderTextLayer(form.ToString(), size.X, size.Y, operations);
            switch (form)
            {
                case WidgetForm.Capsule: _capsuleClockInk?.Dispose();ReplaceSurface(ref _capsuleSurface, _capsuleBrush, surface); break;
                case WidgetForm.Peek: ReplaceSurface(ref _peekSurface, _peekBrush, surface); break;
                default: ReplaceSurface(ref _contentSurface, _contentBrush, surface); break;
            }
            if (form == WidgetForm.Details && !HomePage)
            {
                var geometry = ResponsiveDetails();
                var body = new List<SurfaceOp>();
                DrawDetailsBody(body, geometry);
                float priorScroll = _detailsScroll;
                if (priorContent is not null && (priorContent.Session != media.SessionToken || priorContent.Version != media.ContentVersion)) _detailsScroll = 0;
                if (geometry is not null)
                {
                    // Only the body scrolls: its clip starts at the title top and ends above the
                    // fixed timeline/transport row. The surface height is the real content extent,
                    // so a page with nothing to scroll reports MaxScroll = 0.
                    float contentBottom = Math.Max(geometry.Title.Bottom, geometry.Artist.Bottom);
                    float bodyContent = contentBottom - geometry.BodyViewport.Y
                        + Math.Min(4, _pluginVisuals.Count) * 112f;
                    _detailsBody.Size = new(size.X, Math.Max(1, bodyContent));
                    _detailsViewport.Size = new(size.X, geometry.BodyViewport.Height);
                    _detailsViewport.Offset = new(0, geometry.BodyViewport.Y, 0);
                    _detailsScroll = Math.Clamp(_detailsScroll, 0, geometry.MaxScroll);
                }
                else
                {
                    _detailsBody.Size = new(size.X, 112);
                    _detailsViewport.Size = new(size.X, DetailsBodyViewportHeight(size));
                    _detailsViewport.Offset = new(0, 56, 0);
                    _detailsScroll = 0;
                }
                if (!formChanged) AdjustArtworkScroll(_detailsScroll - priorScroll);
                _detailsBody.Offset = new(0, -_detailsScroll, 0);
                if (priorScroll != _detailsScroll) { _placementKey = null; ConfigurePlacement(); }
                ReplaceSurface(ref _detailsSurface, _detailsBrush, RenderTextLayer("DetailsBody", size.X, _detailsBody.Size.Y, body));
            }
            UpdateMetadata(form, trackChanged);
            // Text, controls and the shell share one interruptible presentation.
            // A second independent fade would expose the final layout inside a small shell.
            ApplyContentWeights(_contentWeights);
            UpdateArtwork();
            _lastContent = key;
            _drawingInvalidated = false;
            _lastTimeline = null;
        }
        UpdateTimeline(media, Stopwatch.GetTimestamp());
        BuildControls();
        if (changed) UpdateFeedback(_feedbackId, _feedbackPressed, force: true);
    }

    /// <summary>Call only while Details is visible. Metadata and cover are not redrawn.</summary>
    void RedrawOutgoingLayers(WidgetForm targetForm)
    {
        // A device rebuild or DPI change also invalidates layers still fading out.
        // Redraw them without resetting their opacity, geometry or scroll position.
        try
        {
            foreach (var form in Enum.GetValues<WidgetForm>())
            {
                float weight = form == WidgetForm.Capsule ? _contentWeights.X : form == WidgetForm.Peek ? _contentWeights.Y : _contentWeights.Z;
                if (form == targetForm || weight <= .001f) continue;
                _form = form;
                var operations = new List<SurfaceOp>();
                if (form == WidgetForm.Capsule)
                {
                    DrawCapsule(operations);
                    var size = LayoutSize(form); _capsuleContent.Size = size;
                    _capsuleClockInk?.Dispose();
                    ReplaceSurface(ref _capsuleSurface, _capsuleBrush, RenderTextLayer("Capsule", size.X, size.Y, operations));
                }
                else if (form == WidgetForm.Peek)
                {
                    DrawPeek(operations);
                    var size = LayoutSize(form); _peekContent.Size = size;
                    ReplaceSurface(ref _peekSurface, _peekBrush, RenderTextLayer("Peek", size.X, size.Y, operations));
                }
                else
                {
                    DrawDetails(operations);
                    var size = LayoutSize(form); _content.Size = size;
                    ReplaceSurface(ref _contentSurface, _contentBrush, RenderTextLayer("Details", size.X, size.Y, operations));
                    var body = new List<SurfaceOp>();
                    // The home page has no details body; during the collapse transition the
                    // body surface can still be 0 DIP tall, which the native renderer rejects.
                    if (!HomePage && size.X > 0 && _detailsBody.Size.Y > 0)
                    {
                        DrawDetailsBody(body, ResponsiveDetails());
                        ReplaceSurface(ref _detailsSurface, _detailsBrush, RenderTextLayer("DetailsBody", size.X, _detailsBody.Size.Y, body));
                    }
                    if (_timelineSurface is not null)
                    {
                        _lastTimeline = null;
                        UpdateTimelineCore(_media, Stopwatch.GetTimestamp());
                    }
                }
                UpdateMetadata(form, false);
            }
        }
        finally { _form = targetForm; }
    }

    /// <summary>Call only while Details is visible. Metadata and cover are not redrawn.</summary>
    public void UpdateTimeline(MediaSnapshot media, long monotonicNow)
    {
        EnsureUiThread();
        if (_disposed) return;
        try { UpdateTimelineCore(media, monotonicNow); }
        catch { InvalidateDrawingCaches(); throw; }
    }

    void UpdateTimelineCore(MediaSnapshot media, long monotonicNow)
    {
        _media = media;
        if (_form != WidgetForm.Details || WidgetsPage || HomePage || !media.Connected) return;
        bool valid = media.Timeline?.Valid == true;
        var position = _seekPreview ?? PositionAt(media, monotonicNow);
        var duration = valid ? media.Timeline!.End - media.Timeline.Start : TimeSpan.Zero;
        float fraction = valid ? (float)Math.Clamp((position - media.Timeline!.Start).TotalSeconds / duration.TotalSeconds, 0, 1) : 0;
        // A quarter-second tick moves only the narrow timeline surface. Time labels round to seconds.
        bool seek = SeekAvailable(media);
        const float trackWidth = 248;
        var key = new TimelineKey(valid, seek, (int)Math.Round(fraction * trackWidth),
            valid ? FormatTime(position - media.Timeline!.Start) : string.Empty,
            valid ? FormatTime(duration) : string.Empty, _seekPreview.HasValue, _text, _accent, CurrentTypography);
        if (_lastTimeline == key) return;
        var operations = new List<SurfaceOp>();
        _timelineTrack.SetBounds(0,4,trackWidth,4,2);
        _timelineTrack.SetAppearance(0x15CAD1D8,_text,_accessibility.HighContrast);
        _timelineTrack.Visual.IsVisible=valid;
        _timelineThumb.SetBounds(Math.Clamp(fraction*trackWidth-4,0,trackWidth-8),2,8,8,4);
        _timelineThumb.SetAppearance(0xFFF6F8FA,_text,_accessibility.HighContrast);
        _timelineThumb.Visual.IsVisible=valid&&seek;
        if (valid)
        {
            if (fraction > 0) RoundRect(operations, 0, 4, Math.Max(2, fraction * trackWidth), 4, 2, _accent);
            Text(operations, key.Position, 0, 20, 112, 18, TextRole.Caption, InkRole.Secondary, flags: TextFlags.TabularNumbers);
            Text(operations, key.Duration, 136, 20, 112, 18, TextRole.Caption, InkRole.Secondary, center: true, flags: TextFlags.TabularNumbers);
        }
        else
        {
            Text(operations, "播放时间暂不可用", 0, 10, trackWidth, 24, TextRole.Caption, InkRole.Secondary, center: true);
        }
        _timeline.Size = new(trackWidth, 38);
        var timelineLayout = ResponsiveDetails();
        _timeline.Offset = new(timelineLayout?.Timeline.X ?? 56, timelineLayout?.Timeline.Y ?? 300, 0);
        ReplaceSurface(ref _timelineSurface, _timelineBrush, RenderTextLayer("Timeline", trackWidth, 38, operations));
        _lastTimeline = key;
        BuildControls();
    }

    public static TimeSpan PositionAt(MediaSnapshot media, long monotonicNow)
    {
        if (media.Timeline is not { Valid: true } time) return TimeSpan.Zero;
        double rate = media.Rate is double suppliedRate && double.IsFinite(suppliedRate) && suppliedRate >= 0 ? suppliedRate : 1;
        double elapsed = media.State == PlaybackState.Playing
            ? Math.Max(0, (monotonicNow - time.ReceivedTimestamp) / (double)Stopwatch.Frequency) * rate
            : 0;
        var ticks = time.Position.Ticks + elapsed * TimeSpan.TicksPerSecond;
        return TimeSpan.FromTicks((long)Math.Clamp(ticks, time.Start.Ticks, time.End.Ticks));
    }

    /// <summary>Preview is local only. The controller commits one media command on pointer release.</summary>
    public void SetSeekPreview(TimeSpan? position)
    {
        EnsureUiThread();
        _seekPreview = position;
        _lastTimeline = null;
        UpdateTimeline(_media, Stopwatch.GetTimestamp());
    }

    public TimeSpan? SeekPositionFor(float dipX)
    {
        if (_form != WidgetForm.Details || !float.IsFinite(dipX) || !SeekAvailable(_media) || _media.Timeline is not { Valid: true } time) return null;
        var track = ResponsiveDetails()?.Timeline;
        float trackX = track?.X ?? (_presentedSize.X - 360) / 2 + 56;
        float trackWidth = track?.Width ?? 248;
        double fraction = Math.Clamp((dipX - trackX) / trackWidth, 0, 1);
        long ticks = time.Start.Ticks + (long)((time.End.Ticks - time.Start.Ticks) * fraction);
        return TimeSpan.FromTicks(Math.Clamp(ticks, Math.Max(time.Start.Ticks, time.MinSeek.Ticks), Math.Min(time.End.Ticks, time.MaxSeek.Ticks)));
    }

    public int HitTest(float x, float y)
    {
        if(_form==WidgetForm.Details){int chrome=_chrome.HitTest(x,y);if(chrome>=0)return chrome;}
        if(_placement.RevealHitRegion is { } reveal&&reveal.Contains(x,y))return WidgetActions.CompanionReveal;
        // Disabled buttons consume their click too; they must never bubble into Body.
        for (int i = _controls.Count - 1; i >= 0; --i)
        {
            var control = _controls[i];
            if (x >= control.X && y >= control.Y && x < control.X + control.Width && y < control.Y + control.Height)
            {
                if (control.Id == WidgetActions.HomeClosePopover && HomePopoverOpen && HomeGeometry.Popover.Contains(x, y))
                    return WidgetActions.HomePopupBody;
                return control.Id;
            }
        }
        return WidgetActions.Body;
    }

    public void ApplyPresentation(WidgetPose pose)
    {
        EnsureUiThread();
        if (_disposed) return;
        _root.Opacity = pose.Opacity;
        _root.Size = _presentedSize = new(pose.Width, pose.Height);
        _bodyRoot.Size = _presentedSize;
        _contentWeights = new((float)Math.Clamp(_layerOpacity[0].Sample(_motionNow).Position, 0, 1),
            (float)Math.Clamp(_layerOpacity[1].Sample(_motionNow).Position, 0, 1),
            (float)Math.Clamp(_layerOpacity[2].Sample(_motionNow).Position, 0, 1));
        ApplyContentWeights(_contentWeights);
        _artworkViewport.Size = _presentedSize;
        _artworkViewportClip.TopInset = Math.Max(0, (float)_artworkClipTop.Sample(_motionNow).Position);
        _artworkViewportClip.BottomInset = Math.Max(0, pose.Height - (float)_artworkClipBottom.Sample(_motionNow).Position);
        _artworkPose = _artworkTransition.Sample(_motionNow);
        _artwork.Offset = _previousArtwork.Offset = new(_artworkPose.X, _artworkPose.Y, 0);
        _artworkMotion.CenterPoint = _previousArtworkMotion.CenterPoint = new(_artworkPose.X + _artworkPose.Width / 2, _artworkPose.Y + _artworkPose.Height / 2, 0);
        float artworkScale = _artworkPose.Width / 208;
        _artwork.Scale = _previousArtwork.Scale = new(artworkScale, artworkScale, 1);
        _artworkClip.CornerRadius = _previousArtworkClip.CornerRadius = new(_artworkPose.Radius / artworkScale);
        PresentLayer(_capsuleContent, 0, pose.Width);
        PresentLayer(_peekContent, 1, pose.Width);
        PresentLayer(_content, 2, pose.Width);
        _peekButtons.Offset = _peekContent.Offset; _detailsButtons.Offset = _content.Offset;
        _detailsViewport.Offset = new((pose.Width - _content.Size.X) / 2,
            ResponsiveDetails() is { } details ? details.BodyViewport.Y : 56, 0);
        var timelineLayout = ResponsiveDetails();
        _timeline.Offset = new(
            timelineLayout?.Timeline.X ?? (pose.Width - 360) / 2 + 56,
            timelineLayout?.Timeline.Y ?? 300, 0);
        // Home uses its own Dock. The legacy five-slot chrome dock contains routes and
        // previews for removed pages, so keep those controls hidden in every Details page.
        _chrome.SetDockVisible(false);
        UpdateAuxiliaryPlacement();
        BeginSystemTransportPresentation();
        BuildControls();
        var feedbackControl = _controls.FirstOrDefault(control => control.Id == _feedbackId);
        if (feedbackControl is not null) _feedback.Offset = new(feedbackControl.X, feedbackControl.Y, 0);
    }

    static Vector3 WeightsFor(WidgetForm form) => form switch
    {
        WidgetForm.Capsule => new(1, 0, 0),
        WidgetForm.Peek => new(0, 1, 0),
        _ => new(0, 0, 1)
    };

    void RetargetContent(WidgetForm form, bool immediate)
    {
        var target = WeightsFor(form);
        for (int i = 0; i < 3; i++)
        {
            bool entering = target[i] > 0;
            bool hidden = _layerOpacity[i].Sample(_motionNow).Position <= .001;
            if (entering && hidden && !immediate) { _layerShift[i].Jump(9); _layerScale[i].Jump(.985); }
            _layerOpacity[i].Retarget(target[i], _motionNow, entering ? 230 : 130, immediate, entering && hidden ? 45 : 0);
            _layerShift[i].Retarget(entering ? 0 : -5, _motionNow, 280, immediate);
            _layerScale[i].Retarget(entering ? 1 : .99, _motionNow, 280, immediate);
        }
        _detailsBodyOpacity.Retarget(target.Z, _motionNow, target.Z > 0 ? 240 : 110, immediate, target.Z > 0 ? 70 : 0);
        _peekTransportOpacity.Retarget(target.Y, _motionNow, target.Y > 0 ? 230 : 100, immediate, target.Y > 0 ? 95 : 0);
        _detailsTransportOpacity.Retarget(target.Z, _motionNow, target.Z > 0 ? 230 : 100, immediate, target.Z > 0 ? 110 : 0);
    }

    void PresentLayer(Visual visual, int index, float width)
    {
        float shift = (float)_layerShift[index].Sample(_motionNow).Position;
        float scale = (float)_layerScale[index].Sample(_motionNow).Position;
        visual.CenterPoint = new(visual.Size.X / 2, 0, 0);
        visual.Scale = new(scale, scale, 1);
        visual.Offset = new((width - visual.Size.X) / 2, shift, 0);
    }

    void ApplyContentWeights(Vector3 weights)
    {
        float bodyWeight = (float)Math.Clamp(_detailsBodyOpacity.Sample(_motionNow).Position, 0, 1);
        float peekTransport = (float)Math.Clamp(_peekTransportOpacity.Sample(_motionNow).Position, 0, 1);
        float detailsTransport = (float)Math.Clamp(_detailsTransportOpacity.Sample(_motionNow).Position, 0, 1);
        _capsuleContent.Opacity = weights.X;
        _peekContent.Opacity = weights.Y;
        _content.Opacity = weights.Z;
        _detailsViewport.Opacity = HomePage ? 0 : bodyWeight;
        if (_homeVisual is not null) _homeVisual.Visual.Opacity = HomePage ? bodyWeight : 0;
        bool detailsMusic = !HomePage && !WidgetsPage;
        _timeline.Opacity = detailsMusic && _media.Connected && _media.Timeline?.Valid == true ? detailsTransport : 0;
        _peekButtons.Opacity = _media.Connected ? peekTransport : 0;
        _detailsButtons.Opacity = detailsMusic && _media.Connected ? detailsTransport : 0;
        // The protected 8-band spectrum belongs to the capsule/peek body; the redesigned
        // Details page uses the space below the artist for the fixed timeline and transport.
        _audioBars.Opacity = _preferences.AudioVisualizationEnabled ? Math.Clamp((MusicFor(WidgetForm.Capsule) ? weights.X : 0) + (_media.Connected ? weights.Y : 0), 0, 1) : 0;
        // The remembered Details page must not hide Capsule/Peek artwork. Only its
        // own Details weight is suppressed on Home and Widgets.
        _artworkViewport.Opacity = _media.Connected ? Math.Clamp(
            (MusicFor(WidgetForm.Capsule) && !CompanionVisible ? weights.X : 0) + weights.Y
            + (detailsMusic ? weights.Z : 0), 0, 1) : 0;
        ApplyBoardVisibility(weights);
    }

    DetailsResponsiveResult? _detailsLayout;
    (Vector2 Size, bool HasTimeline, int Plugins) _detailsLayoutKey;
    /// <summary>Responsive Details/music geometry; null for the empty (disconnected) page or the board page.</summary>
    DetailsResponsiveResult? ResponsiveDetails()
    {
        if (!_media.Connected || WidgetsPage || _form != WidgetForm.Details) return null;
        var size = LayoutSize(WidgetForm.Details);
        bool hasTimeline = _media.Timeline?.Valid == true;
        int plugins = Math.Min(4, _pluginVisuals.Count);
        var key = (size, hasTimeline, plugins);
        if (_detailsLayout is null || _detailsLayoutKey != key)
        {
            _detailsLayout = DetailsResponsiveLayout.Resolve(new(size, hasTimeline, plugins * 112f));
            _detailsLayoutKey = key;
        }
        return _detailsLayout;
    }

    WidgetPose ArtworkPoseFor(WidgetForm form) => form switch
    {
        WidgetForm.Capsule => new(18, 14, 24, 24, 12),
        WidgetForm.Peek => new(24, 64, 48, 48, 14),
        _ => ResponsiveDetails() is { } details
            ? new(details.Cover.X, details.Cover.Y, details.Cover.Width, details.Cover.Height, details.CoverRadius)
            : new(96, 70, 168, 168, 16)
    };

    Vector2 ArtworkViewportFor(WidgetForm form, Vector2 size) => form == WidgetForm.Details
        ? ResponsiveDetails() is { } details
            ? new(DetailsResponsiveLayout.ArtworkTop, details.ArtworkViewport.Bottom)
            : new(56, 80 + Math.Max(1, Math.Min(112, size.Y - 160)))
        : new(0, size.Y);

    // Empty Details keeps its compact 112 DIP body; the music page takes the responsive
    // body viewport from the shared geometry (title top .. timeline/toggle top).
    float DetailsBodyViewportHeight(Vector2 size) => ResponsiveDetails() is { } details
        ? details.BodyViewport.Height
        : Math.Max(1, Math.Min(112, size.Y - 160));

    void UpdateAuxiliaryPlacement()
    {
        float weight=_contentWeights.X+_contentWeights.Y+_contentWeights.Z;
        if(weight<=.001f)return;
        float capsuleWidth=LayoutSize(WidgetForm.Capsule).X;
        float audioX=(((_presentedSize.X-capsuleWidth)/2+capsuleWidth-50)*_contentWeights.X+
            ((_presentedSize.X-320)/2+266)*_contentWeights.Y+((_presentedSize.X-360)/2+136)*_contentWeights.Z)/weight;
        float audioY=(14*_contentWeights.X+72*_contentWeights.Y+(350-_detailsScroll)*_contentWeights.Z)/weight;
        _audioBars.Offset=new(audioX,audioY,0);
        _audioBars.Scale=new(1+1.2f*_contentWeights.Z,1,1);
        float detailsWeight = _contentWeights.Z/weight;
        float clipTop = 80*detailsWeight;
        float clipBottom = _presentedSize.Y*(1-detailsWeight)+(80+DetailsBodyViewportHeight(LayoutSize(WidgetForm.Details)))*detailsWeight;
        _audioClip.TopInset = Math.Max(0,clipTop-audioY);
        _audioClip.BottomInset = Math.Max(0,audioY+24-clipBottom);
        _chrome.SetLayout(_presentedSize.X, _presentedSize.Y);
        _chrome.Visual.Opacity=_contentWeights.Z;
        bool reduced = _preferences.ReducedMotion || !_uiSettings.AnimationsEnabled;
        uint face = _accessibility.HighContrast ? _button : _companionState?.Color ?? _preferences.CompanionColor;
        uint eyes = _accessibility.HighContrast ? _text : FaceInk(face);
        bool musicPeekWithCompanion = _media.Connected && CompanionVisible && _contentWeights.Y > .001f;
        float peekFaceOpticalOffset = 0;
        if (musicPeekWithCompanion)
        {
            EnsureClockReadingMeasurements();
            if (ClockPrimary.Length > 0 || ClockSecondary.Length > 0)
            {
                // The reading field includes 4 DIP of support padding. Center the
                // fixed 58-DIP face + 8-DIP gap + visible ink group in the unchanged slot.
                float inkAdvance = Math.Clamp(_peekClockReadingWidth - 4, 0, PeekClockWidth);
                peekFaceOpticalOffset = (PeekClockWidth - inkAdvance) / 2;
            }
            else
            {
                // With every clock field disabled, the face is the complete
                // header. An empty time slot must not leave it stranded left.
                peekFaceOpticalOffset = (320 - 58) / 2f - (PeekClockX - 66);
            }
        }
        var headerParallax=OwnedHeaderParallax(true);
        _peekCompanion.Present((_presentedSize.X - (_media.Connected ? 320 : 240)) / 2 +
            (_media.Connected ? PeekClockX - 66 + peekFaceOpticalOffset : 91) + headerParallax.X,
            (_media.Connected && ClockSecondary.Length > 0 ? 16 : 14)+headerParallax.Y, CompanionVisible ? _contentWeights.Y : 0, face, eyes, reduced);
        _peekCompanion.Gaze(_gazePointer.X, _gazePointer.Y, _gazeInside && _preferences.CompanionFollowMouse);
        _chrome.SetHomeGaze(_gazePointer.X, _gazePointer.Y, _gazeInside && _preferences.CompanionFollowMouse);
        if(!CompanionVisible || _form!=WidgetForm.Capsule){_companion.Visual.Opacity=0;return;}
        var pose=_placement.Pose;
        if(_form==WidgetForm.Details&&pose.PlacementMode==CompanionPlacementMode.NavigationIntro)
            pose=pose with {BodyAnchor=pose.BodyAnchor+new Vector2((_presentedSize.X-LayoutSize(_form).X)/2,0)};
        _companionSize=PlacementBodySize;
        _companionX=pose.BodyAnchor.X-_companionSize/2;_companionY=pose.BodyAnchor.Y-_companionSize/2;
        _companion.SetBounds(_companionX,_companionY,_companionSize,Math.Min(1,weight));
        _companion.SetPose(pose,_companionSize,null,_preferences.ReducedMotion);
        _companion.SetLiquid(_placement.LiquidFrame, pose.BodyAnchor, _companionSize);
        _companion.SetGaze(new((_gazePointer.X-pose.BodyAnchor.X)/42,(_gazePointer.Y-pose.BodyAnchor.Y)/56),_gazeInside);
    }

    // The clock Peek is shorter than the music Peek. Reserve the complete
    // ears/horns envelope in its narrow side gutter, including the safety gap.
    float PlacementBodySize => _form == WidgetForm.Capsule ? 32 :
        _form == WidgetForm.Peek && !_media.Connected ? 30 : 40;

    void ConfigurePlacement()
    {
        if (_form == WidgetForm.Peek)
        {
            // The new top strip has no wandering or artwork-hide anchors.
            _placement.SetAvailability(false, _sceneVisible, _preferences.ReducedMotion, _motionNow);
            return;
        }
        var size=LayoutSize(_form);float body=PlacementBodySize;
        string key=$"{_form}/{size}/{_media.Connected}/{_media.ContentVersion}/{_media.SessionToken}/{_preferences.CompanionAppearanceId}";
        _placement.SetAppearance(_preferences.CompanionAppearanceId,_motionNow);
        if(key!=_placementKey)
        {
            _placementKey=key;
            var protectedRegions=new List<FloatRect>();var covers=new List<HideAnchor>();
            Vector2 preferred=new(40,26);
            if(_form==WidgetForm.Details)
            {
                preferred=new(32,130);
                // The home handle replaces the old navigation strip; the dock keeps its own
                // guard band so a future companion reveal can never cover the bottom chrome.
                protectedRegions.Add(new(20,8,320,40));
                protectedRegions.Add(new(16,size.Y-72,328,72));
                // The plugin tools affordance sits above the dock guard band with an 8 DIP
                // gap: its bottom edge is size.Y-72, so the dock never covers it.
                if (_pluginPages.Count > 0) protectedRegions.Add(new(16,size.Y-104,32,32));
                var geometry = ResponsiveDetails();
                float viewportBottom = geometry?.BodyViewport.Bottom ?? 80 + DetailsBodyViewportHeight(size);
                if(_media.Connected){float artTop=Math.Max(80,88-_detailsScroll),artBottom=Math.Min(viewportBottom,280-_detailsScroll);
                    var art=new FloatRect(84,artTop,192,Math.Max(0,artBottom-artTop));
                    if(art.Height>0)protectedRegions.Add(art);
                    float metadataTop=Math.Max(80,288-_detailsScroll),metadataBottom=Math.Min(viewportBottom,376-_detailsScroll);
                    if(metadataBottom>metadataTop)protectedRegions.Add(new(24,metadataTop,312,metadataBottom-metadataTop));
                    float waveTop=Math.Max(80,350-_detailsScroll),waveBottom=Math.Min(viewportBottom,374-_detailsScroll);
                    if(waveBottom>waveTop)protectedRegions.Add(new(136,waveTop,88,waveBottom-waveTop));
                    protectedRegions.Add(geometry is null
                        ? new(96,DetailsTransportY,168,56)
                        : new FloatRect(geometry.PreviousButton.X,
                            Math.Min(geometry.PreviousButton.Y, geometry.ToggleButton.Y),
                            geometry.NextButton.Right - geometry.PreviousButton.X,
                            Math.Max(geometry.PreviousButton.Bottom, geometry.ToggleButton.Bottom)
                                - Math.Min(geometry.PreviousButton.Y, geometry.ToggleButton.Y)));
                    if(_media.Timeline?.Valid==true)protectedRegions.Add(geometry is null
                        ? new(56,Math.Min(374,size.Y-138),248,48)
                        : new FloatRect(geometry.Timeline.X,geometry.Timeline.Y,geometry.Timeline.Width,geometry.Timeline.Height));
                    }
                else protectedRegions.AddRange(new FloatRect[]{new(130,105,100,84),new(100,size.Y-76,160,36)});
                float pluginBase = geometry is null ? 80 : geometry.BodyViewport.Y;
                float pluginTop = geometry is not null
                    ? Math.Max(geometry.Title.Bottom, geometry.Artist.Bottom) - geometry.BodyViewport.Y
                    : (_media.Connected ? 296 : 124);
                foreach (var contribution in _pluginVisuals.Take(4))
                {
                    foreach (var operation in contribution.Value.Take(128))
                    {
                        if (!float.IsFinite(operation.X) || !float.IsFinite(operation.Y) || !float.IsFinite(operation.Width) || !float.IsFinite(operation.Height)) continue;
                        float x = Math.Clamp(operation.X,0,size.X-48), y = Math.Clamp(operation.Y,0,96);
                        float width = Math.Clamp(operation.Width,0,size.X-48-x), height = Math.Clamp(operation.Height,0,96-y);
                        float top = Math.Max(pluginBase,pluginBase+pluginTop+y-_detailsScroll), bottom = Math.Min(viewportBottom,pluginBase+pluginTop+y+height-_detailsScroll);
                        if (width > 0 && bottom > top) protectedRegions.Add(new(24+x,top,width,bottom-top));
                    }
                    pluginTop += 112;
                }
            }
            _placement.UpdateLayout(new(_form,new(0,0,size.X,size.Y),24,body,preferred,protectedRegions,covers),_motionNow);
        }
        _placement.SetAvailability(CompanionVisible && _form==WidgetForm.Capsule,_sceneVisible,_preferences.ReducedMotion,_motionNow);
    }
    public void BeginDetails(long sequence,double now)
    {
        _motionNow=now;_openSequence=sequence;
        var size=LayoutSize(WidgetForm.Details);
        _chrome.SetLayout(size.X,size.Y);
        _chrome.BeginOpen(sequence,now,_preferences.ReducedMotion);
        ConfigurePlacement();
        UpdateAuxiliaryPlacement();BuildControls();
    }
    public void EndDetails(){_chrome.Close(retainPresentation:true);SetCapture(PointerCaptureOwner.None,_motionNow);}
    public void SetSceneVisible(bool visible,double now)
    {
        _motionNow=now;_sceneVisible=visible;
        if (!visible) ClearCompanionPointerGaze();
        ConfigurePlacement();
    }
    public void SetCapture(PointerCaptureOwner owner,double now)
    {_motionNow=now;_captureOwner=owner;_placement.SetCapture(owner,now);}
    public bool RouteSpecialPointer(HostPointerEvent pointer,int action,double now)
    {
        _motionNow=now;
        if(pointer.Kind==HostEventKind.Invoke&&action==WidgetActions.CompanionReveal){_placement.Reveal(now);return true;}
        if(_form==WidgetForm.Details)
        {
            if(pointer.Kind is HostEventKind.Enter or HostEventKind.Move)_chrome.PointerMoved(pointer.X,pointer.Y,now);
            else if(pointer.Kind is HostEventKind.Leave or HostEventKind.CaptureLost)_chrome.PointerExited(now);
        }
        // A captured surface keeps every event until it releases: the board must never lose
        // a drag to the dock/companion routes just because the pointer crossed them.
        if(_captureOwner is PointerCaptureOwner.WidgetDrag or PointerCaptureOwner.Timeline or PointerCaptureOwner.WidgetBoard)return false;
        if(pointer.Kind==HostEventKind.Down&&_placement.TryCaptureReveal(new(pointer.X,pointer.Y),_placement.OpenSequence))
        {SetCapture(PointerCaptureOwner.CompanionReveal,now);return true;}
        if(_captureOwner==PointerCaptureOwner.CompanionReveal)
        {
            if(pointer.Kind is HostEventKind.Up or HostEventKind.CaptureLost)
            {_placement.CompleteReveal(_placement.OpenSequence,now,pointer.Kind==HostEventKind.CaptureLost);SetCapture(PointerCaptureOwner.None,now);}
            return true;
        }
        if(action is >= WidgetActions.DockFirst and <= WidgetActions.DockFirst+4 || _captureOwner==PointerCaptureOwner.Navigation)
        {
            if(pointer.Kind==HostEventKind.Down)
            {_dockPressedAction=action;_chrome.SetPointerFeedback(action,true,now);SetCapture(PointerCaptureOwner.Navigation,now);}
            else if(pointer.Kind==HostEventKind.Move)
            {
                // Sliding off the pressed destination cancels it: the release must not
                // activate whichever destination happens to be under the pointer.
                if(_dockPressedAction>=0 && action!=_dockPressedAction)
                {_chrome.SetPointerFeedback(_dockPressedAction,false,now);_dockPressedAction=-2;}
            }
            else if(pointer.Kind==HostEventKind.Up)
            {
                int pressed=_dockPressedAction;_dockPressedAction=-1;
                _chrome.SetPointerFeedback(action,false,now);
                if(pressed==action)_chrome.Activate(action,now);
                else if(pressed>=0)_chrome.SetPointerFeedback(pressed,false,now);
                SetCapture(PointerCaptureOwner.None,now);
            }
            else if(pointer.Kind==HostEventKind.Invoke)
            {_dockPressedAction=-1;_chrome.Activate(action,now);}
            else if(pointer.Kind is HostEventKind.Leave or HostEventKind.CaptureLost)
            {
                if(_dockPressedAction>=0)_chrome.SetPointerFeedback(_dockPressedAction,false,now);
                _dockPressedAction=-1;
                if(pointer.Kind==HostEventKind.CaptureLost)SetCapture(PointerCaptureOwner.None,now);
            }
            // A selection under a stationary hover may require no animation tick.
            // Publish its accessibility label immediately instead of waiting for idle motion.
            BuildControls();
            return true;
        }
        return false;
    }
    public void TickCompanion(double now)
    {
        if(_disposed)return;_motionNow=now;
        _chrome.Tick(now);_placement.Tick(now);UpdateAuxiliaryPlacement();BuildControls();
    }

    public void UpdateCompanionGaze(HostPointerEvent pointer)
    {
        if (_form == WidgetForm.Capsule) return; // Desktop polling owns capsule gaze, including after Leave.
        if (!_preferences.CompanionFollowMouse || _preferences.ReducedMotion) { ClearCompanionPointerGaze(); return; }
        _gazeInside = pointer.Kind is not (HostEventKind.Leave or HostEventKind.CaptureLost);
        _gazePointer = new(pointer.X, pointer.Y);
        UpdateAuxiliaryPlacement();
    }

    public void SetCompanionPointerGaze(float x, float y)
    {
        if (!_preferences.CompanionFollowMouse || _preferences.ReducedMotion || !CompanionVisible) return;
        _gazeInside = true; _gazePointer = new(x, y);
        _companion.SetGaze(new((x - _companionX - _companionSize / 2) / 42, (y - _companionY - _companionSize / 2) / 56), true);
    }
    public void ClearCompanionPointerGaze()
    {
        _gazeInside = false; _companion.ClearPointerGaze();
        _peekCompanion.Gaze(0, 0, false); _chrome.SetHomeGaze(0, 0, false);
    }

    void AdjustArtworkScroll(float delta)
    {
        if (delta == 0) return;
        _artworkTransition.Translate(0, -delta, _motionNow);
        _artworkPose = _artworkPose with { Y = _artworkPose.Y - delta };
        _artwork.Offset = _previousArtwork.Offset = new(_artworkPose.X, _artworkPose.Y, 0);
    }

    void FadeTo(Visual visual, float opacity, int? durationMs = null)
    {
        if (_preferences.ReducedMotion || _lastContent is null)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = opacity;
            return;
        }
        using var easing = _host.Compositor.CreateCubicBezierEasingFunction(new(.2f, 0), new(0, 1));
        using var animation = _host.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, opacity, easing);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs ?? (opacity == 0 ? 100 : 160));
        visual.StartAnimation("Opacity", animation);
    }

    void UpdateArtwork()
    {
        bool reduced = _preferences.ReducedMotion || !_uiSettings.AnimationsEnabled;
        if (reduced)
        {
            _artworkTimer.Stop();
            _artworkUpdatePending = false;
            _previousArtwork.StopAnimation("Opacity");
            _previousArtwork.Opacity = 0;
            FinishArtworkMotion();
        }
        if (!_media.Connected)
        {
            _artworkTimer.Stop();
            _artworkUpdatePending = false;
            if (_artworkShown || _preferences.ReducedMotion)
            {
                FadeTo(_artwork, 0);
                FadeTo(_previousArtwork, 0);
            }
            _artworkShown = false;
            return;
        }
        bool replaced = false;
        if (_artworkDirty || !ReferenceEquals(_lastArtwork, _media.Cover))
        {
            if (!reduced && !_artworkDirty && _artworkTimer.IsRunning)
            {
                // Finish the current 160 ms blend, then show the latest cover.
                // Replacing the outgoing surface halfway through would cause a flash.
                _artworkUpdatePending = true;
                return;
            }
            bool animateReplacement = !_artworkDirty;
            if (_artworkDirty) { _artworkTimer.Stop(); _artworkUpdatePending = false; }
            var operations = new List<SurfaceOp>();
            Cover(operations, 0, 0, 208);
            var replacement = _host.RenderSurface(208, 208, operations);
            var oldPrevious = _previousArtworkSurface;
            try
            {
                _previousArtworkBrush.Surface = _artworkSurface;
                _artworkBrush.Surface = replacement;
            }
            catch
            {
                _previousArtworkBrush.Surface = oldPrevious;
                replacement.Dispose();
                throw;
            }
            _previousArtworkSurface = _artworkSurface;
            _artworkSurface = replacement;
            oldPrevious?.Dispose();
            _lastArtwork = _media.Cover;
            _artworkDirty = false;
            replaced = true;
            // Keep the new cover opaque beneath the outgoing one: a double fade
            // would let the glass show through halfway through the blend.
            _artwork.StopAnimation("Opacity");
            _artwork.Opacity = 1;
            if (animateReplacement && _previousArtworkSurface is not null && !reduced)
            {
                _previousArtwork.StopAnimation("Opacity");
                _previousArtwork.Opacity = 1;
                FadeTo(_previousArtwork, 0, 180);
                AnimateArtworkMotion(_artworkMotion, _activeTrackDirection * 20, 0, _activeTrackDirection * 8, 0);
                AnimateArtworkMotion(_previousArtworkMotion, 0, -_activeTrackDirection * 20, 0, -_activeTrackDirection * 8);
                _artworkTimer.Start();
            }
            else { _previousArtwork.StopAnimation("Opacity"); _previousArtwork.Opacity = 0; }
        }
        if (!replaced && (!_artworkShown || _preferences.ReducedMotion)) FadeTo(_artwork, 1);
        _artworkShown = true;
    }

    void AnimateArtworkMotion(Visual visual, float fromX, float toX, float fromAngle, float toAngle)
    {
        using var easing = _host.Compositor.CreateCubicBezierEasingFunction(new(.16f, 1.12f), new(.30f, 1));
        void Animate(string property, float from, float to)
        {
            visual.StopAnimation(property);
            using var animation = _host.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, from); animation.InsertKeyFrame(1, to, easing);
            animation.Duration = TimeSpan.FromMilliseconds(300); visual.StartAnimation(property, animation);
        }
        Animate("Offset.X", fromX, toX); Animate("RotationAngleInDegrees", fromAngle, toAngle);
    }
    void FinishArtworkMotion()
    {
        foreach (var visual in new[] { _artworkMotion, _previousArtworkMotion })
        { visual.StopAnimation("Offset.X"); visual.StopAnimation("RotationAngleInDegrees"); visual.Offset = Vector3.Zero; visual.RotationAngleInDegrees = 0; }
    }

    void OnArtworkTransitionCompleted(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        timer.Stop();
        if (_disposed || !_artworkUpdatePending) return;
        _artworkUpdatePending = false;
        try { UpdateArtwork(); }
        catch (Exception error) { InvalidateDrawingCaches(); RenderingFailed?.Invoke(error); }
    }

    /// <summary>Subtle button hover/press overlay, animated by composition without repeated drawing.</summary>
    public void SetPointerFeedback(int actionId, bool pressed = false)
    {
        EnsureUiThread();
        try { UpdateFeedback(actionId, pressed, force: false); }
        catch { InvalidateDrawingCaches(); throw; }
    }

    /// <summary>Keyboard/UIA activation receives the same complete tactile response as a click.</summary>
    public void AnimateTransportActivation(int actionId)
    {
        EnsureUiThread();
        if (!_controls.Any(control => control.Id == actionId && control.Enabled)) return;
        var form = _controlForms.GetValueOrDefault(actionId, _form);
        if(OwnedGlassEnabled&&actionId is >=WidgetActions.Previous and <=WidgetActions.Next){
            _ownedControlPulseUntil[actionId-WidgetActions.Previous]=_motionNow+110;_ownedControlMoving=true;
        }
        if (_buttons.TryGetValue((form, actionId), out var button)) button.Activate(_preferences.ReducedMotion);
    }

    public void PrepareTrackTransition(int actionId)
    {
        _requestedTrackDirection = actionId == WidgetActions.Previous ? -1 : actionId == WidgetActions.Next ? 1 : 0;
        _trackRequestedAt = Stopwatch.GetTimestamp(); _trackRequestedSession = _media.SessionToken;
    }

    void UpdateFeedback(int actionId, bool pressed, bool force)
    {
        if (_disposed || !force && !_feedbackDirty && _feedbackId == actionId && _feedbackPressed == pressed) return;
        // The chrome draws its own hover/press scale above the body layers.
        if (!force && _form == WidgetForm.Details) _chrome.SetPointerFeedback(actionId, pressed, _motionNow);
        _feedbackId = actionId;
        _feedbackPressed = pressed;
        _feedbackDirty = true;
        foreach (var button in _buttons)
        {
            bool hovered = button.Key.Form == _controlForms.GetValueOrDefault(actionId, _form) && button.Key.Action == actionId;
            button.Value.SetFeedback(hovered, hovered && pressed, _preferences.ReducedMotion);
        }
        if(_host.IsExperimentalDesktopMaterialCandidate){_ownedControlMoving=true;SubmitOwnedGlass();}
        var control = _controls.FirstOrDefault(c => c.Id == actionId && c.Id != WidgetActions.Body && c.Id != WidgetActions.Seek && c.Enabled);
        if (control is null || actionId is WidgetActions.Previous or WidgetActions.Toggle or WidgetActions.Next)
        {
            _feedback.StopAnimation("Opacity");
            _feedback.Opacity = 0;
            _feedbackDirty = false;
            return;
        }
        var operations = new List<SurfaceOp>();
        // Whole-card controls keep the 16 DIP card radius; the generic pill radius
        // would turn the press veil into a dark ellipse over a 150x148 card.
        float feedbackRadius = WidgetBoardRenderer.IsCardPressTarget(control)
            ? Math.Min(16, Math.Min(control.Width, control.Height) / 2)
            : Math.Min(control.Width, control.Height) / 2;
        RoundRect(operations, 0, 0, control.Width, control.Height, feedbackRadius,
            pressed ? 0x26303A45u : 0x10303A45u);
        _feedback.Size = new(control.Width, control.Height);
        _feedback.Offset = new(control.X, control.Y, 0);
        ReplaceSurface(ref _feedbackSurface, _feedbackBrush, _host.RenderSurface(control.Width, control.Height, operations));
        _feedback.Opacity = 1;
        if (!_preferences.ReducedMotion)
        {
            _feedback.CenterPoint = new(control.Width / 2, control.Height / 2, 0);
            using var scale = _host.Compositor.CreateVector3KeyFrameAnimation();
            scale.InsertExpressionKeyFrame(0, "this.StartingValue");
            scale.InsertKeyFrame(1, new(pressed ? .97f : 1));
            scale.Duration = TimeSpan.FromMilliseconds(80);
            _feedback.StartAnimation("Scale", scale);
        }
        else { _feedback.StopAnimation("Scale"); _feedback.Scale = Vector3.One; }
        _feedbackDirty = false;
    }

    void DrawCapsule(List<SurfaceOp> operations)
    {
        float width = LayoutSize(WidgetForm.Capsule).X;
        if (MusicFor(WidgetForm.Capsule))
        {
            // Metadata owns its own short transition, independently of the clock.
        }
        else
        {
            float left = CompanionVisible ? 56 : 16, available = width - left - 16;
            bool secondary = ClockSecondary.Length > 0;
            Text(operations, ClockPrimary, left, secondary ? 5 : 10, available, secondary ? 27 : 32,
                _preferences.ClockShowTime ? TextRole.ClockCapsule : TextRole.Label, InkRole.Primary, center: true,
                flags: _preferences.ClockShowTime ? TextFlags.TabularNumbers : TextFlags.TabularDigitsOnly);
            if (secondary) Text(operations, ClockSecondary, left, 32, available, 16, TextRole.ClockLegacyAux, InkRole.Secondary, center: true);
        }
    }

    void DrawPeek(List<SurfaceOp> operations)
    {
        if (_media.Connected)
        {
            _disconnectedPeekText = null;
            // Face + clock share only the shell material: no nested header or clock plate.
            float clockX = PeekClockX, clockWidth = PeekClockWidth;
            bool secondary = ClockSecondary.Length > 0;
            if(!_host.IsExperimentalDesktopMaterialCandidate){
            Text(operations, ClockPrimary, clockX, secondary ? 10 : 15, clockWidth, 26, TextRole.ClockWhiteplate, InkRole.Primary, center: true,
                flags: _preferences.ClockShowTime ? TextFlags.TabularNumbers : TextFlags.TabularDigitsOnly, clockPlate: true);
            if (secondary) Text(operations, ClockSecondary, clockX, 36, clockWidth, 12, TextRole.ClockLegacyAux, InkRole.Secondary, center: true, clockPlate: true);
            }
            Transport(86, PeekTransportY, 44, WidgetActions.Previous, _media.Capabilities.Previous);
            Transport(138, PeekTransportY, 44, WidgetActions.Toggle, _media.Capabilities.Toggle);
            Transport(190, PeekTransportY, 44, WidgetActions.Next, _media.Capabilities.Next);
        }
        else
        {
            ClockWhiteplate(operations,20,52,200,ClockSecondary.Length > 0 ? 58 : 44,18);
            var clockOperations = new List<SurfaceOp>();
            bool ownedReading = OwnedGlassEnabled;
            Text(clockOperations, ClockPrimary, 24, 54, 192, 40,
                _preferences.ClockShowTime ? TextRole.ClockPeekHero : TextRole.PageTitle, InkRole.Primary, center: true,
                flags: _preferences.ClockShowTime ? TextFlags.TabularNumbers : TextFlags.TabularDigitsOnly, clockPlate: !ownedReading);
            if (ClockSecondary.Length > 0) Text(clockOperations, ClockSecondary, 24, 91, 192, 18, TextRole.Caption,
                ownedReading ? InkRole.Primary : InkRole.Secondary, center: true, clockPlate: !ownedReading);
            Text(clockOperations, "打开网易云音乐", 62, 115, 116, 20, TextRole.Caption,
                ownedReading ? InkRole.Primary : InkRole.Secondary, center: true);
            // Retain the actual outgoing form's text, not the next global media state.
            _disconnectedPeekText = clockOperations.Where(op => !string.IsNullOrEmpty(op.Text)).ToArray();
            _disconnectedPeekLargeClock = _preferences.ClockShowTime && ClockSecondary.Length == 0;
            operations.AddRange(clockOperations);
        }
    }

    float PeekClockWidth => ClockSecondary.Length > 0 ? 128 : 96;
    float PeekClockX => (320 - PeekClockWidth + (CompanionVisible ? 66 : 0)) / 2;
    const float PeekTransportY = 124;
    static uint FaceInk(uint body)
    {
        double r = ((body >> 16) & 255) / 255d, g = ((body >> 8) & 255) / 255d, b = (body & 255) / 255d;
        return .2126 * r * r + .7152 * g * g + .0722 * b * b > .55 ? 0xFF20252Cu : 0xFFFAFBFCu;
    }

    void DrawDetails(List<SurfaceOp> operations)
    {
        if (HomePage) { DrawHome(operations); return; }
        if (OwnedGlassEnabled && !_host.IsLiquidGlassCapture)
        {
            var support = ReadingMaterialPolicy.Resolve(_lightText, _preferences.TintOpacity);
            RoundRect(operations, 16, 12, 34, 34, 17,
                ((uint)support.Opacity8Bit << 24) | (support.ColorArgb & 0x00ffffffu));
        }
        Icon(operations, "\uE72B", 24, 20, 18,
            _accessibility.HighContrast ? _text : TypographyCatalog.Ink(InkRole.Primary, _lightText));
        if (WidgetsPage) { DrawBoard(operations); DrawPageNotice(operations); return; }
        var size = LayoutSize(WidgetForm.Details);
        float width = size.X, height = size.Y;
        if(_pluginPages.Count>0){RoundRect(operations,16,height-104,32,32,16,_button);Icon(operations,"\uE8A5",24,height-96,16,_secondary);}
        if (_media.Connected)
        {
            // Render and hit testing use one shared 44 DIP row, centered on the responsive
            // page transport band so cover and timeline spacing remain stable.
            var row = DetailsMediaControls(ResponsiveDetails());
            Transport(row.Previous.X, row.Previous.Y, row.Previous.Width,
                WidgetActions.Previous, _media.Capabilities.Previous);
            Transport(row.Toggle.X, row.Toggle.Y, row.Toggle.Width,
                WidgetActions.Toggle, _media.Capabilities.Toggle);
            Transport(row.Next.X, row.Next.Y, row.Next.Width,
                WidgetActions.Next, _media.Capabilities.Next);
        }
        else
        {
            // Explicit secondary action instead of bare text; y=162 keeps it below the
            // empty-state body copy and above the dock.
            RoundRect(operations,100,162,160,36,18,_button);
            Text(operations,"打开网易云音乐",100,162,160,36,TextRole.Caption,InkRole.Primary,center:true);
        }
        DrawPageNotice(operations);
    }

    // A restrained, persistent status line on the page that owns the failure. It never
    // covers the transport row or dock: on the music page it sits at the body's lower edge,
    // on the board page just above the dock inset.
    void DrawPageNotice(List<SurfaceOp> operations)
    {
        if (_form != WidgetForm.Details || string.IsNullOrWhiteSpace(_notice)) return;
        var size = LayoutSize(WidgetForm.Details);
        var geometry = ResponsiveDetails();
        float bodyBottom = geometry?.BodyViewport.Bottom ?? (WidgetsPage ? size.Y - 72 : 168);
        float y = Math.Max(64, bodyBottom - 26);
        var plate = new FloatRect(24, y, Math.Max(0, size.X - 48), 22);
        if (plate.Width <= 40) return;
        uint fill = _lightText ? 0xB320252C : 0xB3F7F9FB;
        RoundRect(operations, plate.X, plate.Y, plate.Width, plate.Height, 11, fill);
        Icon(operations, "\uE7BA", plate.X + 8, plate.Y + 4, 14, _secondary);
        string text = _notice!.Length > 30 ? _notice[..29] + "…" : _notice;
        Text(operations, text, plate.X + 28, plate.Y + 3, plate.Width - 34, 16, TextRole.Caption, InkRole.Primary);
    }

    void DrawDetailsBody(List<SurfaceOp> operations, DetailsResponsiveResult? geometry)
    {
        if (WidgetsPage) return;
        if (!_media.Connected)
        {
            // The empty-state viewport starts at y=56, so these body coordinates mirror the
            // absolute (158,62) icon and (100,118) copy in DetailsChromeLayout terms.
            Ellipse(operations,158,6,44,44,0x27303A45);
            Icon(operations,"\uE8D6",168,16,24,_secondary);
            Text(operations,"等待音乐",100,62,160,24,TextRole.Body,InkRole.Secondary,center:true);
        }
        // Plugin content starts right below the title/artist block, inside the scrollable body.
        float pluginTop = geometry is not null
            ? Math.Max(geometry.Title.Bottom, geometry.Artist.Bottom) - geometry.BodyViewport.Y
            : (_media.Connected ? 296 : 124);
        DrawPluginContributions(operations, pluginTop, LayoutSize(WidgetForm.Details).X);
    }

    void UpdateMetadata(WidgetForm form, bool trackChanged)
    {
        var layer = _metadata[form];
        bool music = MusicFor(form) && !(form == WidgetForm.Details && (WidgetsPage || HomePage));
        layer.Visual.Opacity = music ? 1 : 0;
        if (_preferences.ReducedMotion) layer.Finish();
        if (!music) return;
        var details = form == WidgetForm.Details ? ResponsiveDetails() : null;
        float width = form == WidgetForm.Capsule ? LayoutSize(form).X - 60 - (_preferences.AudioVisualizationEnabled ? 52 : 12)
            : form == WidgetForm.Peek ? (_preferences.AudioVisualizationEnabled ? 170 : 208)
            : details?.Title.Width ?? 312;
        float offsetX = form == WidgetForm.Capsule ? 60 : form == WidgetForm.Peek ? 88 : details?.Title.X ?? 24;
        float offsetY = form == WidgetForm.Capsule ? 9 : form == WidgetForm.Peek ? 64
            : details is not null ? details.Title.Y - details.BodyViewport.Y : 166;
        float artistOffset = form == WidgetForm.Capsule ? 20 : form == WidgetForm.Peek ? 30
            : details is not null ? details.Artist.Y - details.Title.Y : 28;
        var key = (Title, Artist, width, _text, _secondary, CurrentTypography, offsetX, offsetY, artistOffset);
        if (!_drawingInvalidated && _metadataKeys.TryGetValue(form, out var previous) && previous == key) return;
        var operations = new List<SurfaceOp>();
        float height;
        if (form == WidgetForm.Capsule)
        {
            height = 36; layer.Visual.Offset = new(offsetX, offsetY, 0);
            Text(operations, Title, 0, 0, width, 20, TextRole.MediaCapsuleTitle, InkRole.Primary);
            Text(operations, Artist, 0, artistOffset, width, 16, TextRole.MediaCapsuleArtist, InkRole.Secondary);
        }
        else if (form == WidgetForm.Peek)
        {
            height = 50; layer.Visual.Offset = new(offsetX, offsetY, 0);
            Text(operations, Title, 0, 0, width, 26, TextRole.MediaPeekTitle, InkRole.Primary);
            Text(operations, Artist, 0, artistOffset, width, 20, TextRole.MediaPeekArtist, InkRole.Secondary);
        }
        else if (details is not null)
        {
            height = Math.Max(details.Title.Height, artistOffset + details.Artist.Height);
            layer.Visual.Offset = new(offsetX, offsetY, 0);
            Text(operations, Title, 0, 0, width, 24, TextRole.MediaDetailsTitle, InkRole.Primary, center: true);
            Text(operations, Artist, 0, artistOffset, width, 20, TextRole.MediaDetailsArtist, InkRole.Secondary, center: true);
        }
        else
        {
            height = 54; layer.Visual.Offset = new(offsetX, offsetY, 0);
            Text(operations, Title, 0, 0, width, 24, TextRole.MediaDetailsTitle, InkRole.Primary, center: true);
            Text(operations, Artist, 0, artistOffset, width, 20, TextRole.MediaDetailsArtist, InkRole.Secondary, center: true);
        }
        layer.Show(RenderTextLayer($"{form}Metadata", width, height, operations), new(width, height), trackChanged && !_drawingInvalidated && !_preferences.ReducedMotion && _uiSettings.AnimationsEnabled, _activeTrackDirection);
        _metadataOperations[form] = operations.ToArray();
        _metadataKeys[form] = key;
    }

    void DrawPluginContributions(List<SurfaceOp> operations, float top, float shellWidth)
    {
        // The host grants a bounded 96 DIP area per contribution, within the scrollable body.
        // Plugin visuals never cover transport controls or acquire pointer regions.
        float width = shellWidth - 48;
        foreach (var contribution in _pluginVisuals.Take(4))
        {
            foreach (var operation in contribution.Value.Take(128))
            {
                if (!float.IsFinite(operation.X) || !float.IsFinite(operation.Y) || !float.IsFinite(operation.Width) || !float.IsFinite(operation.Height)) continue;
                float x = Math.Clamp(operation.X, 0, width), y = Math.Clamp(operation.Y, 0, 96);
                float w = Math.Clamp(operation.Width, 0, width - x), h = Math.Clamp(operation.Height, 0, 96 - y);
                if (w <= 0 || h <= 0) continue;
                var kind = operation.Kind switch
                {
                    PluginDrawKind.Text => SurfaceOpKind.Text, PluginDrawKind.Line => SurfaceOpKind.Line,
                    PluginDrawKind.Ellipse => SurfaceOpKind.Ellipse, PluginDrawKind.RoundedRectangle => SurfaceOpKind.RoundedRectangle,
                    _ => SurfaceOpKind.Rectangle
                };
                operations.Add(new SurfaceOp { Kind = kind, X = x + 24, Y = y + top, Width = w, Height = h,
                    Color = operation.Color, Text = operation.Text, FontSize = float.IsFinite(operation.FontSize) ? Math.Clamp(operation.FontSize, 8, 28) : 14,
                    Radius = float.IsFinite(operation.Radius) ? Math.Clamp(operation.Radius, 0, Math.Min(w, h) / 2) : 0 });
            }
            top += 112;
        }
    }

    void BuildControls()
    {
        if(_liquidGlassTransportSubmissionPending)return;
        var controls = new List<AccessibleControl>
        {
            new(WidgetActions.Body, _form == WidgetForm.Details ? (HomePage ? "首页" : WidgetsPage ? "小组件面板" : _media.Connected ? $"{Title}，{Artist}" : "音乐连接说明") : "展开音乐组件", 0, 0, _presentedSize.X, _presentedSize.Y)
        };
        var candidates = new Dictionary<int, (float Weight, WidgetForm Form, AccessibleControl Control)>();
        void Consider(AccessibleControl control, WidgetForm form, float weight)
        {
            if((control.Id is WidgetActions.Previous or WidgetActions.Toggle or WidgetActions.Next)
                &&!TransportLiquidGlassHandoffFrame.CanExposeTransportControls(form,_host.IsLiquidGlassCapture,
                    OwnedGlassEnabled,_liquidGlassTransportSubmissionPending,_submittedLiquidGlassTransportFrame))return;
            bool transport = control.Id is WidgetActions.Previous or WidgetActions.Toggle or WidgetActions.Next;
            bool seek = control.Id == WidgetActions.Seek;
            int index = form == WidgetForm.Peek ? 1 : 2;
            if (transport) weight = Math.Min(weight, (float)(form == WidgetForm.Peek
                ? _peekTransportOpacity : _detailsTransportOpacity).Sample(_motionNow).Position);
            if (weight <= .08f) return;
            float scale = transport || seek ? 1 : (float)_layerScale[index].Sample(_motionNow).Position;
            float shift = seek ? 0 : (float)_layerShift[index].Sample(_motionNow).Position;
            float x = (control.X - LayoutSize(form).X / 2) * scale + _presentedSize.X / 2;
            float y = control.Y * scale + shift;
            float left = Math.Max(0, x), right = Math.Min(_presentedSize.X, x + control.Width * scale);
            float top = Math.Max(0, y), bottom = Math.Min(_presentedSize.Y, y + control.Height * scale);
            if (right <= left || bottom <= top) return;
            control = control with { X = left, Y = top, Width = right - left, Height = bottom - top };
            if (!candidates.TryGetValue(control.Id, out var prior) || weight > prior.Weight)
                candidates[control.Id] = (weight, form, control);
        }
        foreach (var form in new[] { WidgetForm.Peek, WidgetForm.Details })
        {
            float weight = form == WidgetForm.Peek ? _contentWeights.Y : _contentWeights.Z;
            if (weight <= .08f) continue;
            void Add(AccessibleControl control) => Consider(control, form, weight);
            if (form == WidgetForm.Peek)
            {
                if (_media.Connected)
                {
                    Add(new(WidgetActions.Previous, "上一首", 86, PeekTransportY, 44, 44, _media.Capabilities.Previous));
                    Add(new(WidgetActions.Toggle, _media.State == PlaybackState.Playing ? "暂停" : "播放", 138, PeekTransportY, 44, 44, _media.Capabilities.Toggle));
                    Add(new(WidgetActions.Next, "下一首", 190, PeekTransportY, 44, 44, _media.Capabilities.Next));
                }
                else Add(new(WidgetActions.OpenMusic, "打开网易云音乐", 54, 108, 132, 36));
                continue;
            }
            var size = LayoutSize(form);
            if (HomePage) continue;
            if (WidgetsPage) continue;
            if(_pluginPages.Count>0)Add(new(WidgetActions.PluginTools,"插件工具",16,size.Y-104,32,32));
            if (_media.Connected)
            {
                var layout = form == WidgetForm.Details ? ResponsiveDetails() : null;
                var row = DetailsMediaControls(layout);
                Add(new(WidgetActions.Previous, "上一首", row.Previous.X, row.Previous.Y,
                    row.Previous.Width, row.Previous.Height, _media.Capabilities.Previous));
                Add(new(WidgetActions.Toggle, _media.State == PlaybackState.Playing ? "暂停" : "播放",
                    row.Toggle.X, row.Toggle.Y, row.Toggle.Width, row.Toggle.Height, _media.Capabilities.Toggle));
                Add(new(WidgetActions.Next, "下一首", row.Next.X, row.Next.Y,
                    row.Next.Width, row.Next.Height, _media.Capabilities.Next));
                bool valid = _media.Timeline?.Valid == true;
                var time = _media.Timeline;
                double value = valid ? Math.Clamp(((_seekPreview ?? PositionAt(_media, Stopwatch.GetTimestamp())) - time!.Start).TotalSeconds / (time.End - time.Start).TotalSeconds * 100, 0, 100) : 0;
                if (valid)
                {
                    float seekX = layout?.Timeline.X ?? 56, seekY = layout is null
                        ? 300
                        : layout.Timeline.Y + (layout.Timeline.Height - 28) / 2;
                    Add(new(WidgetActions.Seek, "播放进度", seekX, seekY, 248, 28,
                        _form == WidgetForm.Details && SeekAvailable(_media), IsRange: true, Value: value));
                }
            }
            else Add(new(WidgetActions.OpenMusic, "打开网易云音乐", 100, 162, 160, 36));
        }
        _controlForms.Clear();
        foreach (var candidate in candidates.OrderBy(pair => pair.Key))
        {
            controls.Add(candidate.Value.Control);
            _controlForms[candidate.Key] = candidate.Value.Form;
        }
        if(_form==WidgetForm.Details)
        {
            if (HomePage && _detailsBodyOpacity.Sample(_motionNow).Position > .08) AddHomeControls(controls, _media);
            else if(WidgetsPage && _boardRender is not null)
                foreach(var item in _boardRender.Controls)
                {
                    float shift=(_presentedSize.X-_detailsWidth)/2;
                    controls.Add(item with {X=item.X+shift});_controlForms[item.Id]=WidgetForm.Details;
                }
            if (_contentWeights.Z > .08f) {
                if (!HomePage) controls.Add(new AccessibleControl(WidgetActions.HomeBack, "返回首页", 12, 6, 44, 44));
                controls.AddRange(_chrome.GetAccessibleControls());
            }
        }
        string rootName = _form == WidgetForm.Details && HomePage ? "GlassDesk，首页" : _form == WidgetForm.Details && WidgetsPage ? "GlassDesk，小组件" : MusicFor(_form) ? $"GlassDesk，{Title}，{Artist}，{PlaybackLabel}" : $"GlassDesk，本地时间 {_now:HH:mm}";
        if (_lastRootName != rootName || !_controls.SequenceEqual(controls))
        {
            _controls = controls;
            _lastRootName = rootName;
            _host.SetAccessibleControls(controls, rootName);
        }
    }

    string Title => string.IsNullOrWhiteSpace(_media.Title) ? "歌曲信息暂不可用" : _media.Title;
    // Fixed transport row for the 476 DIP music page (previous/next 44, toggle 56).
    float DetailsTransportY => 340;
    MediaControlsRow DetailsMediaControls(DetailsResponsiveResult? geometry)
    {
        var size = LayoutSize(WidgetForm.Details);
        float centerX = geometry is null ? size.X / 2
            : geometry.ToggleButton.X + geometry.ToggleButton.Width / 2;
        float centerY = geometry is null ? DetailsTransportY + 28
            : geometry.ToggleButton.Y + geometry.ToggleButton.Height / 2;
        return MediaControlsRowLayout.Resolve(centerX, centerY, 44, 26);
    }
    string ClockPrimary => _preferences.ClockShowTime ? _now.ToString("HH:mm", CultureInfo.InvariantCulture)
        : _preferences.ClockShowDate ? _now.ToString("M月d日", CultureInfo.CurrentCulture)
        : _preferences.ClockShowWeekday ? _now.ToString("dddd", CultureInfo.CurrentCulture) : string.Empty;
    string ClockSecondary => string.Join("  ", new[]
    {
        _preferences.ClockShowTime && _preferences.ClockShowDate ? _now.ToString("M月d日", CultureInfo.CurrentCulture) : null,
        (_preferences.ClockShowTime || _preferences.ClockShowDate) && _preferences.ClockShowWeekday ? _now.ToString("dddd", CultureInfo.CurrentCulture) : null
    }.Where(value => value is not null));
    string Artist => string.IsNullOrWhiteSpace(_media.Artist) ? "歌手信息暂不可用" : _media.Artist;
    string PlaybackLabel => _media.State switch
    {
        PlaybackState.Playing => "播放中",
        PlaybackState.Paused => "已暂停",
        PlaybackState.Changing => "切换中",
        PlaybackState.Stopped or PlaybackState.Closed => "已停止",
        _ => "已连接"
    };

    void Cover(List<SurfaceOp> operations, float x, float y, float size)
    {
        Rect(operations, x, y, size, size, 0xFFE6EAED);
        if (_media.Cover is { Length: > 0 })
        {
            operations.Add(new() { Kind = SurfaceOpKind.Image, X = x, Y = y, Width = size, Height = size, Image = _media.Cover });
            return;
        }
        // Original record motif, used only when the source supplied no artwork.
        Ellipse(operations, x + size * .16f, y + size * .16f, size * .68f, size * .68f, 0xFF30373D);
        Ellipse(operations, x + size * .34f, y + size * .34f, size * .32f, size * .32f, 0xFF70918C);
        Ellipse(operations, x + size * .465f, y + size * .465f, size * .07f, size * .07f, 0xFFF3F5F5);
    }

    void Transport(float x, float y, float size, int action, bool enabled)
    {
        bool toggle = action == WidgetActions.Toggle;
        uint color = enabled ? _text : _disabled;
        var operations = new List<SurfaceOp>();

        float iconSize = toggle ? (size >= 56 ? 24 : 20) : 20;
        // Material, hover feedback and glyph share the same press scale.
        operations = operations.Select(op => op with { X = op.X - x, Y = op.Y - y }).ToList();
        var key = (_form, action);
        if (!_buttons.TryGetValue(key, out var button))
        {
            button = new TransportVisual(_host, toggle);
            _buttons.Add(key, button);
            (_form == WidgetForm.Peek ? _peekButtons : _detailsButtons).Children.InsertAtTop(button.Visual);
        }
        button.Visual.Offset = new(x, y, 0);
        button.SetSize(size);
        button.SetAppearance(_accessibility.HighContrast ? _button : toggle ? 0xD0FFFFFFu : 0xB0FFFFFFu, color, _accessibility.HighContrast);
        button.Replace(_host.RenderSurface(size, size, operations));
        if (toggle)
        {
            button.UpdatePlaybackGlyph(_media.State == PlaybackState.Playing, color, size, iconSize, _preferences.ReducedMotion);
            return;
        }
        button.UpdateSkipGlyph(action == WidgetActions.Next, color, size, iconSize);
    }

    void ClockIcon(List<SurfaceOp> operations, float x, float y, float size)
        => Icon(operations, "\uE917", x, y, size, _secondary);

    uint PlayInk => _accessibility.HighContrast ? Argb(_uiSettings.UIElementColor(UIElementType.HighlightText)) : 0xFFF8FAFB;

    void SetPalette()
    {
        _chrome.SetMaterialTint(_preferences.TintOpacity);
        if (!_accessibility.HighContrast)
        {
            _text = TypographyCatalog.Ink(InkRole.Primary, _lightText);
            _secondary = TypographyCatalog.Ink(InkRole.Secondary, _lightText);
            _disabled = TypographyCatalog.Ink(InkRole.Disabled, _lightText);
            _accent = 0xFF5D6F84; _button = 0x81FFFFFF; _track = 0x29303A45;
            foreach (var depth in _waveDepth)depth.SetAppearance(0xFF596775,_text);
            return;
        }
        _text = Argb(_uiSettings.UIElementColor(UIElementType.WindowText));
        _secondary = _disabled = _text;
        _accent = Argb(_uiSettings.UIElementColor(UIElementType.Highlight));
        _button = Argb(_uiSettings.UIElementColor(UIElementType.Window));
        _track = _text;
        foreach (var depth in _waveDepth)depth.SetAppearance(_text,_button,true);
    }

    // Only disconnected Peek uses this plate; connected Peek text sits on the shell.
    void ClockWhiteplate(List<SurfaceOp> operations, float x, float y, float width, float height, float radius)
    {
        if (_accessibility.HighContrast || OwnedGlassEnabled) return;
        float alpha = Math.Clamp((.36f-_preferences.TintOpacity)/(1-_preferences.TintOpacity),0,.22f);
        if (alpha <= .01f) return;
        RoundRect(operations, x, y, width, height, radius, (uint)Math.Round(alpha * 255) << 24 | 0x00FFFFFF);
    }

    static bool SeekAvailable(MediaSnapshot media) => media.Capabilities.Seek &&
        media.Timeline is { Valid: true } time && time.MaxSeek > time.MinSeek && time.MaxSeek >= time.Start && time.MinSeek <= time.End;

    static uint Argb(Color color) => (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
    static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    void Text(List<SurfaceOp> ops, string value, float x, float y, float width, float height,
        TextRole role, InkRole ink, bool center = false, TextFlags? flags = null, bool clockPlate = false)
    {
        // Caption ink is promoted on the shared translucent shell until a trusted
        // composed background proves its lower emphasis still meets 4.5:1.
        if (ink == InkRole.Caption) ink = InkRole.Secondary;
        bool promote = clockPlate ? _clockPlatePromoteSecondary : _promoteSecondary;
        if (OwnedGlassEnabled && !_host.IsLiquidGlassCapture && role is TextRole.MediaCapsuleArtist or TextRole.MediaPeekArtist or TextRole.MediaDetailsArtist or TextRole.ClockLegacyAux) promote = true;
        if (promote && ink == InkRole.Secondary) ink = InkRole.Primary;
        uint color = _accessibility.HighContrast ? _text : TypographyCatalog.Ink(ink, clockPlate ? _clockPlateLightText : _lightText);
        var operation=SurfaceOp.Typography(value, role, x, y, width, height, color, center, flags, CurrentTypography.Locale);
        if (_preferences.ClockShowTime && ClockFontCatalog.IsClockRole(role))
            operation=operation with { TextFlags=operation.TextFlags | ClockFontCatalog.Get(_preferences.ClockFontId).Flag };
        ops.Add(operation);
    }

    CompositionDrawingSurface RenderTextLayer(string layer, float width, float height, IReadOnlyList<SurfaceOp> operations)
    {
        var result = _host.RenderSurface(width, height, operations);
        if (CaptureTypographyDiagnostics)
            _typographyLayers[layer] = operations.Take(256).ToArray();
        return result;
    }

    // Called only by explicit QA observation; no extra measurement or timer in ordinary runs.
    public object GetTypographyDiagnostics() => new
    {
        Profile = _host.TypographyProfile.ToString(), Revision = _host.TypographyRevision,
        Locale = CurrentTypography.Locale, Dpi = _host.Context.Dpi, Form = _form.ToString(),
        InkState = new {BodyLight=_lightText,BodyPromoted=_promoteSecondary,ClockPlateLight=_clockPlateLightText,ClockPlatePromoted=_clockPlatePromoteSecondary,
            LiquidGlassInkSelected=_host.LiquidGlassInkAvailable&&_host.IsLiquidGlassCapture&&OwnedGlassEnabled,
            Note="Layer colors are requested ink values. Adaptive ink is quarantined; composed contrast is not measured."},
        MusicCapsuleWidth = _musicCapsuleWidth, Cache = _host.TextCacheStats,
        Layers = _typographyLayers.Select(entry => new
        {
            Layer = entry.Key,
            Text = entry.Value.Where(op => op.Kind is SurfaceOpKind.Text or SurfaceOpKind.MultilineText).Select(op => new
            {
                Role = op.TextRole.ToString(), op.Text, op.X, op.Y, op.Width, op.Height, op.FontSize,
                op.FontWeight, op.TrackingEm, op.LineHeightDip, op.BaselineDip,
                Flags = op.TextFlags.ToString(), op.LocaleName, Ink = $"#{op.Color:X8}",
                Metrics = _host.MeasureText(op)
            }).ToArray(),
            Shapes = entry.Value.Where(op => op.Kind is not (SurfaceOpKind.Text or SurfaceOpKind.MultilineText)).Select(op => new
            {Kind=op.Kind.ToString(),op.X,op.Y,op.Width,op.Height,op.Radius,Color=$"#{op.Color:X8}"}).ToArray()
        }).ToArray(),
        ProtectedGeometry = new
        {
            Spectrum = _bands.Select(band => new {Width=band.Size.X,Height=band.Size.Y,X=band.Offset.X,Y=band.Offset.Y,ScaleY=band.Scale.Y}).ToArray(),
            NavigationBounds = _chrome.HomeBounds, NavigationExpandedBounds = _chrome.DockBounds
        }
    };
    static void Icon(List<SurfaceOp> ops, string glyph, float x, float y, float size, uint color)
        => ops.Add(new() { Kind = SurfaceOpKind.Icon, Text = glyph, X = x, Y = y, Width = size, Height = size, FontSize = size, Color = color, Center = true });
    static void Rect(List<SurfaceOp> ops, float x, float y, float width, float height, uint color)
        => ops.Add(new() { Kind = SurfaceOpKind.Rectangle, X = x, Y = y, Width = width, Height = height, Color = color });
    static void RoundRect(List<SurfaceOp> ops, float x, float y, float width, float height, float radius, uint color)
        => ops.Add(new() { Kind = SurfaceOpKind.RoundedRectangle, X = x, Y = y, Width = width, Height = height, Radius = radius, Color = color });
    static void Ellipse(List<SurfaceOp> ops, float x, float y, float width, float height, uint color)
        => ops.Add(new() { Kind = SurfaceOpKind.Ellipse, X = x, Y = y, Width = width, Height = height, Color = color });

    void OnWindowContextChanged(HostContext context)
    {
        if (_disposed || _lastContent is null) return;
        if (_lastContentDpi == context.Dpi) return;
        _lastContentDpi = context.Dpi;
        InvalidateDrawingCaches();
        Render(_form, _media, _now, _preferences, _notice, _capsuleMode);
    }
    uint _lastContentDpi;

    void OnGraphicsDeviceRestored()
    {
        if (_disposed) return;
        InvalidateDrawingCaches();
        Render(_form, _media, _now, _preferences, _notice, _capsuleMode);
    }

    void InvalidateDrawingCaches()
    {
        _ownedClockKey=null;
        InvalidateReadingSupport();
        _drawingInvalidated = true;
        _lastTimeline = null;
        _lastRootName = null;
        _homeVisual?.Invalidate();
        _feedbackDirty = true;
        _artworkDirty = true;
        _chrome.Invalidate();
    }

    static void ReplaceSurface(ref CompositionDrawingSurface? owned, CompositionSurfaceBrush brush, CompositionDrawingSurface replacement)
    {
        var old = owned;
        try { brush.Surface = replacement; }
        catch
        {
            try { replacement.Dispose(); }
            catch { /* Preserve the original surface binding failure. */ }
            throw;
        }
        owned = replacement;
        old?.Dispose();
    }

    void EnsureUiThread()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId) throw new InvalidOperationException("Widget composition must run on its owning UI thread.");
    }

    public void Dispose()
    {
        EnsureUiThread();
        if (_disposed) return;
        _disposed = true;
        DisposeDesktopMaterialVisuals();
        DisposeBoardVisuals();
        _homeVisual?.Dispose();
        _artworkTimer.Stop();
        _artworkTimer.Tick -= OnArtworkTransitionCompleted;
        RenderingFailed = null;
        _host.WindowContextChanged -= OnWindowContextChanged;
        _host.GraphicsDeviceRestored -= OnGraphicsDeviceRestored;
        _host.ContentRoot.Children.Remove(_root);
        _contentBrush.Surface = _capsuleBrush.Surface = _peekBrush.Surface = _artworkBrush.Surface = _previousArtworkBrush.Surface = _timelineBrush.Surface = _feedbackBrush.Surface = _detailsBrush.Surface = null;
        _root.Children.RemoveAll();
        _bodyRoot.Children.RemoveAll();
        _capsuleContent.Children.RemoveAll(); _peekContent.Children.RemoveAll(); _detailsBody.Children.RemoveAll();
        foreach (var metadata in _metadata.Values) metadata.Dispose();
        _metadata.Clear();
        _peekButtons.Children.RemoveAll(); _detailsButtons.Children.RemoveAll();
        foreach (var button in _buttons.Values) button.Dispose();
        _buttons.Clear();
        _peekButtons.Dispose(); _detailsButtons.Dispose();
        _detailsViewport.Children.RemoveAll();
        _contentSurface?.Dispose(); _capsuleSurface?.Dispose(); _peekSurface?.Dispose(); _artworkSurface?.Dispose(); _previousArtworkSurface?.Dispose();
        _timelineSurface?.Dispose(); _feedbackSurface?.Dispose(); _detailsSurface?.Dispose();
        _timeline.Children.RemoveAll();_timelineTrack.Dispose();_timelineThumb.Dispose();
        _content.Dispose(); _capsuleContent.Dispose(); _peekContent.Dispose(); _timeline.Dispose(); _feedback.Dispose();
        _artwork.Clip?.Dispose(); _previousArtwork.Clip?.Dispose(); _artwork.Dispose(); _previousArtwork.Dispose();
        _artworkMotion.Children.RemoveAll(); _previousArtworkMotion.Children.RemoveAll();
        _artworkMotion.Dispose(); _previousArtworkMotion.Dispose();
        _artworkClip.Dispose(); _previousArtworkClip.Dispose();
        _artworkViewport.Children.RemoveAll(); _artworkViewportClip.Dispose(); _artworkViewport.Dispose();
        _detailsBody.Dispose(); _detailsViewport.Clip?.Dispose(); _detailsViewport.Dispose();
        _audioBars.Children.RemoveAll();
        foreach(var depth in _waveDepth)depth.Dispose();
        foreach (var band in _bands) { band.Children.RemoveAll();band.Brush?.Dispose(); band.Dispose(); }
        _audioClip.Dispose(); _audioBars.Dispose(); _companion.Dispose(); _peekCompanion.Dispose(); _chrome.Dispose();
        _contentBrush.Dispose(); _capsuleBrush.Dispose(); _peekBrush.Dispose(); _artworkBrush.Dispose(); _previousArtworkBrush.Dispose();
        _timelineBrush.Dispose(); _feedbackBrush.Dispose(); _detailsBrush.Dispose();
        _bodyRoot.Clip?.Dispose(); _bodyRoot.Dispose(); _root.Dispose();
    }

    sealed record ContentKey(WidgetForm Form, string Session, long Version, bool Connected, string Title, string Artist,
        string Album, byte[]? Cover, PlaybackState State, MediaCapabilities Capabilities, bool TimelineValid,
        string? Notice, string Clock, uint Text, uint Secondary, uint Accent, float Tint, bool ReducedMotion,
        CapsuleContentMode CapsuleMode, bool Companion, bool AudioEnabled, TypographyIdentity Typography);
    readonly record struct TimelineKey(bool Valid, bool Seek, int Pixel, string Position, string Duration, bool Preview, uint Text, uint Accent, TypographyIdentity Typography);
    readonly record struct TypographyIdentity(FontProfile Profile, int Revision, string Locale, uint Dpi);

    sealed class TransportVisual : IDisposable
    {
        public SpriteVisual Visual { get; }
        public bool MotionActive => _spring.Active || _plate.MotionActive;
        readonly Compositor _compositor;
        readonly CompositionSpring _spring;
        readonly CompositionSurfaceBrush _brush;
        readonly ControlDepthVisual _plate;
        readonly SkipGlyphVisual? _skipGlyph;
        readonly PlaybackGlyphVisual? _playbackGlyph;
        readonly ShapeVisual _hover;
        readonly CompositionEllipseGeometry _hoverGeometry;
        readonly CompositionSpriteShape _hoverShape;
        readonly CompositionColorBrush _hoverInk;
        CompositionDrawingSurface? _surface;
        bool _pressed, _hovered;
        bool _ownedGlass;
        readonly bool _systemGlass;
        byte _shade;
        double _releasedAt;
        static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
        public TransportVisual(NativeWidgetHost host, bool toggle)
        {
            _compositor = host.Compositor; _spring = new(_compositor); _systemGlass=host.IsLiquidGlassCapture;
            Visual = _compositor.CreateSpriteVisual();
            _brush = _compositor.CreateSurfaceBrush();
            _brush.Stretch = CompositionStretch.Fill;
            Visual.Brush = _brush;
            _plate=new ControlDepthVisual(_compositor,ControlDepthKind.Plate);Visual.Children.InsertAtBottom(_plate.Visual);
            _hover = _compositor.CreateShapeVisual(); _hoverGeometry = _compositor.CreateEllipseGeometry();
            _hoverInk = _compositor.CreateColorBrush(Color.FromArgb(0, 48, 58, 69));
            _hoverShape = _compositor.CreateSpriteShape(_hoverGeometry); _hoverShape.FillBrush = _hoverInk;
            _hover.Shapes.Add(_hoverShape); Visual.Children.InsertAtTop(_hover);
            if (toggle)
            {
                _playbackGlyph = new PlaybackGlyphVisual(host); Visual.Children.InsertAtTop(_playbackGlyph.Visual);
            }
            else
            {
                _skipGlyph = new SkipGlyphVisual(host); Visual.Children.InsertAtTop(_skipGlyph.Visual);
            }
        }
        public void SetSize(float size)
        {
            Visual.Size = _hover.Size = new(size, size); Visual.CenterPoint = new(size / 2, size / 2, 0);
            _hoverGeometry.Center = _hoverGeometry.Radius = new(size / 2);
            if(_ownedGlass&&_systemGlass)_plate.SetBounds(1,1,size-2,size-2,(size-2)/2);else _plate.SetBounds(0,0,size,size,size/2);
        }
        public void SetAppearance(uint fill,uint ink,bool highContrast)=>_plate.SetAppearance(fill,ink,highContrast);
        public void SetOwnedGlass(bool value)
        {
            if(_ownedGlass==value)return;_ownedGlass=value;
            // Native same-profile light now owns both the face and contact edge.
            // Keep the glyph outside it; a second C# keycap would double the rim.
            _plate.SetOpticalGlass(value&&_systemGlass);_plate.Visual.IsVisible=!value;_hover.IsVisible=!value;
            _skipGlyph?.SetOpticalGlass(value&&_systemGlass);_playbackGlyph?.SetOpticalGlass(value&&_systemGlass);
            if(value&&_systemGlass){float size=Visual.Size.X;_plate.SetBounds(1,1,size-2,size-2,(size-2)/2);}
            _spring.Dispose(); Visual.StopAnimation("Scale");Visual.Scale=Vector3.One;
        }
        public void Replace(CompositionDrawingSurface replacement) => ReplaceSurface(ref _surface, _brush, replacement);
        public void UpdateSkipGlyph(bool next, uint color, float buttonSize, float iconSize)
            => _skipGlyph!.Update(next, color, buttonSize, iconSize);
        public void UpdatePlaybackGlyph(bool playing, uint color, float buttonSize, float iconSize, bool reducedMotion)
            => _playbackGlyph!.Update(playing, color, buttonSize, iconSize, reducedMotion);
        public void SetFeedback(bool hovered, bool pressed, bool reducedMotion)
        {
            if(_ownedGlass&&!_systemGlass){_hovered=hovered;_pressed=pressed;return;}
            _hovered = hovered;_plate.SetFeedback(hovered,pressed,reducedMotion);
            byte shade = pressed ? (byte)0x26 : hovered ? (byte)0x10 : (byte)0;
            if (reducedMotion)
            {
                Visual.StopAnimation("Scale"); Visual.Scale = Vector3.One;
                _hoverInk.StopAnimation("Color"); _hoverInk.Color = Color.FromArgb(shade, 48, 58, 69);
            }
            else if (shade != _shade)
            {
                using var tint = _compositor.CreateColorKeyFrameAnimation();
                tint.InsertExpressionKeyFrame(0, "this.StartingValue");
                tint.InsertKeyFrame(1, Color.FromArgb(shade, 48, 58, 69)); tint.Duration = TimeSpan.FromMilliseconds(100);
                _hoverInk.StartAnimation("Color", tint);
            }
            _shade = shade;
            if (_pressed && !pressed) _releasedAt = Now;
            _pressed = pressed;
            float target = reducedMotion ? 1 : pressed ? .94f : hovered ? 1.018f : 1;
            _spring.Scale(Visual, new(target, target, 1), reducedMotion, pressed ? 140 : 280);
        }
        public void Activate(bool reducedMotion)
        {
            if(_ownedGlass&&!_systemGlass)return;
            if (reducedMotion || _pressed || Now - _releasedAt < 120) return;
            float target = _hovered ? 1.018f : 1;
            _spring.PulseScale(Visual, new(target, target, 1), reducedMotion);
            using var tint = _compositor.CreateColorKeyFrameAnimation();
            tint.InsertExpressionKeyFrame(0, "this.StartingValue");
            tint.InsertKeyFrame(.3f, Color.FromArgb(0x26, 48, 58, 69));
            tint.InsertKeyFrame(1, Color.FromArgb(_hovered ? (byte)0x10 : (byte)0, 48, 58, 69));
            tint.Duration = TimeSpan.FromMilliseconds(260); _hoverInk.StartAnimation("Color", tint);
        }
        public void Dispose()
        {
            _spring.Dispose();
            _brush.Surface = null;
            _surface?.Dispose();
            Visual.StopAnimation("Scale"); _hoverInk.StopAnimation("Color");
            Visual.Children.RemoveAll(); _plate.Dispose(); _skipGlyph?.Dispose(); _playbackGlyph?.Dispose();
            _hover.Shapes.Clear(); _hoverShape.Dispose(); _hoverGeometry.Dispose(); _hoverInk.Dispose(); _hover.Dispose();
            Visual.Dispose();
            _brush.Dispose();
        }
    }
}
