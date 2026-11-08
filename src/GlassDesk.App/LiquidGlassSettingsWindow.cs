using GlassDesk.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace GlassDesk.App;

/// <summary>Independent live material editor for the GPU liquid-glass renderer.</summary>
internal sealed class LiquidGlassSettingsWindow : Window, IDisposable
{
    readonly ISettingsCoordinator _coordinator;
    readonly DispatcherQueueTimer _saveTimer;
    readonly TextBlock _saveState = new() { Text = "更改会自动保存", Opacity = .72 };
    readonly RadioButtons _materialTab = new() { MaxColumns = 2 };
    Border? _lightCard, _darkCard;
    readonly StackPanel _lightPanel = new() { Spacing = 8 };
    readonly StackPanel _darkPanel = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    readonly CheckBox _lensEnabled = new() { Content = "启用透镜折射", IsChecked = true };
    readonly CheckBox _autoDensity = new() { Content = "自动调节密度", IsChecked = true };
    readonly SliderRow _density;
    readonly SliderRow _threshold;
    readonly SliderRow _rim;
    readonly SliderRow _edge;
    readonly SliderRow _power;
    readonly SliderRow _dispersion;
    readonly SliderRow _centerDispersion;
    readonly ComboBox _theme = Choice("跟随系统", "浅色", "深色");
    readonly ComboBox _ink = Choice("跟随主题", "深色墨色", "浅色墨色");
    WidgetPreferences? _inFlight;
    WidgetPreferences? _pending;
    bool _refreshing;
    bool _saving;
    bool _disposed;

    public LiquidGlassSettingsWindow(ISettingsCoordinator coordinator)
    {
        _coordinator = coordinator;
        Title = "液态玻璃设置";
        AppWindow.Resize(new SizeInt32(780, 900));
        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(110);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += OnSaveTimer;

        var defaults = new LiquidGlassPreferences();
        _density = new("材质密度", 0, 1, .01, defaults.Density, value => SetMaterial(s => s with { Density = value }));
        _threshold = new("自适应阈值", .2, .8, .01, defaults.Threshold, value => SetMaterial(s => s with { Threshold = value }));
        _rim = new("曲面最大宽度（逻辑像素）", 4, 96, 1, defaults.RimMaxPixels, value => SetMaterial(s => s with { RimMaxPixels = value }));
        _edge = new("折射强度", 0, 4, .01, defaults.Edge, value => SetMaterial(s => s with { Edge = value }));
        _power = new("曲面衰减", 1, 6, .01, defaults.Power, value => SetMaterial(s => s with { Power = value }));
        _dispersion = new("边缘色散", 0, .4, .005, defaults.Dispersion, value => SetMaterial(s => s with { Dispersion = value }));
        _centerDispersion = new("中心色散", 0, .4, .005, defaults.CenterDispersion, value => SetMaterial(s => s with { CenterDispersion = value }));
        Content = BuildContent();
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        _coordinator.Changed += Refresh;
        Refresh();
    }

    static ComboBox Choice(params string[] labels)
    {
        var result = new ComboBox { MinWidth = 170 };
        foreach (string label in labels) result.Items.Add(new ComboBoxItem { Content = label });
        return result;
    }

