using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace GlassDesk.App;

/// <summary>Preserves native Slider semantics; only its local track/lens is GPU drawn.</summary>
internal sealed class SettingsGlassSlider : Grid, IDisposable
{
    readonly Slider _input = new() { Opacity = .001, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly SettingsGlassSurface _glass = new();
    readonly Border _focus = new() { BorderBrush = SettingsLiquidPreview.Brush(0xFF1689FA), BorderThickness = new(1.5), CornerRadius = new(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly DispatcherQueueTimer _settle;
    readonly Func<bool> _reduced;
    float _activity, _target;
    bool _pressed, _hovered, _active = true;
    public event RangeBaseValueChangedEventHandler? ValueChanged;
    public event Action<string>? Failed;
    public double Minimum { get => _input.Minimum; set => _input.Minimum = value; }
    public double Maximum { get => _input.Maximum; set => _input.Maximum = value; }
    public double StepFrequency { get => _input.StepFrequency; set => _input.StepFrequency = value; }
    public double Value { get => _input.Value; set => _input.Value = value; }
    public string AccessibleName { set => AutomationProperties.SetName(_input, value); }
    public SettingsGlassSlider(Func<bool> reduced)
    {
        _reduced = reduced; MinHeight = 40;
        Children.Add(_glass); Children.Add(_input); Children.Add(_focus);
        _glass.Failed += message => Failed?.Invoke(message);
        _settle = DispatcherQueue.GetForCurrentThread().CreateTimer(); _settle.Interval = TimeSpan.FromMilliseconds(16);
        _settle.Tick += (_, _) => { _activity += (_target - _activity) * .34f; if (Math.Abs(_activity - _target) < .008f) { _activity = _target; _settle.Stop(); } Render(); };
        _input.ValueChanged += (sender, args) => { Render(); ValueChanged?.Invoke(sender, args); };
        _input.PointerEntered += (_, _) => { _hovered = true; Update(); };
        _input.PointerExited += (_, _) => { _hovered = false; Update(); };
        _input.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { _pressed = true; Update(); }), true);
        _input.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { _pressed = false; Update(); }), true);
        _input.PointerCaptureLost += (_, _) => { _pressed = false; Update(); };
        _input.PointerCanceled += (_, _) => { _pressed = false; Update(); };
        _input.GotFocus += (_, _) => { _focus.Visibility = _input.FocusState == FocusState.Keyboard ? Visibility.Visible : Visibility.Collapsed; Render(); };
        _input.LostFocus += (_, _) => { _focus.Visibility = Visibility.Collapsed; _pressed = false; Update(); };
        _input.KeyDown += (_, e) => { if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down) { _pressed = true; Update(); } };
        _input.KeyUp += (_, _) => { _pressed = false; Update(); };
        SizeChanged += (_, _) => Render();
        Unloaded += (_, _) => { _settle.Stop(); _pressed = _hovered = false; };
    }
    void Update()
    {
        _target = _pressed ? 1 : _hovered ? .22f : 0;
        if (_reduced()) { _activity = _target; _settle.Stop(); Render(); }
        else if (_active) _settle.Start();
    }
    void Render()
    {
        if (!_active || ActualWidth < 8) return;
        float w = (float)ActualWidth, h = (float)ActualHeight;
        float value = (float)Math.Clamp((Value - Minimum) / Math.Max(1e-8, Maximum - Minimum), 0, 1);
        var f = new float[64]; f[3] = 1; f[12] = value; f[13] = _activity; f[14] = _input.IsEnabled ? 1 : .45f;
        f[16] = 16 + value * (w - 32); f[17] = h / 2;
        f[18] = 24 + _activity * 10; f[19] = 24 + _activity * 3;
        f[32] = f[19] / 2; f[33] = _activity;
        _glass.Present(f);
    }
    public void SetActive(bool active) { _active = active; _glass.SetActive(active); if (!active) { _settle.Stop(); _pressed = _hovered = false; _activity = _target = 0; } else Render(); }
    public void Dispose() { _settle.Stop(); _glass.Dispose(); }
}

/// <summary>A self-contained companion switch; this sample changes only the preview.</summary>
internal sealed class SettingsGlassSwitch : Grid, IDisposable
{
    readonly SettingsGlassSurface _glass = new();
    readonly ToggleButton _input = new() { Background = SettingsLiquidPreview.Brush(0), BorderThickness = new(0), Opacity = .001, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    readonly Border _focus = new() { BorderBrush = SettingsLiquidPreview.Brush(0xFF1689FA), BorderThickness = new(1.5), CornerRadius = new(19), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly DispatcherQueueTimer _timer;
    readonly Func<bool> _reduced;
    float _position, _activity;
    bool _active = true, _pressed;
    public event Action<bool>? Changed;
    public event Action<string>? Failed;
    public SettingsGlassSwitch(Func<bool> reduced)
    {
        _reduced = reduced; Width = 66; Height = 40;
        Children.Add(_glass); Children.Add(_input); Children.Add(_focus);
        AutomationProperties.SetName(_input, "陪伴开心表情预览，仅影响实验台");
        _glass.Failed += text => Failed?.Invoke(text);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => { float end = _input.IsChecked == true ? 1 : 0; _position += (end - _position) * .28f; _activity += ((_pressed ? 1 : 0) - _activity) * .28f; if (Math.Abs(_position - end) < .002 && _activity < .008 && !_pressed) { _position = end; _activity = 0; _timer.Stop(); } Render(); };
        _input.Checked += (_, _) => Change(); _input.Unchecked += (_, _) => Change();
        _input.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { _pressed = true; Wake(); }), true);
        _input.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { _pressed = false; Wake(); }), true);
        _input.PointerCaptureLost += (_, _) => { _pressed = false; Wake(); };
        _input.GotFocus += (_, _) => _focus.Visibility = _input.FocusState == FocusState.Keyboard ? Visibility.Visible : Visibility.Collapsed;
        _input.LostFocus += (_, _) => { _focus.Visibility = Visibility.Collapsed; _pressed = false; Wake(); };
        Loaded += (_, _) => Render(); Unloaded += (_, _) => _timer.Stop();
    }
    void Change() { Wake(); Changed?.Invoke(_input.IsChecked == true); }
    void Wake() { if (!_active) return; if (_reduced()) { _position = _input.IsChecked == true ? 1 : 0; _activity = 0; Render(); } else _timer.Start(); }
    void Render()
    {
        if (!_active) return;
        var f = new float[64]; f[3] = 3; f[12] = _position; f[13] = _activity; f[14] = _input.IsEnabled ? 1 : .45f;
        f[16] = 18 + _position * 30; f[17] = 20; f[18] = 27 + _activity * 3; f[19] = 27; f[32] = 13.5f; f[33] = _activity;
        _glass.Present(f);
    }
    public void SetActive(bool active) { _active = active; _glass.SetActive(active); if (!active) _timer.Stop(); else Render(); }
    public void Dispose() { _timer.Stop(); _glass.Dispose(); }
}
