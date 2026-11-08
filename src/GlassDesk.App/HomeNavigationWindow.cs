using System.Numerics;
using System.Runtime.InteropServices;
using GlassDesk.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;

namespace GlassDesk.App;

/// <summary>
/// A compact, owned WinUI Dock-like side navigation for the Home route.
/// A first user request activates the XAML window so its content is initialized;
/// subsequent displays reuse it without activation. Escape follows the XAML key route.
/// </summary>
internal sealed class HomeNavigationWindow : Window, IDisposable
{
    const double WidthDip = 60, HeightDip = 274, GapDip = 8;
    const double EntryWidthDip = 48;
    const double EntryHeightDip = 48;
    const int GwlpHwndParent = -8, GwlExStyle = -20;
    const long WsExToolWindow = 0x00000080L;
    const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004,
        SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    readonly nint _owner;
    readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    readonly StackPanel _entries = new()
    {
        Width = WidthDip,
        Spacing = 6,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    readonly Button[] _entryButtons = new Button[5];
    readonly Button _home;
    readonly Action _selectHome;
    readonly Action<HomeNavigationWindow> _dismiss;
    readonly Action<HomeNavigationWindow> _closed;
    bool _visible, _hasLoaded, _isClosed, _closing, _disposed;
    RectInt32? _lastBounds;
    float _lastScale = 1;

    public nint Handle { get; }
    public bool IsVisible => _visible;
    public FloatRect? ScreenBounds => _visible && _lastBounds is { } bounds
        ? new(bounds.X, bounds.Y, bounds.Width, bounds.Height) : null;
    public float ScreenCornerRadius => 18 * _lastScale;

    public HomeNavigationWindow(nint owner, Action selectHome, Action<HomeNavigationWindow> dismiss,
        Action<HomeNavigationWindow> closed)
    {
        _owner = owner;
        _selectHome = selectHome;
        _dismiss = dismiss;
        _closed = closed;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "GlassDesk 导航";
        AppWindow.IsShownInSwitchers = false;

        // Match the existing configuration-window ownership pattern. The side window
        // remains an ordinary top-level window so an explicit user click can focus it.
        SetWindowLongPtr(Handle, GwlpHwndParent, owner);
        SetWindowLongPtr(Handle, GwlExStyle, new nint(GetWindowLongPtr(Handle, GwlExStyle).ToInt64() | WsExToolWindow));
        SetWindowPos(Handle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(false, false);

        var destinations = new (string Name, string Glyph, bool Enabled)[]
        {
            ("首页", "\uE80F", true),
            ("本地音乐", "\uE8D6", false),
            ("歌单", "\uE8FD", false),
            ("收藏", "\uE734", false),
            ("最近播放", "\uE823", false)
        };
        for (int i = 0; i < destinations.Length; i++)
        {
            var (name, glyph, enabled) = destinations[i];
            var entry = CreateEntry(name, glyph, enabled);
            _entryButtons[i] = entry;
            _entries.Children.Add(entry);
        }
        _home = _entryButtons[0];
        AutomationProperties.SetName(_home, "首页");
        AutomationProperties.SetHelpText(_home, "返回首页并关闭导航侧栏。");
        _home.Click += OnHomeClick;
        _home.KeyDown += OnKeyDown;
        _root.Children.Add(_entries);
        AutomationProperties.SetName(_root, "Dock 式导航");
        AutomationProperties.SetHelpText(_root, "当前仅首页可用，其余项目暂不可用。");
        _root.KeyDown += OnKeyDown;
        _root.PointerPressed += OnRootPointerPressed;
        _root.Loaded += OnLoaded;
        Content = _root;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
    }

    public void ShowWithoutActivation()
    {
        if (_isClosed || _disposed) return;
        RepositionNearOwner();
        if (_visible) return;
        bool wasLoaded = _hasLoaded;
        _visible = true;
        try
        {
            // AppWindow.Show alone can make the HWND visible without activating the
            // XAML Window that owns its content. The initial open is a user click;
            // use the documented XAML activation path once, then reuse on later opens.
            if (!wasLoaded) Activate();
            else AppWindow.Show(false);
        }
        catch
        {
            _visible = false;
            throw;
        }
        if (wasLoaded) AnimateIn();
    }

    public void RepositionNearOwner()
    {
        if (_isClosed || !GetWindowRect(_owner, out var ownerRect)) return;
        var ownerId = Win32Interop.GetWindowIdFromWindow(_owner);
        var workArea = DisplayArea.GetFromWindowId(ownerId, DisplayAreaFallback.Nearest).WorkArea;
        uint dpi = GetDpiForWindow(_owner);
        double scale = (dpi == 0 ? 96u : dpi) / 96d;
        _lastScale = (float)scale;
        int width = Math.Min((int)Math.Round(WidthDip * scale), workArea.Width);
        int height = Math.Min((int)Math.Round(HeightDip * scale), workArea.Height);
        int gap = (int)Math.Round(GapDip * scale);
        int workRight = workArea.X + workArea.Width;
        int workBottom = workArea.Y + workArea.Height;
        // The navigation circle is 20 DIP from the right edge and 56 DIP wide;
        // its top is 72 DIP above the bottom of the Home panel.
        int centerX = ownerRect.Right - (int)Math.Round(48 * scale);
        int x = Math.Clamp(centerX - width / 2, workArea.X, workRight - width);
        int triggerTop = ownerRect.Bottom - (int)Math.Round(72 * scale);
        int y = Math.Clamp(triggerTop - gap - height, workArea.Y, workBottom - height);
        var target = new RectInt32(x, y, width, height);
        if (_lastBounds is { } previous && previous.X == target.X && previous.Y == target.Y
            && previous.Width == target.Width && previous.Height == target.Height) return;
        var prior = _lastBounds;
        _lastBounds = target;
        try
        {
            AppWindow.Resize(new(width, height));
            AppWindow.Move(new(x, y));
        }
        catch { _lastBounds = prior; throw; }
    }

    public new void Close()
    {
        if (!_isClosed && !_disposed) _dismiss(this);
    }

    public void Hide()
    {
        if (_isClosed || _disposed || !_visible) return;
        // The primary widget uses a native HWND; keep this XAML Window alive when dismissed.
        _visible = false;
        try { AppWindow.Hide(); }
        catch
        {
            _visible = true;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isClosed || _closing) return;
        _closing = true;
        try { base.Close(); }
        finally { if (!_isClosed) _closing = false; }
    }

    void OnLoaded(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= OnLoaded;
        _hasLoaded = true;
        AnimateIn();
    }

    void AnimateIn()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        visual.CenterPoint = new((float)_root.ActualWidth / 2, (float)_root.ActualHeight, 0);
        if (!new UISettings().AnimationsEnabled)
        {
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            return;
        }
        visual.Opacity = 0;
        visual.Scale = new(.9f, .9f, 1);
        using var easing = visual.Compositor.CreateCubicBezierEasingFunction(new(.2f, .8f), new(.2f, 1));
        using var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = TimeSpan.FromMilliseconds(210);
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, easing);
        using var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = TimeSpan.FromMilliseconds(300);
        scale.InsertKeyFrame(0, new(.9f, .9f, 1));
        scale.InsertKeyFrame(1, Vector3.One, easing);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
    }