    UIElement BuildContent()
    {
        var root = new Grid { Language = "zh-CN", RequestedTheme = ElementTheme.Light, Background = new SolidColorBrush(Color.FromArgb(255, 245, 246, 249)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Spacing = 6, Margin = new Thickness(26, 22, 26, 16) };
        header.Children.Add(new TextBlock { Text = "液态玻璃设置", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "材质、透镜与自适应参数会实时应用并保存。", Opacity = .7, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 0, 24, 20) };
        var content = new StackPanel { Spacing = 18 };
        content.Children.Add(Section("材质"));
        _materialTab.Items.Add("浅色玻璃");
        _materialTab.Items.Add("深色玻璃");
        _materialTab.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            bool dark = _materialTab.SelectedIndex == 1;
            _lightPanel.Visibility = dark ? Visibility.Collapsed : Visibility.Visible;
            _darkPanel.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
            if (_lightCard is not null) _lightCard.Visibility = _lightPanel.Visibility;
            if (_darkCard is not null) _darkCard.Visibility = _darkPanel.Visibility;
        };
        content.Children.Add(_materialTab);
        _lightCard = Card(_lightPanel);
        _darkCard = Card(_darkPanel);
        content.Children.Add(_lightCard);
        content.Children.Add(_darkCard);
        _lightPanel.Children.Add(BuildMaterialControls(false));
        _darkPanel.Children.Add(BuildMaterialControls(true));

        content.Children.Add(Section("透镜"));
        content.Children.Add(Card(_lensEnabled));
        var lens = new StackPanel { Spacing = 5 };
        foreach (var row in new[] { _rim, _edge, _power, _dispersion, _centerDispersion }) lens.Children.Add(row);
        content.Children.Add(Card(lens));

        content.Children.Add(Section("自适应"));
        _autoDensity.Checked += (_, _) => SetMaterial(s => s with { AutoDensity = true });
        _autoDensity.Unchecked += (_, _) => SetMaterial(s => s with { AutoDensity = false });
        content.Children.Add(Card(_autoDensity));
        var adaptive = new StackPanel { Spacing = 5 };
        adaptive.Children.Add(_density);
        adaptive.Children.Add(_threshold);
        content.Children.Add(Card(adaptive));

        content.Children.Add(Section("主题与墨色"));
        content.Children.Add(Card(ChoiceRow("主题", _theme), ChoiceRow("墨色", _ink)));
        var restore = new Button { Content = "恢复默认", MinHeight = 38, HorizontalAlignment = HorizontalAlignment.Left };
        restore.Click += (_, _) =>
        {
            var next = CurrentPreferences with { LiquidGlass = new LiquidGlassPreferences() };
            QueueSave(next);
            Refresh();
        };
        content.Children.Add(restore);
        scroll.Content = content;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 225, 227, 232)),
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12, 24, 14),
            Child = _saveState
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _lensEnabled.Checked += (_, _) => SetMaterial(s => s with { LensEnabled = true });
        _lensEnabled.Unchecked += (_, _) => SetMaterial(s => s with { LensEnabled = false });
        _theme.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _theme.SelectedIndex < 0) return;
            var theme = _theme.SelectedIndex switch { 1 => LiquidGlassTheme.Light, 2 => LiquidGlassTheme.Dark, _ => LiquidGlassTheme.FollowSystem };
            SetMaterial(s => s with { Theme = theme });
        };
        _ink.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _ink.SelectedIndex < 0) return;
            var ink = _ink.SelectedIndex switch { 1 => LiquidGlassInk.Dark, 2 => LiquidGlassInk.Light, _ => LiquidGlassInk.FollowTheme };
            SetMaterial(s => s with { Ink = ink });
        };
        AutomationProperties.SetName(_materialTab, "编辑材质");
        AutomationProperties.SetName(_lensEnabled, "启用透镜折射");
        AutomationProperties.SetName(_autoDensity, "自动密度");
        return root;
    }

    UIElement BuildMaterialControls(bool dark)
    {
        var current = dark ? new LiquidGlassPreferences().Dark : new LiquidGlassPreferences().Light;
        var material = new StackPanel { Spacing = 5 };
        var tintLabel = new TextBlock { Text = "染色颜色", Opacity = .8, Margin = new Thickness(2, 8, 2, 2) };
        var tintRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsColorChannelTextInputVisible = false,
            IsColorSliderVisible = false, IsHexInputVisible = false, MinHeight = 180, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var colorText = new TextBox { Width = 96, PlaceholderText = "#FFFFFF", VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(picker, "染色颜色选择");
        picker.Color = ParseTint(current.Tint);
        colorText.Text = current.Tint;
        picker.ColorChanged += (_, args) =>
        {
            if (_refreshing) return;
            colorText.Text = TintText(args.NewColor);
            UpdateTint(dark, colorText.Text);
        };
        colorText.LostFocus += (_, _) =>
        {
            if (!TryParseTint(colorText.Text, out var color)) { colorText.Text = dark ? CurrentPreferences.LiquidGlass.Dark.Tint : CurrentPreferences.LiquidGlass.Light.Tint; return; }
            picker.Color = color;
            UpdateTint(dark, TintText(color));
        };
        AutomationProperties.SetName(colorText, dark ? "深色玻璃染色颜色" : "浅色玻璃染色颜色");
        tintRow.Children.Add(picker);
        tintRow.Children.Add(colorText);
        material.Children.Add(tintLabel);
        material.Children.Add(tintRow);
        AddMaterialRow(material, dark, "染色强度", 0, 1.5, .01, current.Opacity, (v, m) => m with { Opacity = v });
        AddMaterialRow(material, dark, "薄层染色", 0, .8, .01, current.Thin, (v, m) => m with { Thin = v });
        AddMaterialRow(material, dark, "浓层染色", 0, .95, .01, current.Dense, (v, m) => m with { Dense = v });
        AddMaterialRow(material, dark, "模糊（逻辑像素）", 0, 20, .1, current.Blur, (v, m) => m with { Blur = v });
        AddMaterialRow(material, dark, "饱和度", 0, 3, .01, current.Saturation, (v, m) => m with { Saturation = v });
        AddMaterialRow(material, dark, "亮度", .4, 1.6, .01, current.Brightness, (v, m) => m with { Brightness = v });
        AddMaterialRow(material, dark, "对比度", .4, 1.6, .01, current.Contrast, (v, m) => m with { Contrast = v });
        AddMaterialRow(material, dark, "边缘高光", 0, 2, .01, current.RimLight, (v, m) => m with { RimLight = v });
        AddMaterialRow(material, dark, "曲面明暗", 0, 2.5, .01, current.Bevel, (v, m) => m with { Bevel = v });
        AddMaterialRow(material, dark, "阴影", 0, 3, .01, current.Shadow, (v, m) => m with { Shadow = v });
        return material;
    }

    void AddMaterialRow(StackPanel panel, bool dark, string label, double min, double max, double step, float initial,
        Func<float, LiquidGlassMaterialPreferences, LiquidGlassMaterialPreferences> update)
    {
        var row = new SliderRow(label, min, max, step, initial,
            value => SetMaterial(state => dark
                ? state with { Dark = update(value, state.Dark) }
                : state with { Light = update(value, state.Light) }));
        panel.Children.Add(row);
    }

    void UpdateTint(bool dark, string tint) => SetMaterial(state => dark
        ? state with { Dark = state.Dark with { Tint = tint } }
        : state with { Light = state.Light with { Tint = tint } });

    static Color ParseTint(string value)
    {
        uint rgb = uint.Parse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
    static string TintText(Color value) => $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    static bool TryParseTint(string value, out Color color)
    {
        try
        {
            string normalized = value.Trim();
            if (normalized.Length != 7 || normalized[0] != '#') { color = default; return false; }
            color = ParseTint(normalized.ToUpperInvariant());
            return true;
        }
        catch (FormatException) { color = default; return false; }
    }

    static UIElement ChoiceRow(string label, ComboBox choice)
    {
        var row = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(choice, 1);
        row.Children.Add(choice);
        return row;
    }

    static TextBlock Section(string title) => new() { Text = title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(2, 4, 2, 0) };
    static Border Card(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235)),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = child
    };
    static Border Card(UIElement first, UIElement second)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(first); stack.Children.Add(second);
        return Card(stack);
    }

    LiquidGlassPreferences CurrentGlass
        => (_pending?.LiquidGlass ?? _inFlight?.LiquidGlass ?? _coordinator.Preferences.LiquidGlass).Validated();
    WidgetPreferences CurrentPreferences => _coordinator.Preferences with { LiquidGlass = CurrentGlass };

    void SetMaterial(Func<LiquidGlassPreferences, LiquidGlassPreferences> update)
    {
        if (_refreshing || _disposed) return;
        QueueSave(CurrentPreferences with { LiquidGlass = update(CurrentGlass).Validated() });
    }

    void QueueSave(WidgetPreferences value)
    {
        _pending = value;
        _coordinator.PreviewLiquidGlass(value.LiquidGlass);
        _density.SetEnabled(!value.LiquidGlass.AutoDensity);
        _saveState.Text = "正在保存…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    async void OnSaveTimer(DispatcherQueueTimer sender, object args) => await SavePendingAsync();

    async Task SavePendingAsync()
    {
        if (_saving || _pending is null || _disposed) return;
        _saving = true;
        _saveTimer.Stop();
        try
        {
            while (_pending is not null)
            {
                var requested = _coordinator.Preferences with { LiquidGlass = _pending.LiquidGlass };
                _pending = null;
                _inFlight = requested;
                var result = await _coordinator.UpdatePreferencesAsync(requested);
                _inFlight = null;
                if (!result.Success)
                {
                    _saveState.Text = "设置未能保存，请重试";
                    if (_pending is null) Refresh();
                }
                else _saveState.Text = "已保存";
            }
        }
        catch { _saveState.Text = "设置未能保存"; }
        finally
        {
            _inFlight = null;
            _saving = false;
            if (_pending is not null && !_disposed) _saveTimer.Start();
        }
    }

    void Refresh()
    {
        if (_disposed) return;
        var value = CurrentGlass;
        _refreshing = true;
        try
        {
            if (_materialTab.SelectedIndex < 0) _materialTab.SelectedIndex = 0;
            bool showDark = _materialTab.SelectedIndex == 1;
            _lightPanel.Visibility = showDark ? Visibility.Collapsed : Visibility.Visible;
            _darkPanel.Visibility = showDark ? Visibility.Visible : Visibility.Collapsed;
            if (_lightCard is not null) _lightCard.Visibility = _lightPanel.Visibility;
            if (_darkCard is not null) _darkCard.Visibility = _darkPanel.Visibility;
            _lensEnabled.IsChecked = value.LensEnabled;
            _autoDensity.IsChecked = value.AutoDensity;
            _density.Value = value.Density;
            _threshold.Value = value.Threshold;
            _density.SetEnabled(!value.AutoDensity);
            _theme.SelectedIndex = value.Theme switch { LiquidGlassTheme.Light => 1, LiquidGlassTheme.Dark => 2, _ => 0 };
            _ink.SelectedIndex = value.Ink switch { LiquidGlassInk.Dark => 1, LiquidGlassInk.Light => 2, _ => 0 };
            _rim.Value = value.RimMaxPixels;
            _edge.Value = value.Edge;
            _power.Value = value.Power;
            _dispersion.Value = value.Dispersion;
            _centerDispersion.Value = value.CenterDispersion;
            _saveState.Text = _pending is null && !_saving ? "更改会自动保存" : "正在保存…";
            UpdateMaterialValues(_lightPanel, value.Light);
            UpdateMaterialValues(_darkPanel, value.Dark);
        }
        finally { _refreshing = false; }
    }

    static void UpdateMaterialValues(StackPanel panel, LiquidGlassMaterialPreferences material)
    {
        if (panel.Children.OfType<StackPanel>().FirstOrDefault() is not { } editor) return;
        foreach (var row in editor.Children.OfType<SliderRow>())
        {
            float? value = row.Label switch
            {
                "染色强度" => material.Opacity, "薄层染色" => material.Thin, "浓层染色" => material.Dense,
                "模糊（逻辑像素）" => material.Blur, "饱和度" => material.Saturation, "亮度" => material.Brightness,
                "对比度" => material.Contrast, "边缘高光" => material.RimLight, "曲面明暗" => material.Bevel, "阴影" => material.Shadow,
                _ => null
            };
            if (value.HasValue) row.Value = value.Value;
        }
        if (editor.Children.OfType<StackPanel>().FirstOrDefault() is { } tintRow)
        {
            if (tintRow.Children.OfType<ColorPicker>().FirstOrDefault() is { } picker) picker.Color = ParseTint(material.Tint);
            if (tintRow.Children.OfType<TextBox>().FirstOrDefault() is { } box) box.Text = material.Tint;
        }
    }

    void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        _saveTimer.Stop();
        _ = SavePendingAsync();
        AppWindow.Hide();
    }
    void OnClosed(object sender, WindowEventArgs args) => Detach();
    public void Show() { if (_disposed) return; Refresh(); AppWindow.Show(); Activate(); }
    void Detach()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.Changed -= Refresh;
        AppWindow.Closing -= OnClosing;
        Closed -= OnClosed;
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimer;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _saveTimer.Stop();
        _ = SavePendingAsync();
        AppWindow.Closing -= OnClosing;
        Close();
        Detach();
    }

    sealed class SliderRow : Grid
    {
        readonly Slider _slider;
        readonly TextBox _valueBox;
        readonly Action<float> _changed;
        bool _refreshing;
        public string Label { get; }
        public void SetEnabled(bool value) { _slider.IsEnabled = value; _valueBox.IsEnabled = value; }
        public float Value
        {
            get => (float)_slider.Value;
            set
            {
                _refreshing = true;
                _slider.Value = Math.Clamp(value, (float)_slider.Minimum, (float)_slider.Maximum);
                _valueBox.Text = Format(Value);
                _refreshing = false;
            }
        }
        public SliderRow(string label, double minimum, double maximum, double step, float value, Action<float> changed)
        {
            Label = label;
            _changed = changed;
            ColumnSpacing = 12;
            Margin = new Thickness(0, 2, 0, 2);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            _slider = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = step, VerticalAlignment = VerticalAlignment.Center, MinHeight = 38 };
            Grid.SetColumn(_slider, 1); Children.Add(_slider);
            _valueBox = new TextBox { Width = 76, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(_valueBox, 2); Children.Add(_valueBox);
            Value = value;
            _slider.ValueChanged += (_, _) =>
            {
                if (_refreshing) return;
                _refreshing = true; _valueBox.Text = Format(Value); _refreshing = false;
                _changed(Value);
            };
            _valueBox.LostFocus += (_, _) => CommitValue();
            _valueBox.KeyDown += (_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.Enter) return;
                args.Handled = true; CommitValue();
            };
            AutomationProperties.SetName(_slider, label);
            AutomationProperties.SetName(_valueBox, $"{label} 数值");
        }
        void CommitValue()
        {
            if (!double.TryParse(_valueBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
            { _valueBox.Text = Format(Value); return; }
            Value = (float)Math.Clamp(parsed, _slider.Minimum, _slider.Maximum);
            _changed(Value);
        }
        static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
