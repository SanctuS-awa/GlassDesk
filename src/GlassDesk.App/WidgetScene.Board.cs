using System.Globalization;
using System.Numerics;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Windows.UI.Composition;

namespace GlassDesk.App;

public sealed partial class WidgetScene
{
    string _detailsPageId = DetailsPageController.Home;
    float _detailsWidth = 360, _boardScroll;
    long _boardRevision = -1;
    WidgetBoardState _board = WidgetBoardData.CreateDefault();
    WidgetBoardState _savedBoard = WidgetBoardData.CreateDefault();
    int _boardPressedAction = -1;
    string? _boardDragTarget;
    bool _boardHiddenPanelOpen;
    string? _boardUndoHiddenLabel;
    float _boardHiddenScroll;
    IReadOnlyList<WidgetInstanceResult> _boardResults = [];
    bool _boardEditing, _boardAddMenu;
    SpriteVisual _boardCards = null!;
    CompositionSurfaceBrush _boardBrush = null!;
    CompositionDrawingSurface? _boardSurface;
    WidgetBoardRenderResult? _boardRender;
    public bool WidgetsPage => _detailsPageId == DetailsPageController.Widgets;
    public bool HomePage => _detailsPageId == DetailsPageController.Home;
    public float CurrentPageScroll => HomePage ? HomeGeometry.ScrollY : WidgetsPage ? _boardScroll : _detailsScroll;
    internal WidgetBoardRenderResult? BoardRender => _boardRender;
    internal WidgetBoardState SavedBoard => _savedBoard;
    internal bool BoardHiddenPanelOpen => _boardHiddenPanelOpen;
    // Pointer feedback belongs to the renderer input description; the host repaints once
    // after changing it and never polls in between.
    internal void SetBoardInteractionFeedback(int pressedAction, string? dragTargetInstanceId)
    {
        if (_boardPressedAction == pressedAction && string.Equals(_boardDragTarget, dragTargetInstanceId, StringComparison.Ordinal)) return;
        _boardPressedAction = pressedAction;
        _boardDragTarget = dragTargetInstanceId;
        _drawingInvalidated = true;
    }
    // Hidden-component management state: the sheet, its scroll offset and the last explicit
    // removal that can still be undone while the sheet stays open.
    internal void SetBoardHiddenState(bool panelOpen, string? undoHiddenLabel, float scrollOffset)
    {
        _boardHiddenPanelOpen = panelOpen;
        _boardUndoHiddenLabel = panelOpen ? undoHiddenLabel : null;
        _boardHiddenScroll = float.IsFinite(scrollOffset) ? Math.Max(0, scrollOffset) : 0;
        _drawingInvalidated = true;
    }
    public event Action<string>? DetailsPageRequested;
    public event Action? BoardViewportChanged;
    // Empty state needs 16 top + a 144 block + 8 bottom = 168 DIP viewport; adding the
    // toolbar and dock insets gives the minimum page height. Content may then grow to 512.
    internal const float MinimumBoardPageHeight = 344;
    // The widget page height follows its content: one card stays compact, more cards grow
    // up to 512 and then scroll. The dock inset is part of the formula so it never overlaps.
    public float BoardNaturalHeight => Math.Clamp(
        WidgetBoardLayout.Pack(_board, _detailsWidth).ContentBottom + WidgetBoardLayout.BottomInset,
        MinimumBoardPageHeight, 512);

    void InitializeBoardVisuals()
    {
        _boardCards = _host.Compositor.CreateSpriteVisual();
        _boardBrush = _host.Compositor.CreateSurfaceBrush();
        _boardBrush.Stretch = CompositionStretch.Fill;
        _boardCards.Brush = _boardBrush;
        _boardCards.Offset = new(0, WidgetBoardLayout.Top, 0);
        _boardCards.Opacity = 0;
        // The toolbar owns transient menus and stays above the card viewport.
        _bodyRoot.Children.InsertBelow(_boardCards, _content);
        _chrome.PageRequested += id => DetailsPageRequested?.Invoke(id);
    }