    void OnRootPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        // Keep the non-activating open behavior. A click on the active tile may focus
        // it, while clicking a disabled tile or the gaps between entries stays inert.
        if (_isClosed) return;
        DependencyObject? source = args.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (ReferenceEquals(source, _home))
            {
                _home.Focus(FocusState.Pointer);
                return;
            }
            source = VisualTreeHelper.GetParent(source);
        }
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        Close();
    }

    void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_disposed || _closing) return;
        args.Cancel = true;
        Close();
    }

    void OnHomeClick(object sender, RoutedEventArgs args)
    {
        if (_isClosed) return;
        _selectHome();
        if (_visible) Close();
    }

    void OnClosed(object sender, WindowEventArgs args)
    {
        if (_isClosed) return;
        _isClosed = true;
        _visible = false;
        _home.Click -= OnHomeClick;
        _home.KeyDown -= OnKeyDown;
        _root.KeyDown -= OnKeyDown;
        _root.PointerPressed -= OnRootPointerPressed;
        _root.Loaded -= OnLoaded;
        AppWindow.Closing -= OnAppWindowClosing;
        Closed -= OnClosed;
        _closed(this);
    }

    static Button CreateEntry(string name, string glyph, bool enabled)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 20,
            Foreground = new SolidColorBrush(enabled
                ? Colors.White
                : Color.FromArgb(255, 179, 188, 201))
        };
        var entry = new Button
        {
            Width = EntryWidthDip,
            Height = EntryHeightDip,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(enabled
                ? Color.FromArgb(246, 29, 34, 43)
                : Color.FromArgb(228, 33, 39, 49)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(enabled ? (byte)78 : (byte)38, 255, 255, 255)),
            BorderThickness = new Thickness(0.75),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = enabled,
            Content = icon
        };
        AutomationProperties.SetName(entry, name);
        if (!enabled) AutomationProperties.SetHelpText(entry, "此项目暂不可用。");
        return entry;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(nint window, out WindowRect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
}
