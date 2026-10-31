using System.Runtime.InteropServices;
using System.Text.Json;
using GlassDesk.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace GlassDesk.App;

/// <summary>One reusable light settings window; normal Close hides it.</summary>
public sealed class SettingsWindow : Window, IDisposable
{
    // Quiet light material: pale canvas, white grouped surfaces, translucent sidebar.
    const uint CanvasColor = 0xFFF5F5F7;
    const uint FallbackCanvasColor = 0xFFF0F2F5;
    const uint SidebarColor = 0xBAF0F1F4;
    const uint SurfaceColor = 0xFCFFFFFF;
    const uint BorderColor = 0xFFE3E4E8;
    // Local pointer fills do not add a second glass surface to every setting.
    const uint ButtonColor = 0xFFFFFFFF;
    const uint ButtonPointerOverColor = 0x1420252C;
    const uint ButtonPressedColor = 0x2420252C;
    const uint ButtonDisabledColor = 0x40FFFFFF;
    const uint AccentFill = 0xFF1689F9;
    const uint InkColor = 0xFF20252C;
    const double ContentWidth = 680;
    static readonly AccessibilitySettings HighContrast = new();
    static readonly UISettings SystemColors = new();
    static readonly string[] PageTitles = ["外观", "时间", "交互", "陪伴", "插件", "通用"];
    const int TimePage = 1, PluginPage = 4;
    static ControlTemplate? _optionTemplate;
    static ControlTemplate OptionTemplate => _optionTemplate ??= (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
          <Border x:Name="OptionRoot" Background="{TemplateBinding Background}"
                  BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                  CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}">
            <VisualStateManager.VisualStateGroups>
              <VisualStateGroup x:Name="CommonStates">
                <VisualState x:Name="Normal"/>
                <VisualState x:Name="PointerOver"><VisualState.Setters><Setter Target="OptionRoot.Opacity" Value="0.9"/></VisualState.Setters></VisualState>
                <VisualState x:Name="Pressed"><VisualState.Setters><Setter Target="OptionRoot.Opacity" Value="0.75"/></VisualState.Setters></VisualState>
                <VisualState x:Name="Disabled"><VisualState.Setters><Setter Target="OptionRoot.Opacity" Value="0.4"/></VisualState.Setters></VisualState>
              </VisualStateGroup>
            </VisualStateManager.VisualStateGroups>
            <ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
                              HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
        """);
    readonly UISettings _uiSettings = new();
    readonly ISettingsCoordinator _coordinator;
    readonly DispatcherQueueTimer _changeTimer;
    readonly DispatcherQueueTimer _statusTimer;
    readonly DispatcherQueueTimer _feedbackTimer;
    readonly Action<string>? _trace;
    readonly bool _minimalContent;
    FrameworkElement? _tracedRoot;
    readonly TextBlock _pageTitle = SettingsTypography.Apply(
        new TextBlock { Text = PageTitles[0], VerticalAlignment = VerticalAlignment.Center }, TextRole.PageTitle, InkRole.Primary);
    readonly Border _colorPickerHost = new() { CornerRadius = new CornerRadius(UiRadius.Control), Height = 240 };
    readonly RectangleGeometry _colorPickerClip = new();
    readonly Slider _blur = new() { Minimum = 0, Maximum = 40, StepFrequency = 1 };
    readonly Slider _transparency = new() { Minimum = 10, Maximum = 90, StepFrequency = 1 };
    readonly Slider _hover = new() { Minimum = 100, Maximum = 1500, StepFrequency = 50 };
    readonly Slider _leave = new() { Minimum = 100, Maximum = 1500, StepFrequency = 50 };
    readonly ToggleSwitch _reducedMotion = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _fullscreen = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _startup = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _audio = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _companion = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _companionFollowMouse = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _liquidGlass = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _clockTime = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _clockDate = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly ToggleSwitch _clockWeekday = new() { OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
    readonly TextBlock _audioStatus = Description();
    readonly TextBlock _liquidGlassStatus = Description();
    readonly StackPanel _pluginList = new() { Spacing = 12 };
    readonly StackPanel _pluginPages = new() { Spacing = 12 };
    readonly TextBox _customColor = new() { PlaceholderText = "#202329", MinWidth = 0 };
    readonly ColorPicker _colorPicker = new()
    {
        IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false,
        IsColorSliderVisible = true, IsColorChannelTextInputVisible = true, IsHexInputVisible = false,
        IsColorPreviewVisible = true, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    readonly ComboBox _companionForm = new() { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _colorFeedback = Description();
    readonly Microsoft.UI.Xaml.Shapes.Path _companionPreview = new() { StrokeThickness = 1 };
    readonly Microsoft.UI.Xaml.Shapes.Ellipse _previewLeftEye = new() { Width = 8, Height = 12 };
    readonly Microsoft.UI.Xaml.Shapes.Ellipse _previewRightEye = new() { Width = 8, Height = 12 };
    NavigationView? _navigation;
    readonly List<NavigationViewItem> _navigationItems = [];
    SettingsPageDeck? _deck;
    SettingsCapsulePreview? _capsuleLayoutPreview;
    SettingsCapsulePreview? _clockLayoutPreview;
    SettingsLiquidPreview? _liquidPreview;
    readonly List<(string Id, RadioButton Button)> _clockFontChoices = [];
    readonly List<(WidgetTextInk Ink, RadioButton Button)> _textInkChoices = [];
    readonly List<Action> _switchRefresh = [];
    readonly List<RadioButton> _appearanceChoices = [];
    readonly List<(uint Color, Button Button)> _colorPresets = [];
    readonly TextBlock _saveHint = Description("更改会自动保存");
    string? _pluginsKey;
    readonly TextBlock _blurLabel = Label();
    readonly TextBlock _transparencyLabel = Label();
    TextBlock? _systemLensMaterialNote;
    readonly TextBlock _hoverLabel = Label();
    readonly TextBlock _leaveLabel = Label();
    readonly TextBlock _connection = Label();
    readonly TextBlock _capabilities = Description();
    readonly TextBlock _status = Description();
    readonly TextBlock _hotkeyHint = Description("选中输入框，按下组合键即可绑定。");
    readonly TextBlock _connectionHelp = Description("请在网易云音乐中播放一首歌。若仍未连接，请在音乐设置中开启 SMTC（系统媒体控制）。");
    readonly TextBlock _visibilityText = Label();
    readonly TextBox _hotkey = new() { IsReadOnly = true, PlaceholderText = "未绑定", MinWidth = 220 };
    readonly Button _visibility = new() { Content = "隐藏组件" };
    readonly Border _onboarding = new() { Visibility = Visibility.Collapsed };
    readonly InfoBar _feedback = new() { IsOpen = false, IsClosable = true };
    readonly TextBox _diagnosticPath = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    WidgetPreferences? _pending;
    WidgetPreferences? _inFlight;
    TaskCompletionSource? _updateCompletion;
    bool _refreshing;
    bool _shutdown;
    bool _disposed;
    bool _updating;
    internal bool LastSaveFailed {get;private set;}
    bool _shown;
    bool _hexDirty, _writingHex;
    long _appearanceRevision, _pendingAppearanceRevision;
    (uint Color, string Id)? _previewAppearance;

    public SettingsWindow(ISettingsCoordinator coordinator) : this(coordinator, minimalContent: false, useBackdrop: true) { }

    internal SettingsWindow(ISettingsCoordinator coordinator, bool minimalContent, bool useBackdrop, Action<string>? trace = null)
    {
        _coordinator = coordinator;
        _minimalContent = minimalContent;
        _trace = trace;
        DispatcherQueueTimer? timer = null;
        DispatcherQueueTimer? statusTimer = null;
        DispatcherQueueTimer? feedbackTimer = null;
        try
        {
            Title = _coordinator.IsUiPreview ? "GlassDesk UI Preview 设置" : "GlassDesk 设置";
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "GlassDesk.ico");
            if (File.Exists(iconPath))
            {
                try { AppWindow.SetIcon(iconPath); }
                catch { /* Branding must not prevent the settings from opening. */ }
            }
            Trace("settings-window-created");
            _changeTimer = timer = DispatcherQueue.CreateTimer();
            _changeTimer.Interval = TimeSpan.FromMilliseconds(250);
            _changeTimer.IsRepeating = false;
            _changeTimer.Tick += OnChangeTimerTick;
            _statusTimer = statusTimer = DispatcherQueue.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.IsRepeating = true;
            _statusTimer.Tick += OnStatusTimerTick;
            _feedbackTimer = feedbackTimer = DispatcherQueue.CreateTimer();
            _feedbackTimer.Interval = TimeSpan.FromSeconds(2.4);
            _feedbackTimer.IsRepeating = false;
            _feedbackTimer.Tick += OnFeedbackTimerTick;
            if (minimalContent) BuildMinimalContent();
            else BuildContent();
            Trace(minimalContent ? "settings-content-minimal" : "settings-content-full");
            _tracedRoot = (FrameworkElement)Content;
            _tracedRoot.Loaded += OnContentLoaded;
            _tracedRoot.LayoutUpdated += OnFirstLayoutUpdated;
            if (useBackdrop && !HighContrast.HighContrast && SettingsAcrylicBackdrop.IsSupported)
            {
                try { SystemBackdrop = new SettingsAcrylicBackdrop(); Trace("settings-sidebar-acrylic"); }
                catch { SystemBackdrop = null; Trace("settings-backdrop-fallback"); }
            }
            if (!minimalContent) BindEvents();
            ResizeToWorkArea();
            Refresh();
            _coordinator.Changed += Refresh;
            AppWindow.Closing += OnAppWindowClosing;
            Closed += OnWindowClosed;
            Trace("settings-construction-complete");
        }
        catch (Exception error)
        {
            Trace($"settings-construction-error:{error}");
            _shutdown = true;
            timer?.Stop();
            if (timer is not null) timer.Tick -= OnChangeTimerTick;
            statusTimer?.Stop();
            if (statusTimer is not null) statusTimer.Tick -= OnStatusTimerTick;
            feedbackTimer?.Stop();
            if (feedbackTimer is not null) feedbackTimer.Tick -= OnFeedbackTimerTick;
            _coordinator.Changed -= Refresh;
            AppWindow.Closing -= OnAppWindowClosing;
            Closed -= OnWindowClosed;
            DetachContentTrace();
            try { SystemBackdrop = null; Content = null; Close(); }
            catch { /* Preserve the original resource initialization failure. */ }
            _disposed = true;
            throw;
        }
    }

    void Trace(string message)
    {
        try { _trace?.Invoke(message); }
        catch { /* Optional QA tracing must not change the window's behavior. */ }
    }

    void OnContentLoaded(object sender, RoutedEventArgs args)
    {
        Trace("settings-content-loaded");
    }
    void OnFirstLayoutUpdated(object? sender, object args)
    {
        if (_tracedRoot is null || _tracedRoot.ActualWidth <= 0 || _tracedRoot.ActualHeight <= 0) return;
        Trace($"settings-first-layout:{_tracedRoot.ActualWidth:0.##}x{_tracedRoot.ActualHeight:0.##}");
        _tracedRoot.LayoutUpdated -= OnFirstLayoutUpdated;
    }

    void DetachContentTrace()
    {
        if (_tracedRoot is null) return;
        _tracedRoot.Loaded -= OnContentLoaded;
        _tracedRoot.LayoutUpdated -= OnFirstLayoutUpdated;
        _tracedRoot = null;
    }

    async void OnChangeTimerTick(DispatcherQueueTimer sender, object args) => await FlushPreferencesAsync();
    void OnFeedbackTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_disposed) _feedback.IsOpen = false;
    }
    void OnStatusTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !_shown || _minimalContent || !AppWindow.IsVisible)
        {
            sender.Stop();
            return;
        }
        RefreshRuntimeStatus();
    }
    void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdown) return;
        args.Cancel = true;
        Hide();
    }
    void OnWindowClosed(object sender, WindowEventArgs args) => Detach();

    public void Show()
    {
        if (_disposed) return;
        Refresh();
        AppWindow.Show();
        _shown = true;
        _liquidPreview?.SetActive(_navigation?.SelectedItem is NavigationViewItem { Tag: 2 });
        _deck?.StopTransition();
        if (!_minimalContent) _statusTimer.Start();
        Trace("settings-appwindow-shown");
        Activate();
        Trace("settings-window-activated");
    }

    public void ShowPlugins()
    {
        Show();
        if (_navigation is not null) _navigation.SelectedItem = _navigationItems[PluginPage];
    }

    void BuildMinimalContent()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(28) };
        panel.Children.Add(SettingsTypography.Apply(new TextBlock { Text = "GlassDesk" }, TextRole.PageTitle, InkRole.Primary));
        panel.Children.Add(Description("设置窗口验收"));
        var close = new Button { Content = "关闭设置" };
        SettingsTypography.Apply(close, TextRole.Label, InkRole.Primary);
        close.Click += (_, _) => Hide();
        panel.Children.Add(close);
        var root = new Grid { RequestedTheme = ElementTheme.Light, Background = Brush(0xFFF6F8FB) };
        root.Children.Add(panel);
        Content = root;
    }

    public void Hide()
    {
        if (_disposed) return;
        _shown = false;
        _liquidPreview?.SetActive(false);
        _deck?.StopTransition();
        _statusTimer.Stop();
        AppWindow.Hide();
        _ = FlushPendingAsync();
    }

    void BuildContent()
    {
        var root = new Grid { RequestedTheme = ElementTheme.Light, Background = Brush(SidebarColor) };
        _navigation = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 190,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false, IsTitleBarAutoPaddingEnabled = false,
            Background = new SolidColorBrush(Colors.Transparent), IsPaneOpen = true
        };
        _navigation.Resources["NavigationViewDefaultPaneBackground"] = Brush(SidebarColor);
        _navigation.Resources["NavigationViewContentBackground"] = Brush(CanvasColor);
        if (!HighContrast.HighContrast)
        {
            foreach (var key in new[] { "NavigationViewItemBackgroundSelected", "NavigationViewItemBackgroundSelectedPointerOver", "NavigationViewItemBackgroundSelectedPressed" })
                _navigation.Resources[key] = Brush(AccentFill);
            _navigation.Resources["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(Colors.Transparent);
        }
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(16, 22, 8, 24) };
        var logo = new Image { Width = 38, Height = 38, Stretch = Stretch.Uniform,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/GlassDesk-logo.png")) };
        Name(logo, "GlassDesk 标志");
        brand.Children.Add(new Border { Child = logo, CornerRadius = new CornerRadius(10), Background = Brush(ButtonColor), Padding = new Thickness(2) });
        var brandText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var wordmark = SettingsTypography.Apply(new TextBlock { Text = "GlassDesk" }, TextRole.PageTitle, InkRole.Primary);
        wordmark.FontSize = 18;
        brandText.Children.Add(wordmark);
        brandText.Children.Add(Description("桌面陪伴"));
        brand.Children.Add(brandText);
        _navigation.PaneHeader = brand;
        string[] glyphs = ["\uE790", "\uE823", "\uE7C2", "\uE76E", "\uEA86", "\uE713"];
        for (int index = 0; index < PageTitles.Length; index++)
        {
            var label = SettingsTypography.Apply(new TextBlock { Text = PageTitles[index] }, TextRole.NavigationSettings, InkRole.Primary);
            var item = new NavigationViewItem
            {
                Content = label, Icon = new FontIcon { Glyph = glyphs[index], FontSize = 16 },
                Tag = index, MinHeight = 40, Margin = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(8)
            };
            Name(item, PageTitles[index]);
            _navigationItems.Add(item); _navigation.MenuItems.Add(item);
        }
        var visibilityContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        visibilityContent.Children.Add(new FontIcon { Glyph = "\uE890", FontSize = 16 });
        SettingsTypography.Apply(_visibilityText, TextRole.CaptionSettings, InkRole.Secondary);
        visibilityContent.Children.Add(_visibilityText); _visibility.Content = visibilityContent;
        _visibility.MinHeight = 40; _visibility.Padding = new Thickness(12, 8, 12, 8);
        _visibility.CornerRadius = new CornerRadius(8);
        _visibility.Background = null; _visibility.BorderThickness = new Thickness(0);
        _visibility.HorizontalAlignment = HorizontalAlignment.Stretch;
        _visibility.Margin = new Thickness(12, 8, 12, 16);
        _navigation.PaneFooter = _visibility;
        root.Children.Add(_navigation);
        var content = new Grid
        {
            RowSpacing = 12, Padding = new Thickness(32, 18, 28, 0),
            MaxWidth = ContentWidth + 60, Background = Brush(CanvasColor), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SettingsTypography.ApplySettingsTitle(_pageTitle);
        content.Children.Add(_pageTitle);
        Grid.SetRow(_feedback, 1);
        _navigation.Content = content;

        var onboardingContent = new Grid { ColumnSpacing = 12 };
        onboardingContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        onboardingContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        onboardingContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        onboardingContent.Children.Add(new FontIcon
        {
            Glyph = "\uE946", FontSize = 16, Foreground = Brush(0xFF28786E), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        });
        var onboardingText = new StackPanel { Spacing = 4 };
        onboardingText.Children.Add(SettingsTypography.Apply(new TextBlock { Text = "使用提示" }, TextRole.CaptionSettings, InkRole.Primary));
        onboardingText.Children.Add(Description("悬停查看歌曲，点击展开播放器。组件固定在主屏幕可用区域顶部居中，点击组件外部收起。"));
        Grid.SetColumn(onboardingText, 1); onboardingContent.Children.Add(onboardingText);
        var understood = new Button
        {
            Content = new FontIcon { Glyph = "\uE8BB", FontSize = 12 }, Width = 32, Height = 32,
            MinWidth = 32, Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Name(understood, "知道了");
        ToolTipService.SetToolTip(understood, "知道了");
        understood.Click += (_, _) => QueuePreferences(CurrentPreferences with { OnboardingSeen = true });
        Grid.SetColumn(understood, 2); onboardingContent.Children.Add(understood);
        _onboarding.Child = onboardingContent;
        _onboarding.Padding = new Thickness(14, 12, 10, 12);
        _onboarding.CornerRadius = new CornerRadius(UiRadius.Control);
        _onboarding.Background = Brush(0xFFEFF6F4);
        _onboarding.BorderBrush = Brush(0xFFD8E8E3);
        _onboarding.BorderThickness = new Thickness(1);
        content.Children.Add(_feedback);

        var appearance = Section("调整桌面组件的材质、内容与动态。");
        _capsuleLayoutPreview = new SettingsCapsulePreview();
        appearance.Children.Add(_capsuleLayoutPreview);
        appearance.Children.Add(SectionHeading("材质"));
        var legacyMaterialControls = Group(SliderRow("模糊强度", _blurLabel, _blur),
            SliderRow("透明度", _transparencyLabel, _transparency));
        appearance.Children.Add(legacyMaterialControls);
        _systemLensMaterialNote = Description("Liquid Glass 使用清透材质；这两项调节适用于磨砂模式。");
        _systemLensMaterialNote.Visibility = Visibility.Collapsed;
        appearance.Children.Add(_systemLensMaterialNote);
        appearance.Children.Add(SectionHeading("文字颜色"));
        appearance.Children.Add(BuildTextInkChoices());
        appearance.Children.Add(Description("按当前桌面背景选择黑色或白色；文字颜色不会自动切换。"));
        appearance.Children.Add(SectionHeading("动态"));
        appearance.Children.Add(Group(ToggleRow("简化动画", _reducedMotion),
            ToggleRow("真实音频波浪条", _audio, "仅在展示音乐频谱时采集。")));
        appearance.Children.Add(_audioStatus);
        var restore = CommandButton("恢复默认外观", "\uE777");
        restore.Click += (_, _) => QueuePreferences(CurrentPreferences with { Blur = 20, TintOpacity = .24f, ReducedMotion = false, TextInk = WidgetTextInk.Black });
        appearance.Children.Add(restore);
        appearance.Children.Add(_onboarding);

        var time = Section("让时间有自己的样子。");
        _clockLayoutPreview = new SettingsCapsulePreview();
        time.Children.Add(_clockLayoutPreview);
        time.Children.Add(SectionHeading("数字字体"));
        time.Children.Add(BuildClockFontChoices());
        time.Children.Add(Description("应用于胶囊与小展开的时间数字，日期和星期保持清晰的系统字体。"));
        time.Children.Add(SectionHeading("显示内容"));
        time.Children.Add(Group(ToggleRow("时间", _clockTime), ToggleRow("日期", _clockDate), ToggleRow("星期", _clockWeekday)));
        var restoreClock = CommandButton("恢复默认时间样式", "\uE777");
        restoreClock.Click += (_, _) => QueuePreferences(CurrentPreferences with { ClockShowTime = true,
            ClockShowDate = false, ClockShowWeekday = false, ClockFontId = ClockFontCatalog.System });
        time.Children.Add(restoreClock);

        var interaction = Section("预览胶囊展开与收拢效果。");
        _liquidPreview = new SettingsLiquidPreview(() => CurrentPreferences.ReducedMotion || !_uiSettings.AnimationsEnabled,
            _coordinator.CreatePreviewGeometry,()=>WinRT.Interop.WindowNative.GetWindowHandle(this));
        interaction.Children.Add(Surface(_liquidPreview));
        interaction.Children.Add(SectionHeading("桌面行为"));
        interaction.Children.Add(Group(SliderRow("悬停展开延迟", _hoverLabel, _hover),
            SliderRow("离开收起延迟", _leaveLabel, _leave)));
        interaction.Children.Add(SectionHeading("专注当前任务"));
        interaction.Children.Add(Group(ToggleRow("全屏时自动隐藏", _fullscreen,
            "进入游戏或全屏视频时，暂时隐藏桌面组件。")));
        interaction.Children.Add(Description("桌面组件固定在主屏幕可用区域顶部居中。"));

        var companion = Section("安静的原创小球，用表情和视线回应你。");
        companion.Children.Add(Group(ToggleRow("陪伴球", _companion, "回应指针、音乐和展开动作，并自然眨眼；隐藏时休息。"),
            ToggleRow("陪伴球跟随鼠标", _companionFollowMouse, "收起时也能望向胶囊外的鼠标；关闭后立即停止跟随。")));
        var colors = new StackPanel { Spacing = 12 };
        colors.Children.Add(SectionHeading("球体颜色"));
        colors.Children.Add(BuildColorPresets());
        colors.Children.Add(Description("眼睛会自动使用清晰的浅色或深色。"));
        Name(_colorPicker, "陪伴颜色，RGB 不透明取色器");
        // Keep the compact well, but let the complete picker template scroll
        // inside it so the channel controls remain reachable at any pane width.
        _colorPicker.MinHeight = 0;
        _colorPicker.VerticalAlignment = VerticalAlignment.Top;
        _colorPickerHost.Child = new ScrollViewer
        {
            Content = _colorPicker,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _colorPickerHost.Clip = _colorPickerClip;
        _colorPickerHost.SizeChanged += (_, args) => ClipColorPicker(args.NewSize);
        colors.Children.Add(new Expander { Header = "更多颜色", Content = _colorPickerHost,
            HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = false });
        var customRow = new Grid { ColumnSpacing = 8 };
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Name(_customColor, "自定义球体颜色，六位十六进制 RGB");
        customRow.Children.Add(_customColor);
        var applyColor = CommandButton("应用", "\uE790");
        applyColor.Click += async (_, _) => { if (CommitHexColor()) await FlushPendingAsync(); };
        Grid.SetColumn(applyColor, 1); customRow.Children.Add(applyColor); WrapActions(customRow, _customColor, applyColor);
        colors.Children.Add(customRow);
        _colorFeedback.Visibility = Visibility.Collapsed; colors.Children.Add(_colorFeedback);
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(SectionHeading("形式"));
        _companionForm.Items.Add(new ComboBoxItem { Content = "原生", Tag = CompanionAppearanceIds.Original });
        _companionForm.Items.Add(new ComboBoxItem { Content = "猫娘", Tag = CompanionAppearanceIds.CatEar });
        _companionForm.Items.Add(new ComboBoxItem { Content = "恶魔", Tag = CompanionAppearanceIds.Devil });
        Name(_companionForm, "陪伴形式"); form.Children.Add(BuildCompanionChoices());
        form.Children.Add(Description("猫娘使用宽根猫耳；恶魔使用向内收尖的弯角。保留当前颜色。"));
        var preview = new Canvas { Width = 96, Height = 100, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        preview.Children.Add(_companionPreview);
        Canvas.SetLeft(_previewLeftEye, 31.7); Canvas.SetTop(_previewLeftEye, 45.1);
        Canvas.SetLeft(_previewRightEye, 56.3); Canvas.SetTop(_previewRightEye, 45.1);
        preview.Children.Add(_previewLeftEye); preview.Children.Add(_previewRightEye);
        var characterPreview = new Border
        {
            Child = preview, Background = Brush(0xFFF0F2F5), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 8, 16, 8), IsHitTestVisible = false
        };
        companion.Children.Insert(1, characterPreview);
        companion.Children.Add(form); companion.Children.Add(colors);

        var plugins = Section("安装本地功能插件，分别控制各项贡献。");
        plugins.Children.Add(Group(ToggleRow("液态玻璃", _liquidGlass, "在系统磨砂上叠加曲面高光与边缘厚度，不截取桌面。")));
        plugins.Children.Add(_liquidGlassStatus);
        var import = CommandButton("导入本地插件", "\uE898");
        import.Click += async (_, _) =>
        {
            import.IsEnabled = false;
            try
            {
                await FlushPendingAsync();
                string? packagePath;
                try
                {
                    // App SDK pickers accept the owning WindowId directly and support
                    // elevated desktop processes, unlike the legacy UWP picker.
                    var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
                    picker.FileTypeFilter.Add(".glassdesk-plugin");
                    var file = await picker.PickSingleFileAsync();
                    packagePath = file?.Path;
                }
                catch (Exception error)
                {
                    Trace($"plugin-picker-unavailable:{error.GetType().Name}:0x{error.HResult:X8}");
                    ShowFeedback($"无法打开文件选择器（0x{error.HResult:X8}），请稍后重试。", error: true);
                    return;
                }
                if (packagePath is not null) ReportResult(await _coordinator.ImportPluginAsync(packagePath));
            }
            catch (Exception error)
            {
                Trace($"plugin-import-failed:{error.GetType().Name}:0x{error.HResult:X8}");
                ShowFeedback($"插件导入失败（0x{error.HResult:X8}），请检查文件后重试。", error: true);
            }
            finally { import.IsEnabled = true; }
        };
        plugins.Children.Add(import);
        plugins.Children.Add(Description("插件在程序内运行，请仅导入你信任的文件。更新同名插件会保留配置。"));
        plugins.Children.Add(_pluginList); plugins.Children.Add(_pluginPages);

        var startup = Section();
        startup.Children.Add(Group(ToggleRow("登录 Windows 后启动", _startup, "下次登录时自动启动 GlassDesk。")));
        var hotkeySettings = new StackPanel { Spacing = 10, Padding = new Thickness(0) };
        hotkeySettings.Children.Add(SettingTitle("显示 / 隐藏快捷键"));
        var binding = new Grid { ColumnSpacing = 12 };
        binding.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        binding.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        binding.Children.Add(_hotkey);
        _hotkey.MinWidth = 80;
        var clear = CommandButton("清除", "\uE894");
        _startup.IsEnabled = _hotkey.IsEnabled = clear.IsEnabled = !_coordinator.IsUiPreview;
        clear.Click += (_, _) => QueuePreferences(CurrentPreferences with { HotkeyKey = 0, HotkeyModifiers = 0 });
        Grid.SetColumn(clear, 1); binding.Children.Add(clear);
        WrapActions(binding, _hotkey, clear);
        hotkeySettings.Children.Add(binding); hotkeySettings.Children.Add(_hotkeyHint);
        startup.Children.Add(Surface(hotkeySettings));

        var connection = Section();
        var connectionSettings = new StackPanel { Spacing = 10, Padding = new Thickness(0) };
        SettingsTypography.Apply(_connection, TextRole.SectionTitle, InkRole.Primary);
        connectionSettings.Children.Add(_connection); connectionSettings.Children.Add(_capabilities); connectionSettings.Children.Add(_status);
        var connectionActions = new Grid { ColumnSpacing = 8 };
        connectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var openMusic = CommandButton("打开音乐", "\uE8D6");
        Name(openMusic, "打开网易云音乐");
        ToolTipService.SetToolTip(openMusic, "打开网易云音乐");
        openMusic.HorizontalAlignment = HorizontalAlignment.Stretch;
        openMusic.Click += (_, _) => _coordinator.OpenMusic();
        connectionActions.Children.Add(openMusic);
        var reconnect = CommandButton("重新连接", "\uE72C");
        reconnect.HorizontalAlignment = HorizontalAlignment.Stretch;
        reconnect.Click += async (_, _) =>
        {
            reconnect.IsEnabled = false;
            try { await _coordinator.RetryMediaAsync(); }
            catch { ShowFeedback("连接未能恢复，请稍后重试。", error: true); }
            finally { reconnect.IsEnabled = true; }
        };
        Grid.SetColumn(reconnect, 1);
        connectionActions.Children.Add(reconnect);
        WrapActions(connectionActions, openMusic, reconnect);
        connectionSettings.Children.Add(connectionActions);
        connectionSettings.Children.Add(_connectionHelp);
        connection.Children.Add(Surface(connectionSettings));

        var about = new StackPanel { Spacing = 10, Padding = new Thickness(0) };
        about.Children.Add(SettingTitle("GlassDesk"));
        string version = typeof(SettingsWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "preview";
        about.Children.Add(Description($"版本 {version}{(_coordinator.IsUiPreview ? " · UI Preview" : "")} · Windows 11"));
        var export = CommandButton("导出诊断", "\uE896");
        export.Click += async (_, _) =>
        {
            export.IsEnabled = false;
            try
            {
                _diagnosticPath.Text = await _coordinator.ExportDiagnosticsAsync();
                _diagnosticPath.Visibility = Visibility.Visible;
                ShowFeedback("诊断已导出。");
            }
            catch (Exception) { ShowFeedback("诊断导出失败，请稍后重试。", error: true); }
            finally { export.IsEnabled = true; }
        };
        about.Children.Add(export); about.Children.Add(_diagnosticPath);
        about.Children.Add(Description("诊断用于排查运行与连接问题，不包含歌曲历史或账号信息。"));
        about.Children.Add(Separator());
        var quit = CommandButton("退出 GlassDesk", "\uE7E8");
        quit.Background = null; quit.BorderThickness = new Thickness(0);
        quit.Click += async (_, _) => { await FlushPendingAsync(); _coordinator.Quit(); };
        about.Children.Add(quit);
        connection.Children.Add(Surface(about));

        var general = Section("启动、快捷键、音乐连接与诊断。");
        // Keep the existing live-save and connection controls in one concise page.
        general.Children.Add(startup); general.Children.Add(connection);
        var sections = new[] { appearance, time, interaction, companion, plugins, general };
        // One page-level 16 DIP tail plus the deck's 8 DIP padding keeps the last
        // card clear of the footer (the plan's ≥24 DIP bottom inset per page).
        foreach (var page in sections) { page.Padding = new Thickness(0, 0, 0, 16); page.MaxWidth = ContentWidth; }
        _deck = new SettingsPageDeck(sections, () => CurrentPreferences.ReducedMotion || !_uiSettings.AnimationsEnabled);
        Grid.SetRow(_deck, 2); content.Children.Add(_deck);
        _navigation.SelectionChanged += OnNavigationSelectionChanged;
        _navigation.SelectedItem = _navigationItems[0];

        var footer = new Grid
        {
            Padding = new Thickness(0, 8, 0, 12), MinHeight = 36,
            BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(0, 1, 0, 0)
        };
        SettingsTypography.Apply(_saveHint, TextRole.Caption, InkRole.Caption);
        footer.Children.Add(_saveHint);
        Grid.SetRow(footer, 3); content.Children.Add(footer);
        root.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            // Recording/editing fields and open flyouts handle Escape first.
            if (_hotkey.FocusState != FocusState.Unfocused) return;
            args.Handled = true; Hide();
        };
        root.SizeChanged += (_, args) =>
        {
            bool top = args.NewSize.Width < 620;
            _navigation.PaneDisplayMode = top ? NavigationViewPaneDisplayMode.Top : NavigationViewPaneDisplayMode.Left;
            _navigation.OpenPaneLength = args.NewSize.Width < 820 ? 176 : 200;
            if (!top) _navigation.IsPaneOpen = true;
            _navigation.PaneHeader = top ? null : brand;
            content.Padding = new Thickness(args.NewSize.Width < 820 ? 20 : 32, 16, 20, 0);
        };
        ConfigureGlassControls(root);
        Content = root;
        ExtendsContentIntoTitleBar = false;
        // A conventional complete HWND replaces the old disconnected rounded regions.
        // Keep Windows caption buttons, resizing and Snap instead of emulating them.
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(true, true);
        Name(_blur, "模糊强度"); Name(_transparency, "玻璃透明度");
        Name(_hover, "悬停展开延迟，毫秒"); Name(_leave, "离开收起延迟，毫秒");
        Name(_hotkey, "显示或隐藏快捷键，聚焦后按组合键绑定");
        Name(_diagnosticPath, "已导出的诊断文件路径");
        Name(_reducedMotion, "简化动画"); Name(_fullscreen, "全屏时自动隐藏");
        Name(_startup, "登录 Windows 后启动"); Name(_audio, "真实音频波浪条");
        Name(_companion, "陪伴球"); Name(_liquidGlass, "液态玻璃");
        Name(_companionFollowMouse, "陪伴球跟随鼠标");
        Name(_clockTime, "显示时间"); Name(_clockDate, "显示日期"); Name(_clockWeekday, "显示星期");
    }

    void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: int index }) return;
        RefreshMaterialControls();
        _deck?.SetSelectedIndex(index);
        _liquidPreview?.SetActive(_shown && index == 2);
        _pageTitle.Text = PageTitles[index];
        foreach (var item in _navigationItems)
        {
            bool active = item.Tag is int page && page == index;
            var foreground = active && !HighContrast.HighContrast ? new SolidColorBrush(Colors.White) : Brush(InkColor);
            if (item.Content is TextBlock label) label.Foreground = foreground;
            if (item.Icon is FontIcon icon) icon.Foreground = foreground;
        }
        if (index == 0) _capsuleLayoutPreview?.Present(CurrentPreferences, DateTimeOffset.Now);
        if (index == TimePage) _clockLayoutPreview?.Present(CurrentPreferences, DateTimeOffset.Now);
    }

    void RefreshMaterialControls()
    {
        bool systemLens = _coordinator.UsesSystemCompositorLens;
        _blur.IsEnabled = !systemLens;
        _transparency.IsEnabled = !systemLens;
        if (_systemLensMaterialNote is not null)
            _systemLensMaterialNote.Visibility = systemLens ? Visibility.Visible : Visibility.Collapsed;
        string help = systemLens ? "Liquid Glass 使用清透材质；这两项调节适用于磨砂模式。" : "";
        AutomationProperties.SetHelpText(_blur, help);
        AutomationProperties.SetHelpText(_transparency, help);
    }

    FrameworkElement BuildClockFontChoices()
    {
        var choices = new List<FrameworkElement>();
        foreach (var face in ClockFontCatalog.All)
        {
            var sample = new TextBlock { Text = "10:08", FontFamily = new FontFamily(face.Family), FontSize = 30,
                Foreground = Brush(InkColor), HorizontalAlignment = HorizontalAlignment.Center };
            Typography.SetNumeralAlignment(sample, FontNumeralAlignment.Tabular);
            var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(sample);
            content.Children.Add(Description(face.Name));
            var choice = new RadioButton { Content = content, GroupName = "ClockFont", MinWidth = 0,
                MinHeight = 98, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12),
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                Template = OptionTemplate, UseSystemFocusVisuals = true };
            Name(choice, $"时间字体：{face.Name}，{face.Family}");
            choice.Checked += (_, _) =>
            {
                if (!_refreshing) QueuePreferences(CurrentPreferences with { ClockFontId = face.Id });
            };
            _clockFontChoices.Add((face.Id, choice)); choices.Add(choice);
        }
        return new SettingsColumns(choices, 4, 125);
    }

    FrameworkElement BuildTextInkChoices()
    {
        var choices = new List<FrameworkElement>();
        foreach (var (ink, label, foreground, background) in new[]
        {
            (WidgetTextInk.Black, "黑色", 0xFF000000u, 0xFFF1F2F4u),
            (WidgetTextInk.White, "白色", 0xFFFFFFFFu, 0xFF343840u)
        })
        {
            var sample = new Border
            {
                Width = 42, Height = 32, CornerRadius = new CornerRadius(6),
                Background = Brush(background), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = "Aa", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Brush(foreground), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(sample); content.Children.Add(SettingTitle(label));
            var choice = new RadioButton
            {
                Content = content, GroupName = "WidgetTextInk", MinWidth = 0, MinHeight = 64,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(10), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                Template = OptionTemplate, UseSystemFocusVisuals = true
            };
            Name(choice, $"组件文字颜色：{label}");
            choice.Checked += (_, _) =>
            {
                if (!_refreshing) QueuePreferences(CurrentPreferences with { TextInk = ink });
            };
            _textInkChoices.Add((ink, choice)); choices.Add(choice);
        }
        return new SettingsColumns(choices, 2, 130);
    }

    // Keep the native ToggleSwitch as the sole input/UIA/keyboard target. A
    // non-interactive drawing underneath replaces only its appearance. There is
    // no second Click handler or shadow boolean that can drift from IsOn.
    FrameworkElement CompanionSwitch(ToggleSwitch toggle)
    {
        var host = new Grid { Width = 64, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        var drawing = new Grid { IsHitTestVisible = false };
        var track = new Border { Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Background = Brush(0xFFE6E8ED), BorderBrush = Brush(0xFFC4C8D0) };
        drawing.Children.Add(track);
        var shift = new TranslateTransform();
        var face = new Grid { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), RenderTransform = shift };
        var press = new ScaleTransform { CenterX = 13, CenterY = 13 };
        var orb = new Border { CornerRadius = new CornerRadius(13), Background = Brush(0xFF202329), RenderTransform = press };
        face.Children.Add(orb);
        var calm = new Canvas { Width = 26, Height = 26 };
        var happy = new Canvas { Width = 26, Height = 26 };
        for (int index = 0; index < 2; index++)
        {
            var eye = new Border { Width = 3.3, Height = 6.1, CornerRadius = new CornerRadius(1.65), Background = new SolidColorBrush(Colors.White) };
            Canvas.SetLeft(eye, index == 0 ? 7 : 15.5); Canvas.SetTop(eye, 9.5); calm.Children.Add(eye);
            // Happy uses the character's tilted, wider eyes and raised lower lid.
            var smileEye = new Microsoft.UI.Xaml.Shapes.Path { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2.3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry { Figures = { new PathFigure { StartPoint = new(0, 3.5),
                    Segments = { new QuadraticBezierSegment { Point1 = new(2.5, -1), Point2 = new(5, 3.5) } } } } } };
            Canvas.SetLeft(smileEye, index == 0 ? 5.9 : 14.5); Canvas.SetTop(smileEye, 9.7); happy.Children.Add(smileEye);
        }
        face.Children.Add(calm); face.Children.Add(happy); drawing.Children.Add(face);
        var focus = new Border { BorderBrush = Brush(AccentFill), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(18), Margin = new Thickness(-3, 3, -3, 3), Visibility = Visibility.Collapsed };
        drawing.Children.Add(focus); host.Children.Add(drawing);
        toggle.Width = 64; toggle.MinWidth = 64; toggle.Height = 44; toggle.Opacity = 0;
        host.Children.Add(toggle);
        Storyboard? transition = null;
        bool? prior = null;
        bool motionWasReduced = false;
        void Present()
        {
            bool high = HighContrast.HighContrast;
            drawing.Visibility = high ? Visibility.Collapsed : Visibility.Visible;
            toggle.Opacity = high ? 1 : 0;
            if (high) { transition?.Stop(); prior = null; return; }
            bool reduced = CurrentPreferences.ReducedMotion || !_uiSettings.AnimationsEnabled;
            bool changed = prior != toggle.IsOn;
            bool animate = prior.HasValue && changed && toggle.IsLoaded && !reduced;
            if (changed || reduced != motionWasReduced)
            {
                double current = shift.X, target = toggle.IsOn ? 34 : 0;
                transition?.Stop(); transition = null; shift.X = animate ? current : target;
                if (animate)
                {
                    var slide = new DoubleAnimation { From = current, To = target, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(slide, shift); Storyboard.SetTargetProperty(slide, "X");
                    transition = new Storyboard(); transition.Children.Add(slide); transition.Begin();
                }
            }
            motionWasReduced = reduced;
            drawing.Opacity = toggle.IsEnabled ? 1 : .4;
            track.Background = Brush(toggle.IsOn ? AccentFill : 0xFFE6E8ED);
            track.BorderBrush = Brush(toggle.IsOn ? 0xFF147BE0 : 0xFFC4C8D0);
            calm.Visibility = toggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
            happy.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            focus.Visibility = toggle.FocusState == FocusState.Keyboard ? Visibility.Visible : Visibility.Collapsed;
            prior = toggle.IsOn;
        }
        toggle.Toggled += (_, _) => Present();
        toggle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        { if (toggle.IsEnabled) press.ScaleX = press.ScaleY = .94; }), true);
        void ReleasePress() { press.ScaleX = press.ScaleY = 1; }
        toggle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ReleasePress()), true);
        toggle.PointerCanceled += (_, _) => ReleasePress(); toggle.PointerCaptureLost += (_, _) => ReleasePress();
        toggle.GotFocus += (_, _) => Present(); toggle.LostFocus += (_, _) => Present();
        toggle.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => Present());
        host.Loaded += (_, _) => Present();
        host.Unloaded += (_, _) => { transition?.Stop(); transition = null; prior = null; };
        host.PointerEntered += (_, _) => { if (toggle.IsEnabled) orb.Background = Brush(0xFF343840); };
        host.PointerExited += (_, _) => orb.Background = Brush(0xFF202329);
        _switchRefresh.Add(Present); Present();
        return host;
    }

    FrameworkElement BuildCompanionChoices()
    {
        string[] ids = [CompanionAppearanceIds.Original, CompanionAppearanceIds.CatEar, CompanionAppearanceIds.Devil];
        string[] names = ["原生", "猫娘", "恶魔"];
        var choices = new List<FrameworkElement>();
        for (int index = 0; index < ids.Length; index++)
        {
            int selected = index;
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(CompanionThumbnail(ids[index])); content.Children.Add(SettingTitle(names[index]));
            var radio = new RadioButton
            {
                Content = content, GroupName = "GlassDeskCompanionAppearance", MinHeight = 96,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                Template = OptionTemplate, UseSystemFocusVisuals = true
            };
            Name(radio, "陪伴形式，" + names[index]);
            radio.Checked += (_, _) => { if (!_refreshing) _companionForm.SelectedIndex = selected; };
            _appearanceChoices.Add(radio); choices.Add(radio);
        }
        return new SettingsColumns(choices, 3, 120);
    }

    static Canvas CompanionThumbnail(string id)
    {
        var canvas = new Canvas { Width = 72, Height = 60, IsHitTestVisible = false };
        var points = CompanionContour.GetPoints(id, CompanionShapePose.Rounded);
        var figure = new PathFigure { IsClosed = true, IsFilled = true,
            StartPoint = new(12 + points[0].X * 2, 8 + points[0].Y * 2) };
        foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = new(12 + point.X * 2, 8 + point.Y * 2) });
        var geometry = new PathGeometry(); geometry.Figures.Add(figure);
        uint body = HighContrast.HighContrast ? Argb(SystemColors.UIElementColor(UIElementType.WindowText)) : 0xFF202329;
        uint eye = HighContrast.HighContrast ? Argb(SystemColors.UIElementColor(UIElementType.Window)) : 0xFFFFFFFF;
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = ColorBrush(body) });
        for (int index = 0; index < 2; index++)
        {
            var shape = new Border { Width = 5.34, Height = 8, CornerRadius = new CornerRadius(2.67), Background = ColorBrush(eye) };
            Canvas.SetLeft(shape, 12 + (index == 0 ? 6.56 : 14.77) * 2); Canvas.SetTop(shape, 27.4);
            canvas.Children.Add(shape);
        }
        return canvas;
    }

    FrameworkElement BuildColorPresets()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (uint color in new uint[] { 0xFF202329, 0xFF5368CB, 0xFF4C8878, 0xFF9071AC, 0xFFC57C6B, 0xFF788798 })
        {
            var button = new Button
            {
                Width = 32, Height = 36, MinWidth = 32, MinHeight = 36, Padding = new Thickness(4),
                CornerRadius = new CornerRadius(8), Background = null, BorderThickness = new Thickness(1),
                Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = ColorBrush(color) }
            };
            Name(button, $"陪伴颜色 #{color & 0xFFFFFF:X6}");
            button.Click += (_, _) =>
            {
                _hexDirty = false; _colorFeedback.Visibility = Visibility.Collapsed;
                SetCompanionAppearance(color, CurrentPreferences.CompanionAppearanceId);
            };
            _colorPresets.Add((color, button)); panel.Children.Add(button);
        }
        return panel;
    }

    static uint Argb(Color value) => (uint)value.A << 24 | (uint)value.R << 16 | (uint)value.G << 8 | value.B;
    static TextBlock SectionHeading(string text) => SettingsTypography.Apply(
        new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 0) }, TextRole.Caption, InkRole.Secondary);

    void BindEvents()
    {
        _blur.ValueChanged += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { Blur = (float)_blur.Value }); };
        _transparency.ValueChanged += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { TintOpacity = (float)(1 - _transparency.Value / 100) }); };
        _hover.ValueChanged += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { HoverDelayMs = (int)_hover.Value }); };
        _leave.ValueChanged += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { LeaveDelayMs = (int)_leave.Value }); };
        _reducedMotion.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { ReducedMotion = _reducedMotion.IsOn }); };
        _fullscreen.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { AvoidFullscreen = _fullscreen.IsOn }); };
        _startup.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { RunAtStartup = _startup.IsOn }); };
        _audio.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { AudioVisualizationEnabled = _audio.IsOn }); };
        _companion.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { CompanionEnabled = _companion.IsOn }); };
        _companionFollowMouse.Toggled += async (_, _) => { if (!_refreshing) { QueuePreferences(CurrentPreferences with { CompanionFollowMouse = _companionFollowMouse.IsOn }); await FlushPendingAsync(); } };
        _liquidGlass.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { LiquidGlassEnabled = _liquidGlass.IsOn }); };
        _clockTime.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { ClockShowTime = _clockTime.IsOn }); };
        _clockDate.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { ClockShowDate = _clockDate.IsOn }); };
        _clockWeekday.Toggled += (_, _) => { if (!_refreshing) QueuePreferences(CurrentPreferences with { ClockShowWeekday = _clockWeekday.IsOn }); };
        _hotkey.KeyDown += CaptureHotkey;
        _colorPicker.ColorChanged += (_, args) =>
        {
            if (_refreshing) return;
            _hexDirty = false;
            SetCompanionAppearance(0xFF000000u | (uint)args.NewColor.R << 16 | (uint)args.NewColor.G << 8 | args.NewColor.B,
                CurrentPreferences.CompanionAppearanceId);
        };
        // Spectrum/slider children handle the routed release themselves.
        _colorPicker.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(async (_, _) => await FlushPendingAsync()), true);
        _companionForm.SelectionChanged += async (_, _) =>
        {
            if (_refreshing || _companionForm.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
            SetCompanionAppearance(CurrentPreferences.CompanionColor, id); await FlushPendingAsync();
        };
        _customColor.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            args.Handled = true; if (CommitHexColor()) await FlushPendingAsync();
        };
        _customColor.TextChanged += (_, _) => { if (!_refreshing && !_writingHex) _hexDirty = true; };
        _customColor.LostFocus += async (_, _) => { if (!_refreshing && CommitHexColor()) await FlushPendingAsync(); };
        _visibility.Click += (_, _) => _coordinator.ToggleVisibility();
    }

    WidgetPreferences CurrentPreferences => _pending ?? _inFlight ?? _coordinator.Preferences;

    bool CommitHexColor()
    {
        if (!_hexDirty) return false;
        string hex = _customColor.Text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
        {
            _colorFeedback.Text = "请输入六位 RGB，例如 #202329。"; _colorFeedback.Visibility = Visibility.Visible; return false;
        }
        _hexDirty = false; _writingHex = true;
        try { _customColor.Text = $"#{rgb:X6}"; }
        finally { _writingHex = false; }
        SetCompanionAppearance(0xFF000000 | rgb, CurrentPreferences.CompanionAppearanceId); return true;
    }

    void SetCompanionAppearance(uint color, string id)
    {
        color |= 0xFF000000; id = CompanionAppearanceIds.Normalize(id);
        _appearanceRevision++;
        if (!_hexDirty) _colorFeedback.Visibility = Visibility.Collapsed;
        // Live preview and persisted preferences have independent cadences. Pending always holds the latest valid value.
        _coordinator.PreviewCompanionAppearance(color, id);
        QueuePreferences(CurrentPreferences with { CompanionColor = color, CompanionAppearanceId = id });
    }

    void RefreshCompanionPreview(uint color, string appearanceId)
    {
        if (_previewAppearance == (color, appearanceId)) return;
        _previewAppearance = (color, appearanceId);
        var points = CompanionContour.GetPoints(appearanceId, CompanionShapePose.Rounded);
        var figure = new PathFigure { IsClosed = true, IsFilled = true,
            StartPoint = new Windows.Foundation.Point(12 + points[0].X * 3, 16 + points[0].Y * 3) };
        for (int index = 1; index < points.Count; index++)
            figure.Segments.Add(new LineSegment { Point = new(12 + points[index].X * 3, 16 + points[index].Y * 3) });
        var geometry = new PathGeometry(); geometry.Figures.Add(figure);
        // The sample represents the chosen RGB. Theme-role substitution would map
        // both the body and its eyes to WindowText in high contrast.
        _companionPreview.Data = geometry; _companionPreview.Fill = ColorBrush(color);
        _companionPreview.Stroke = ColorBrush(color == 0xFFFFFFFF ? 0xFFB9C1C8 : color);
        double r = ((color >> 16) & 255) / 255d, g = ((color >> 8) & 255) / 255d, b = (color & 255) / 255d;
        uint eye = .2126 * r * r + .7152 * g * g + .0722 * b * b > .20 ? 0xFF202329 : 0xFFFFFFFF;
        _previewLeftEye.Fill = _previewRightEye.Fill = ColorBrush(eye);
    }

    void QueuePreferences(WidgetPreferences preferences)
    {
        if (_disposed) return;
        _pending = preferences.Validated();
        _saveHint.Text = "正在保存…";
        _pendingAppearanceRevision = _appearanceRevision;
        Refresh();
        _changeTimer.Stop();
        _changeTimer.Start();
    }

    async Task FlushPreferencesAsync()
    {
        if (_disposed || _updating || _pending is null) return;
        var requested = _pending;
        long requestedAppearanceRevision = _pendingAppearanceRevision;
        _pending = null;
        _updating = true;
        _inFlight = requested;
        _updateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await _coordinator.UpdatePreferencesAsync(requested);
            LastSaveFailed = !result.Success;
            _saveHint.Text = result.Success ? "已保存至本机" : "更改尚未保存，请检查提示";
            if (!result.Success)
            {
                if (_pending is not null) _pending = RebasePending(_pending, requested, _coordinator.Preferences,
                    _pendingAppearanceRevision > requestedAppearanceRevision);
                ShowFeedback(result.Message ?? "设置未能保存，请重试。", error: true);
            }
            else if (!string.IsNullOrWhiteSpace(result.Message)) ShowFeedback(result.Message);
        }
        catch (Exception)
        {
            LastSaveFailed = true;
            _saveHint.Text = "更改尚未保存，请检查提示";
            if (_pending is not null) _pending = RebasePending(_pending, requested, _coordinator.Preferences,
                _pendingAppearanceRevision > requestedAppearanceRevision);
            ShowFeedback("设置未能保存，请重试。", error: true);
        }
        finally
        {
            _updating = false;
            _inFlight = null;
            _updateCompletion?.TrySetResult();
            if (!_disposed)
            {
                if (_pending is not null) _saveHint.Text = "正在保存…";
                // A coordinator save can clear its temporary preview. Restore the
                // newer pending choice before the next presentation frame.
                if (_pending is { } pending && _pendingAppearanceRevision > requestedAppearanceRevision)
                    _coordinator.PreviewCompanionAppearance(pending.CompanionColor, pending.CompanionAppearanceId);
                Refresh();
                if (_pending is not null) _changeTimer.Start();
            }
        }
    }

    public async Task FlushPendingAsync()
    {
        _changeTimer.Stop();
        while (!_disposed)
        {
            if (_updating && _updateCompletion is { } completion) { await completion.Task; continue; }
            if (_pending is null) return;
            await FlushPreferencesAsync();
        }
    }

    static WidgetPreferences RebasePending(WidgetPreferences pending, WidgetPreferences requested, WidgetPreferences actual,
        bool preservePendingAppearance)
        => actual with
        {
            Blur = pending.Blur != requested.Blur ? pending.Blur : actual.Blur,
            TintOpacity = pending.TintOpacity != requested.TintOpacity ? pending.TintOpacity : actual.TintOpacity,
            ReducedMotion = pending.ReducedMotion != requested.ReducedMotion ? pending.ReducedMotion : actual.ReducedMotion,
            ClockShowTime = pending.ClockShowTime != requested.ClockShowTime ? pending.ClockShowTime : actual.ClockShowTime,
            ClockShowDate = pending.ClockShowDate != requested.ClockShowDate ? pending.ClockShowDate : actual.ClockShowDate,
            ClockShowWeekday = pending.ClockShowWeekday != requested.ClockShowWeekday ? pending.ClockShowWeekday : actual.ClockShowWeekday,
            ClockFontId = pending.ClockFontId != requested.ClockFontId ? pending.ClockFontId : actual.ClockFontId,
            TextInk = pending.TextInk != requested.TextInk ? pending.TextInk : actual.TextInk,
            AudioVisualizationEnabled = pending.AudioVisualizationEnabled != requested.AudioVisualizationEnabled ? pending.AudioVisualizationEnabled : actual.AudioVisualizationEnabled,
            CompanionEnabled = pending.CompanionEnabled != requested.CompanionEnabled ? pending.CompanionEnabled : actual.CompanionEnabled,
            CompanionFollowMouse = pending.CompanionFollowMouse != requested.CompanionFollowMouse ? pending.CompanionFollowMouse : actual.CompanionFollowMouse,
            CompanionColor = preservePendingAppearance || pending.CompanionColor != requested.CompanionColor ? pending.CompanionColor : actual.CompanionColor,
            CompanionAppearanceId = preservePendingAppearance || pending.CompanionAppearanceId != requested.CompanionAppearanceId ? pending.CompanionAppearanceId : actual.CompanionAppearanceId,
            LiquidGlassEnabled = pending.LiquidGlassEnabled != requested.LiquidGlassEnabled ? pending.LiquidGlassEnabled : actual.LiquidGlassEnabled,
            HoverDelayMs = pending.HoverDelayMs != requested.HoverDelayMs ? pending.HoverDelayMs : actual.HoverDelayMs,
            LeaveDelayMs = pending.LeaveDelayMs != requested.LeaveDelayMs ? pending.LeaveDelayMs : actual.LeaveDelayMs,
            AvoidFullscreen = pending.AvoidFullscreen != requested.AvoidFullscreen ? pending.AvoidFullscreen : actual.AvoidFullscreen,
            RunAtStartup = pending.RunAtStartup != requested.RunAtStartup ? pending.RunAtStartup : actual.RunAtStartup,
            HotkeyKey = pending.HotkeyKey != requested.HotkeyKey ? pending.HotkeyKey : actual.HotkeyKey,
            HotkeyModifiers = pending.HotkeyModifiers != requested.HotkeyModifiers ? pending.HotkeyModifiers : actual.HotkeyModifiers,
            OnboardingSeen = pending.OnboardingSeen != requested.OnboardingSeen ? pending.OnboardingSeen : actual.OnboardingSeen
        };

    void CaptureHotkey(object sender, KeyRoutedEventArgs args)
    {
        if (_coordinator.IsUiPreview) return;
        if (args.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.Menu or
            VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
        if (args.Key == VirtualKey.Tab) return;
        args.Handled = true;
        if (args.Key == VirtualKey.Escape) { _hotkeyHint.Text = "点击输入框后按组合键，例如 Ctrl + Shift + G。"; return; }
        uint modifiers = (Down(VirtualKey.Menu) ? 1u : 0u) | (Down(VirtualKey.Control) ? 2u : 0u) |
            (Down(VirtualKey.Shift) ? 4u : 0u) | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? 8u : 0u);
        if ((modifiers & 11) == 0)
        {
            _hotkeyHint.Text = "请包含 Ctrl、Alt 或 Windows 键，再配合字母、数字或功能键。";
            return;
        }
        _hotkeyHint.Text = "若组合键被其他程序占用，仍保留原绑定。";
        QueuePreferences(CurrentPreferences with { HotkeyModifiers = modifiers, HotkeyKey = (uint)args.Key });
    }

    void Refresh()
    {
        if (_disposed || _minimalContent) return;
        _refreshing = true;
        try
        {
            RefreshMaterialControls();
            var preference = CurrentPreferences;
            _blur.Value = preference.Blur;
            _transparency.Value = Math.Round((1 - preference.TintOpacity) * 100);
            _hover.Value = preference.HoverDelayMs; _leave.Value = preference.LeaveDelayMs;
            _reducedMotion.IsOn = preference.ReducedMotion;
            _clockTime.IsOn = preference.ClockShowTime; _clockDate.IsOn = preference.ClockShowDate; _clockWeekday.IsOn = preference.ClockShowWeekday;
            foreach (var choice in _clockFontChoices)
            {
                bool selected = choice.Id == ClockFontCatalog.Normalize(preference.ClockFontId);
                choice.Button.IsChecked = selected;
                choice.Button.BorderBrush = selected ? Brush(AccentFill) : Brush(BorderColor);
                choice.Button.Background = selected ? Brush(0xFFEAF3FF) : Brush(ButtonColor);
            }
            foreach (var choice in _textInkChoices)
            {
                bool selected = choice.Ink == preference.TextInk;
                choice.Button.IsChecked = selected;
                choice.Button.BorderBrush = selected ? Brush(AccentFill) : Brush(BorderColor);
                choice.Button.Background = selected ? Brush(0xFFEAF3FF) : Brush(ButtonColor);
            }
            // These remain persisted legacy material preferences. The selected
            // system compositor lens does not use them to render its glass.
            _audio.IsOn = preference.AudioVisualizationEnabled;
            _companion.IsOn = preference.CompanionEnabled;
            _companionFollowMouse.IsOn = preference.CompanionFollowMouse;
            _liquidGlass.IsOn = preference.LiquidGlassEnabled;
            RefreshRuntimeStatus();
            if (_customColor.FocusState == FocusState.Unfocused && !_hexDirty)
            {
                _customColor.Text = $"#{preference.CompanionColor & 0x00FFFFFF:X6}";
                _hexDirty = false;
            }
            var companionColor = Color.FromArgb(255, (byte)(preference.CompanionColor >> 16), (byte)(preference.CompanionColor >> 8), (byte)preference.CompanionColor);
            if (_colorPicker.Color != companionColor) _colorPicker.Color = companionColor;
            _companionForm.SelectedIndex = preference.CompanionAppearanceId == CompanionAppearanceIds.CatEar ? 1 : preference.CompanionAppearanceId == CompanionAppearanceIds.Devil ? 2 : 0;
            for (int index = 0; index < _appearanceChoices.Count; index++)
            {
                bool selected = index == _companionForm.SelectedIndex;
                _appearanceChoices[index].IsChecked = selected;
                _appearanceChoices[index].Background = selected ? Brush(0xFFEAF3FF) : Brush(ButtonColor);
                _appearanceChoices[index].BorderBrush = selected ? Brush(AccentFill) : Brush(BorderColor);
            }
            foreach (var (color, button) in _colorPresets)
                button.BorderBrush = color == preference.CompanionColor ? Brush(AccentFill) : Brush(0x00FFFFFF);
            _companionFollowMouse.IsEnabled = preference.CompanionEnabled;
            _capsuleLayoutPreview?.Present(preference, DateTimeOffset.Now);
            _clockLayoutPreview?.Present(preference, DateTimeOffset.Now);
            _liquidPreview?.Present(preference);
            foreach (var updateSwitch in _switchRefresh) updateSwitch();
            RefreshCompanionPreview(preference.CompanionColor, preference.CompanionAppearanceId);
            _fullscreen.IsOn = preference.AvoidFullscreen; _startup.IsOn = preference.RunAtStartup;
            _blurLabel.Text = $"{preference.Blur:0}";
            _transparencyLabel.Text = $"{(1 - preference.TintOpacity) * 100:0}%";
            _hoverLabel.Text = $"{preference.HoverDelayMs} ms";
            _leaveLabel.Text = $"{preference.LeaveDelayMs} ms";
            _hotkey.Text = FormatHotkey(preference.HotkeyModifiers, preference.HotkeyKey);
            _onboarding.Visibility = preference.OnboardingSeen ? Visibility.Collapsed : Visibility.Visible;
            string visibilityAction = _coordinator.IsWidgetVisible ? "隐藏组件" : "显示组件";
            _visibilityText.Text = visibilityAction;
            Name(_visibility, visibilityAction);
            ToolTipService.SetToolTip(_visibility, visibilityAction);
            var media = _coordinator.Media;
            _connection.Text = media.Connected ? "已连接网易云音乐" : "等待音乐播放";
            var controls = new List<string>();
            if (media.Capabilities.Previous) controls.Add("上一首");
            if (media.Capabilities.Toggle) controls.Add("播放 / 暂停");
            if (media.Capabilities.Next) controls.Add("下一首");
            if (media.Capabilities.Seek && media.Timeline?.Valid == true) controls.Add("进度跳转");
            _capabilities.Text = media.Connected
                ? (controls.Count == 0 ? "当前音乐来源暂不支持播放控制。" : "支持" + string.Join("、", controls) + "。") +
                    (media.Timeline?.Valid != true ? "播放时间暂不可用。" : "")
                : "在网易云音乐中播放歌曲，组件会自动连接。";
            _status.Text = _coordinator.ConnectionIssue ?? "";
            _status.Visibility = string.IsNullOrWhiteSpace(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
            _connectionHelp.Visibility = media.Connected && string.IsNullOrWhiteSpace(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
            RefreshPlugins();
        }
        finally { _refreshing = false; }
    }

    public static string FormatHotkey(uint modifiers, uint key)
    {
        if (key == 0) return "未绑定";
        var pieces = new List<string>();
        if ((modifiers & 2) != 0) pieces.Add("Ctrl");
        if ((modifiers & 1) != 0) pieces.Add("Alt");
        if ((modifiers & 4) != 0) pieces.Add("Shift");
        if ((modifiers & 8) != 0) pieces.Add("Win");
        pieces.Add(key switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
            0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc",
            _ => ((VirtualKey)key).ToString()
        });
        return string.Join(" + ", pieces);
    }

    void ReportResult(SettingsUpdateResult result)
    {
        _pluginsKey = null;
        ShowFeedback(result.Message ?? (result.Success ? "已更新。" : "操作未完成，请重试。"), !result.Success);
        Refresh();
    }

    void RefreshRuntimeStatus()
    {
        if (_disposed || _minimalContent) return;
        if (_navigation?.SelectedItem is NavigationViewItem { Tag: int selected })
        {
            if (selected == 0) _capsuleLayoutPreview?.Present(CurrentPreferences, DateTimeOffset.Now);
            if (selected == TimePage) _clockLayoutPreview?.Present(CurrentPreferences, DateTimeOffset.Now);
            if (selected == 2) _liquidPreview?.Present(CurrentPreferences);
        }
        // Native capture advances independently of preference/scene Changed events.
        // Read its live state and map the raw native wording into user-facing
        // Chinese here; native strings stay untouched.
        try
        {
            string liquid = LocalizeMaterialStatus(_coordinator.LiquidGlassStatus);
            if (_liquidGlassStatus.Text != liquid) _liquidGlassStatus.Text = liquid;
        }
        catch
        {
            const string unavailable = "材质状态暂不可用，请稍后重试。";
            if (_liquidGlassStatus.Text != unavailable) _liquidGlassStatus.Text = unavailable;
        }
        string audio = LocalizeAudioStatus(_coordinator.AudioStatus);
        if (_audioStatus.Text != audio) _audioStatus.Text = audio;
    }

    /// <summary>The native material reports engineering wording; keep it out of the settings UI.</summary>
    internal static string LocalizeMaterialStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return status;
        if (status.StartsWith("Disabled", StringComparison.Ordinal))
            return status.Contains("affinity",StringComparison.OrdinalIgnoreCase) ? "未启用（窗口捕获排除设置恢复失败）" : "未启用";
        if(status.StartsWith("Tripped:",StringComparison.Ordinal))return "已熔断：材质停止，禁止自动图形重试";
        if(status.StartsWith("Experimental:",StringComparison.Ordinal))return "实验：仅折射本地静态壁纸，不折射其他应用窗口";
        if(status.StartsWith("Active:",StringComparison.Ordinal))return "已启用：系统磨砂与曲面光照，无桌面捕获";
        if (status.StartsWith("Starting", StringComparison.Ordinal)) return "正在启动";
        if (status.StartsWith("Running", StringComparison.Ordinal)) return "已启用";
        if (status.StartsWith("Paused", StringComparison.Ordinal)) return "已暂停：组件隐藏时保留启用意图";
        if (status.StartsWith("Fallback:", StringComparison.Ordinal))
            return "已回退：" + (status["Fallback:".Length..].Trim() switch
            {
                "solid/high-contrast policy; enabled intent retained" => "高对比度或实色策略；已保留启用意图",
                "D3D material device unavailable; enabled intent retained" => "D3D 材质设备不可用；已保留启用意图",
                "solid; wallpaper candidate unavailable or outside capsule/peek" => "静态壁纸不可用或当前不在胶囊/小展开，使用纯色材质",
                "solid backend; no capture" => "纯色宿主，不读取桌面",
                var other => other
            });
        if (status.StartsWith("QA", StringComparison.Ordinal)) return "已启用（QA 快照）";
        return status;
    }

    /// <summary>Audio capture states have no localised reason in the coordinator yet.</summary>
    internal static string LocalizeAudioStatus(string status) => status switch
    {
        "Inactive" => "未启用",
        "Starting" => "正在启动",
        "Capturing" => "采集中",
        "Silent" => "静音中",
        "Unavailable" => "暂不可用",
        "Faulted" => "已停止",
        _ => status
    };

    void RefreshPlugins()
    {
        var statuses = _coordinator.Plugins;
        var pages = _coordinator.PluginPages;
        string key = JsonSerializer.Serialize(new { statuses, pages });
        if (_pluginsKey == key) return;
        _pluginsKey = key;
        _pluginList.Children.Clear(); _pluginPages.Children.Clear();
        foreach (var status in statuses)
        {
            var panel = new StackPanel { Spacing = 8, Padding = new Thickness(0) };
            var heading = new Grid { ColumnSpacing = 16 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { Spacing = 4 };
            labels.Children.Add(SettingTitle(status.Name));
            labels.Children.Add(Description($"{status.Version} · {PluginStateLabel(status.State)}"));
            heading.Children.Add(labels);
            if (!status.BuiltIn)
            {
                var enabled = new ToggleSwitch { IsOn = status.Enabled, OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
                Name(enabled, $"启用 {status.Name}");
                enabled.Toggled += async (_, _) =>
                {
                    enabled.IsEnabled = false;
                    try { await FlushPendingAsync(); ReportResult(await _coordinator.SetPluginEnabledAsync(status.Id, enabled.IsOn)); }
                    catch { ShowFeedback("插件状态更新失败。", error: true); }
                    finally { enabled.IsEnabled = true; }
                };
                Grid.SetColumn(enabled, 1); heading.Children.Add(enabled); WrapActions(heading, labels, enabled);
            }
            panel.Children.Add(heading);
            panel.Children.Add(Description(status.ResourceDescription));
            if (!string.IsNullOrWhiteSpace(status.Reason)) panel.Children.Add(Description(status.Reason));
            foreach (var setting in _coordinator.GetPluginSettings(status.Id))
                panel.Children.Add(PluginSettingRow(status.Id, setting));
            if (!status.BuiltIn)
            {
                var remove = CommandButton("移除插件", "\uE74D");
                remove.Click += async (_, _) =>
                {
                    remove.IsEnabled = false;
                    try { await FlushPendingAsync(); ReportResult(await _coordinator.RemovePluginAsync(status.Id)); }
                    catch { ShowFeedback("插件移除失败。", error: true); }
                    finally { remove.IsEnabled = true; }
                };
                panel.Children.Add(remove);
            }
            _pluginList.Children.Add(Surface(panel));
        }
        if (pages.Count > 0) _pluginPages.Children.Add(SettingTitle("插件工具"));
        foreach (var page in pages)
        {
            var panel = new StackPanel { Spacing = 12, Padding = new Thickness(0) };
            panel.Children.Add(SettingTitle(page.Name));
            foreach (var item in page.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.CommandId))
                {
                    var command = CommandButton(item.Label, "\uE768");
                    command.Click += async (_, _) =>
                    {
                        int slash = item.CommandId.IndexOf('/');
                        if (slash <= 0 || slash == item.CommandId.Length - 1) { ShowFeedback("插件操作标识无效。", error: true); return; }
                        command.IsEnabled = false;
                        try { ReportResult(await _coordinator.ExecutePluginCommandAsync(item.CommandId[..slash], item.CommandId[(slash + 1)..])); }
                        catch { ShowFeedback("插件操作未能完成。", error: true); }
                        finally { command.IsEnabled = true; }
                    };
                    panel.Children.Add(command);
                    if (!string.IsNullOrWhiteSpace(item.Value)) panel.Children.Add(Description(item.Value));
                }
                else
                {
                    var text = new StackPanel { Spacing = 4 };
                    text.Children.Add(Label(item.Label));
                    if (!string.IsNullOrWhiteSpace(item.Value)) text.Children.Add(Description(item.Value));
                    panel.Children.Add(text);
                }
            }
            _pluginPages.Children.Add(Surface(panel));
        }
    }

    FrameworkElement PluginSettingRow(string pluginId, PluginSetting setting)
    {
        var configuration = _coordinator.GetPluginConfiguration(pluginId);
        string value = configuration is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty(setting.Id, out var found)
            ? found.ToString() : setting.DefaultValue;
        if (setting.Kind == PluginSettingKind.Boolean)
        {
            var toggle = new ToggleSwitch { IsOn = bool.TryParse(value, out var on) && on, OnContent = "", OffContent = "", MinWidth = 0, Width = 52 };
            Name(toggle, setting.Name);
            toggle.Toggled += async (_, _) => await SavePluginValueAsync(pluginId, setting.Id, JsonSerializer.SerializeToElement(toggle.IsOn));
            return ToggleRow(setting.Name, toggle, setting.Description);
        }
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(SettingTitle(setting.Name));
        if (!string.IsNullOrWhiteSpace(setting.Description)) panel.Children.Add(Description(setting.Description));
        var input = new TextBox { Text = value, MinWidth = 0, MinHeight = 40, CornerRadius = new CornerRadius(UiRadius.Control),
            Background = Brush(ButtonColor), BorderBrush = Brush(BorderColor), HorizontalAlignment = HorizontalAlignment.Stretch };
        SettingsTypography.Apply(input, TextRole.Body, InkRole.Secondary);
        Name(input, setting.Name);
        var save = CommandButton("保存", "\uE74E");
        save.Click += async (_, _) =>
        {
            JsonElement next;
            if (setting.Kind == PluginSettingKind.Number)
            {
                if (!double.TryParse(input.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var number) || !double.IsFinite(number))
                { ShowFeedback("请输入有效数字。", error: true); return; }
                next = JsonSerializer.SerializeToElement(number);
            }
            else next = JsonSerializer.SerializeToElement(input.Text);
            save.IsEnabled = false;
            try { await SavePluginValueAsync(pluginId, setting.Id, next); }
            finally { save.IsEnabled = true; }
        };
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Children.Add(input); Grid.SetColumn(save, 1); controls.Children.Add(save); WrapActions(controls, input, save);
        panel.Children.Add(controls); return panel;
    }

    async Task SavePluginValueAsync(string pluginId, string settingId, JsonElement value)
    {
        try
        {
            var current = _coordinator.GetPluginConfiguration(pluginId);
            var properties = current is { ValueKind: JsonValueKind.Object }
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.Value.GetRawText()) ?? new()
                : new Dictionary<string, JsonElement>();
            properties[settingId] = value;
            ReportResult(await _coordinator.SavePluginConfigurationAsync(pluginId, JsonSerializer.SerializeToElement(properties)));
        }
        catch { ShowFeedback("插件设置未能保存。", error: true); }
    }

    static string PluginStateLabel(PluginRunState state) => state switch
    {
        PluginRunState.Running => "运行中", PluginRunState.Paused => "已暂停", PluginRunState.Disabled => "已关闭",
        PluginRunState.Starting => "启动中", PluginRunState.Faulted => "已回退", PluginRunState.RestartRequired => "需要重启", _ => "已停止"
    };

    /// <summary>Star text/Auto action rows become two rows before their controls can exceed the viewport.</summary>
    static void WrapActions(Grid row, FrameworkElement first, FrameworkElement action)
    {
        row.RowSpacing = 8;
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Arrange(double width)
        {
            bool narrow = width < 280 * Math.Max(1, SystemColors.TextScaleFactor);
            Grid.SetRow(action, narrow ? 1 : 0); Grid.SetColumn(action, narrow ? 0 : 1);
            Grid.SetColumnSpan(first, narrow ? 2 : 1);
            action.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
        row.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        row.Loaded += (_, _) => Arrange(row.ActualWidth);
    }

    void ConfigureGlassControls(Grid root)
    {
        // Keep standard WinUI focus/input behavior; only material/state tokens change.
        if (!HighContrast.HighContrast)
        {
            // Pointer states must differ: a single fill made every button look dead.
            root.Resources["ButtonBackground"] = Brush(ButtonColor);
            root.Resources["ButtonBackgroundPointerOver"] = Brush(ButtonPointerOverColor);
            root.Resources["ButtonBackgroundPressed"] = Brush(ButtonPressedColor);
            root.Resources["ButtonBackgroundDisabled"] = Brush(ButtonDisabledColor);
            // The fixed material is nearly white; an off switch needs an ink slot
            // to stay visible, unlike the old grey acrylic it was tuned for.
            foreach (string key in new[] { "ToggleSwitchFillOff", "ToggleSwitchFillOffPointerOver", "ToggleSwitchFillOffPressed" })
                root.Resources[key] = Brush(0x1620252C);
            foreach (string key in new[] { "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" })
                root.Resources[key] = Brush(0xE6FFFFFF);
            foreach (string key in new[] { "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed", "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed" })
                root.Resources[key] = Brush(AccentFill);
            var accent = Color.FromArgb(255, 0x16, 0x89, 0xF9);
            foreach (string key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
                root.Resources[key] = accent;
            // Keyboard-only focus ring: 2px accent.fill around a 1px light separator.
            root.Resources["FocusVisualPrimaryBrush"] = Brush(AccentFill);
            root.Resources["FocusVisualSecondaryBrush"] = Brush(0xFFFFFFFF);
            root.Resources["FocusVisualPrimaryThickness"] = new Thickness(2);
            root.Resources["FocusVisualSecondaryThickness"] = new Thickness(1);
        }
        foreach (var toggle in new[] { _reducedMotion, _fullscreen, _startup, _audio, _companion, _companionFollowMouse, _liquidGlass, _clockTime, _clockDate, _clockWeekday }) toggle.MinHeight = 44;
        foreach (var input in new[] { _hotkey, _customColor, _diagnosticPath })
        {
            SettingsTypography.Apply(input, input == _diagnosticPath ? TextRole.CaptionSettings : TextRole.Body, InkRole.Secondary);
            input.MinHeight = 40; input.CornerRadius = new CornerRadius(8);
            input.Background = Brush(ButtonColor); input.BorderBrush = Brush(BorderColor);
            input.Padding = new Thickness(14, 8, 14, 8);
        }
        SettingsTypography.Apply(_companionForm, TextRole.Label, InkRole.Primary);
        _companionForm.CornerRadius = new CornerRadius(UiRadius.Control);
        SettingsTypography.ApplyInfoBar(_feedback);
    }

    void ShowFeedback(string message, bool error = false)
    {
        if (_disposed) return;
        _feedback.Title = error ? "暂未完成" : "已更新";
        _feedback.Message = message;
        // Success uses the quiet informational bar and disappears after 2.4 s;
        // only actionable errors stay until the user closes them.
        _feedback.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        _feedback.IsOpen = true;
        _feedbackTimer.Stop();
        if (!error) _feedbackTimer.Start();
    }

    /// <summary>
    /// Rounded well around the oversized template colour picker. Keep this to the
    /// XAML clip only: a composition geometric clip on the picker's backing visual
    /// crashed XAML (E_NOINTERFACE fail-fast) when the window was captured during a
    /// page transition, so the material must stay inside the XAML layer.
    /// </summary>
    void ClipColorPicker(Windows.Foundation.Size size)
    {
        _colorPickerClip.Rect = new Windows.Foundation.Rect(0, 0, size.Width, size.Height);
    }

    void ResizeToWorkArea()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)Math.Round(960 * scale), Math.Max(1, area.Width - (int)Math.Round(24 * scale)));
        int height = Math.Min((int)Math.Round(700 * scale), Math.Max(1, area.Height - (int)Math.Round(24 * scale)));
        AppWindow.Resize(new(width, height));
        AppWindow.Move(new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2));
    }

    /// <summary>
    /// One settings page. The page name now lives in the window header, so the
    /// page keeps its single-line description; the deck owner adds the bottom
    /// inset that guarantees the 24 DIP scroll tail.
    /// </summary>
    static StackPanel Section(string? description = null)
    {
        var panel = new StackPanel { Spacing = 16 };
        if (!string.IsNullOrEmpty(description)) panel.Children.Add(Description(description));
        return panel;
    }

    static Border Group(params FrameworkElement[] rows)
    {
        var panel = new StackPanel();
        for (int index = 0; index < rows.Length; index++)
        {
            if (index > 0) panel.Children.Add(new Border { Height = 1, Background = Brush(BorderColor), Margin = new Thickness(16, 0, 16, 0) });
            panel.Children.Add(rows[index]);
        }
        return Surface(panel);
    }

    static Border Surface(UIElement child) => new()
    {
        Child = child, BorderBrush = Brush(BorderColor), Background = Brush(SurfaceColor),
        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 4, 16, 4)
    };

    static Grid SliderRow(string title, TextBlock value, Slider slider, string? description = null)
    {
        var row = new Grid { ColumnSpacing = 12, RowSpacing = 4, MinHeight = 60, Padding = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(SettingTitle(title));
        if (description is not null) labels.Children.Add(Description(description));
        row.Children.Add(labels);
        SettingsTypography.Apply(value, TextRole.Body, InkRole.Secondary, TextFlags.TabularDigitsOnly);
        value.MinWidth = 64;
        value.TextAlignment = TextAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2); row.Children.Add(value);
        slider.MinHeight = 32; slider.VerticalAlignment = VerticalAlignment.Center;
        slider.IsThumbToolTipEnabled = false;
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        void Arrange(double width)
        {
            bool narrow = width < 440 * Math.Max(1, SystemColors.TextScaleFactor);
            row.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : Math.Clamp(width * .34, 150, 220));
            Grid.SetColumnSpan(labels, narrow ? 3 : 1);
            Grid.SetRow(slider, narrow ? 1 : 0); Grid.SetColumn(slider, narrow ? 0 : 1);
            Grid.SetColumnSpan(slider, narrow ? 2 : 1); Grid.SetRow(value, narrow ? 1 : 0);
        }
        row.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        row.Loaded += (_, _) => Arrange(row.ActualWidth);
        return row;
    }

    Grid ToggleRow(string title, ToggleSwitch toggle, string? description = null)
    {
        var row = new Grid { ColumnSpacing = 16, MinHeight = 56, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(SettingTitle(title));
        if (description is not null) labels.Children.Add(Description(description));
        row.Children.Add(labels);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        var switchHost = CompanionSwitch(toggle);
        Grid.SetColumn(switchHost, 1); row.Children.Add(switchHost);
        return row;
    }

    static Button CommandButton(string title, string glyph)
    {
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var label = SettingsTypography.Apply(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center }, TextRole.Label, InkRole.Primary);
        Grid.SetColumn(label, 1); content.Children.Add(label);
        var button = new Button
        {
            Content = content, CornerRadius = new CornerRadius(8), MinHeight = 36,
            Background = Brush(ButtonColor), BorderBrush = Brush(BorderColor), Foreground = Brush(InkColor),
            Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left
        };
        Name(button, title);
        ToolTipService.SetToolTip(button, title);
        return button;
    }

    static Border Separator() => new() { Height = 1, Background = Brush(BorderColor), Margin = new Thickness(0, 2, 0, 2) };
    static TextBlock Label(string text = "") => SettingsTypography.Apply(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, TextRole.Body, InkRole.Secondary);
    static TextBlock SettingTitle(string text) => SettingsTypography.Apply(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, TextRole.Label, InkRole.Primary);
    // Ink 2 is the conservative readable caption tier until the final composed
    // acrylic/background contrast matrix establishes that Ink 3 is safe.
    static TextBlock Description(string text = "") => SettingsTypography.Apply(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, TextRole.CaptionSettings, InkRole.Secondary);
    static SolidColorBrush Brush(uint argb)
    {
        if (HighContrast.HighContrast)
        {
            var role = argb switch
            {
                CanvasColor or FallbackCanvasColor or SidebarColor or SurfaceColor or 0x121689F9 or 0xFFEAF3FF or ButtonColor or ButtonPointerOverColor
                    or ButtonPressedColor or ButtonDisabledColor or 0xFFEFF6F4 or 0xFFF6F8FB => UIElementType.Window,
                0xFF28786E or AccentFill => UIElementType.Highlight,
                _ => UIElementType.WindowText
            };
            return new(SystemColors.UIElementColor(role));
        }
        return ColorBrush(argb);
    }
    static SolidColorBrush ColorBrush(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    static void Name(DependencyObject control, string name) => AutomationProperties.SetName(control, name);
    static bool Down(VirtualKey key) => (GetKeyState((int)key) & 0x8000) != 0;
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);

    public void CloseForShutdown()
    {
        if (_disposed) return;
        _shutdown = true;
        _changeTimer.Stop();
        _shown = false;
        _statusTimer.Stop();
        Close();
        Detach();
    }

    void Detach()
    {
        if (_disposed) return;
        _disposed = true;
        _shown = false;
        _liquidPreview?.Dispose();
        _deck?.StopTransition();
        if (_navigation is not null) _navigation.SelectionChanged -= OnNavigationSelectionChanged;
        _changeTimer.Stop();
        _changeTimer.Tick -= OnChangeTimerTick;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _feedbackTimer.Stop();
        _feedbackTimer.Tick -= OnFeedbackTimerTick;
        _coordinator.Changed -= Refresh;
        AppWindow.Closing -= OnAppWindowClosing;
        Closed -= OnWindowClosed;
        DetachContentTrace();
    }

    public void Dispose() => CloseForShutdown();
}