    internal void SetWidgetBoard(string pageId, WidgetBoardState board, IReadOnlyList<WidgetInstanceResult> results,
        long revision, bool editing, bool addMenuOpen, float scrollOffset)
    {
        bool changed = _detailsPageId != pageId || _boardRevision != revision || _boardEditing != editing || _boardAddMenu != addMenuOpen;
        string normalizedPage = DetailsPageController.Normalize(pageId);
        bool leavingHome = HomePage && normalizedPage != DetailsPageController.Home;
        _detailsPageId = normalizedPage;
        if (leavingHome) ResetHomeState();
        // Keep the complete saved board for capacity/hidden management; paint only the
        // current presentation subset.
        _savedBoard = WidgetBoardData.ValidateAndClone(board);
        _board = WidgetBoardData.TimeOnlyForPresentation(_savedBoard); _boardResults = results; _boardRevision = revision;
        _boardEditing = editing; _boardAddMenu = addMenuOpen;
        _boardScroll = float.IsFinite(scrollOffset) ? Math.Max(0,scrollOffset) : 0;
        if (HomePage)
            _homeScroll = Math.Min(_boardScroll, HomeGeometry.MaxScrollY);
        if(!WidgetsPage)_detailsScroll=_boardScroll;
        _chrome.SetCurrentPage(_detailsPageId);
        if (changed) _drawingInvalidated = true;
    }

    void DrawBoard(List<SurfaceOp> operations)
    {
        var size = LayoutSize(WidgetForm.Details);
        bool firstBoardRender = _boardRender is null;
        _boardRender = WidgetBoardRenderer.Build(_board,_boardResults,size.X,size.Y,_boardScroll,
            _boardEditing,_boardAddMenu,_lightText,CultureInfo.CurrentUICulture.Name,_accessibility.HighContrast,
            _savedBoard.Instances.Count, WidgetBoardData.Counts(_savedBoard).Hidden,
            _boardPressedAction,_boardDragTarget,
            hiddenPanelOpen: _boardHiddenPanelOpen, undoHiddenLabel: _boardUndoHiddenLabel,
            hiddenInstances: WidgetBoardData.HiddenForPresentation(_savedBoard), hiddenPanelScroll: _boardHiddenScroll);
        _boardScroll = _boardRender.ScrollOffset;
        _boardCards.Size = new(size.X, Math.Max(1,size.Y-WidgetBoardLayout.BottomInset-WidgetBoardLayout.Top));
        ReplaceSurface(ref _boardSurface,_boardBrush,RenderTextLayer("WidgetBoard",_boardCards.Size.X,_boardCards.Size.Y,_boardRender.CardOperations));
        operations.AddRange(_boardRender.ToolbarOperations);
        // The first painted frame publishes its packing so the host can schedule providers.
        // Without it, a page opened before its first render can stay in "preparing" forever.
        if (firstBoardRender) BoardViewportChanged?.Invoke();
    }

    bool ScrollBoard(float delta)
    {
        if (_boardRender is null || _boardAddMenu) return false;
        float viewportHeight = Math.Max(0, CurrentSize.Y - WidgetBoardLayout.Top - WidgetBoardLayout.BottomInset);
        float maximum = Math.Max(0,_boardRender.ContentHeight-viewportHeight);
        float next = Math.Clamp(_boardScroll+delta,0,maximum);
        if (Math.Abs(next-_boardScroll)<.1f) return false;
        _boardScroll=next; _drawingInvalidated=true;
        Render(_form,_media,_now,_preferences,_notice,_capsuleMode);
        BoardViewportChanged?.Invoke();
        return true;
    }

    void ApplyBoardVisibility(Vector3 weights)
    {
        _boardCards.Offset = new((_presentedSize.X-_detailsWidth)/2,WidgetBoardLayout.Top,0);
        _boardCards.Opacity = WidgetsPage ? weights.Z : 0;
        if (!WidgetsPage) return;
        _detailsViewport.Opacity = _timeline.Opacity = _detailsButtons.Opacity = 0;
        _audioBars.Opacity = _preferences.AudioVisualizationEnabled
            ? Math.Clamp((MusicFor(WidgetForm.Capsule)?weights.X:0)+(_media.Connected?weights.Y:0),0,1) : 0;
        _artworkViewport.Opacity = _media.Connected
            ? Math.Clamp((MusicFor(WidgetForm.Capsule)&&!CompanionVisible?weights.X:0)+weights.Y,0,1) : 0;
    }

    void DisposeBoardVisuals()
    {
        DetailsPageRequested = null; BoardViewportChanged = null;
        _boardBrush.Surface=null; _boardSurface?.Dispose();
        _bodyRoot.Children.Remove(_boardCards); _boardCards.Dispose(); _boardBrush.Dispose();
    }
}
