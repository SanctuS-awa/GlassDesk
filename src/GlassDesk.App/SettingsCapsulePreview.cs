using System.Globalization;
using GlassDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace GlassDesk.App;

/// <summary>Layout-only sample on a controlled background. It does not mirror the desktop material.</summary>
internal sealed class SettingsCapsulePreview : Grid
{
    readonly Canvas _canvas = new() { Width = 200, Height = 52 };
    readonly Border _shell = new() { Width = 200, Height = 52,
        HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(26) };
    readonly Path _body = new();
    readonly Border[] _eyes = [new(), new()];
    readonly TextBlock _clock = new(), _aux = new();
    (WidgetPreferences Preferences, string Minute, bool HighContrast)? _key;

    public SettingsCapsulePreview()
    {
        CornerRadius = new CornerRadius(10); Padding = new Thickness(16, 12, 16, 14);
        var high = new AccessibilitySettings().HighContrast;
        var colors = new UISettings();
        Background = PreviewBackground(high, colors);
        _shell.Background = PreviewSampleBackground(high, colors, WidgetTextInk.Black);
        var layout = new Grid { RowSpacing = 12 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = SettingsTypography.Apply(new TextBlock { Text = "组件布局示意" }, TextRole.Caption, InkRole.Secondary);
        layout.Children.Add(label);
        _canvas.Children.Add(_body);
        foreach (var eye in _eyes) _canvas.Children.Add(eye);
        _canvas.Children.Add(_clock); _canvas.Children.Add(_aux);
        _shell.Child = _canvas; Grid.SetRow(_shell, 1); layout.Children.Add(_shell);
        var note = SettingsTypography.Apply(new TextBlock { Text = "样板背景只辅助查看手动文字颜色；实际材质以桌面组件为准。",
            TextWrapping = TextWrapping.Wrap }, TextRole.Caption, InkRole.Secondary);
        Grid.SetRow(note, 2); layout.Children.Add(note); Children.Add(layout);
        IsHitTestVisible = false;
        AutomationProperties.SetName(this, "组件布局示意，样板背景不代表桌面材质，圆形陪伴球在时间左侧");
    }

    public void Present(WidgetPreferences preferences, DateTimeOffset now)
    {
        bool high = new AccessibilitySettings().HighContrast;
        var key = (preferences, now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), high);
        if (_key == key) return; _key = key;
        var system = new UISettings();
        Background = PreviewBackground(high, system);
        _shell.Background = PreviewSampleBackground(high, system, preferences.TextInk);
        uint color = preferences.CompanionColor;
        Color body = high ? system.UIElementColor(UIElementType.WindowText)
            : Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        var points = CompanionContour.GetPoints(preferences.CompanionAppearanceId, CompanionShapePose.Rounded);
        const double scale = 32d / 24;
        var figure = new PathFigure { IsClosed = true, IsFilled = true,
            StartPoint = new(18 + points[0].X * scale, 10 + points[0].Y * scale) };
        foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = new(18 + point.X * scale, 10 + point.Y * scale) });
        var geometry = new PathGeometry(); geometry.Figures.Add(figure);
        _body.Data = geometry; _body.Fill = new SolidColorBrush(body);
        _body.Visibility = preferences.CompanionEnabled ? Visibility.Visible : Visibility.Collapsed;
        double r = body.R / 255d, g = body.G / 255d, b = body.B / 255d;
        var eyeInk = high ? system.UIElementColor(UIElementType.Window)
            : .2126 * r * r + .7152 * g * g + .0722 * b * b > .20
                ? Color.FromArgb(255, 32, 35, 41) : Color.FromArgb(255, 255, 255, 255);
        for (int i = 0; i < _eyes.Length; i++)
        {
            var eye = _eyes[i]; eye.Width = 2.67 * scale; eye.Height = 4 * scale; eye.CornerRadius = new(eye.Width / 2);
            eye.Background = new SolidColorBrush(eyeInk); eye.Visibility = _body.Visibility;
            Canvas.SetLeft(eye, 18 + (i == 0 ? 6.56 : 14.77) * scale); Canvas.SetTop(eye, 10 + 9.7 * scale);
        }
        string primary = preferences.ClockShowTime ? now.ToString("HH:mm", CultureInfo.InvariantCulture)
            : preferences.ClockShowDate ? now.ToString("M月d日", CultureInfo.CurrentCulture)
            : preferences.ClockShowWeekday ? now.ToString("dddd", CultureInfo.CurrentCulture) : "";
        string auxiliary = string.Join("  ", new[]
        {
            preferences.ClockShowTime && preferences.ClockShowDate ? now.ToString("M月d日", CultureInfo.CurrentCulture) : null,
            (preferences.ClockShowTime || preferences.ClockShowDate) && preferences.ClockShowWeekday ? now.ToString("dddd", CultureInfo.CurrentCulture) : null
        }.Where(text => text is not null));
        bool secondary = auxiliary.Length > 0;
        SettingsTypography.Apply(_clock, preferences.ClockShowTime ? TextRole.ClockCapsule : TextRole.Label, InkRole.Primary);
        SettingsTypography.Apply(_aux, TextRole.ClockLegacyAux, InkRole.Secondary);
        _clock.Text = primary; _aux.Text = auxiliary;
        SettingsTypography.SetClockFamily(_clock, preferences.ClockShowTime ? preferences.ClockFontId : null);
        if (!high)
        {
            bool light = preferences.TextInk == WidgetTextInk.White;
            uint primaryInk = TypographyCatalog.Ink(InkRole.Primary, light);
            uint secondaryInk = TypographyCatalog.Ink(InkRole.Secondary, light);
            _clock.Foreground = new SolidColorBrush(Color.FromArgb(255,
                (byte)(primaryInk >> 16), (byte)(primaryInk >> 8), (byte)primaryInk));
            _aux.Foreground = new SolidColorBrush(Color.FromArgb(255,
                (byte)(secondaryInk >> 16), (byte)(secondaryInk >> 8), (byte)secondaryInk));
        }
        double left = preferences.CompanionEnabled ? 56 : 16, width = 200 - left - 16;
        _clock.Width = _aux.Width = width; _clock.Height = secondary ? 27 : 32; _aux.Height = 16;
        _clock.TextAlignment = _aux.TextAlignment = TextAlignment.Center;
        Canvas.SetLeft(_clock, left); Canvas.SetTop(_clock, secondary ? 5 : 10);
        Canvas.SetLeft(_aux, left); Canvas.SetTop(_aux, 32);
        _aux.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
    }

    static Brush PreviewBackground(bool high, UISettings system)
    {
        if (high) return new SolidColorBrush(system.UIElementColor(UIElementType.Window));
        return new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1),
            GradientStops = { new GradientStop { Offset = 0, Color = Color.FromArgb(255, 220, 231, 247) },
                new GradientStop { Offset = 1, Color = Color.FromArgb(255, 242, 233, 237) } } };
    }

    static Brush PreviewSampleBackground(bool high, UISettings system, WidgetTextInk ink)
    {
        if (high) return new SolidColorBrush(system.UIElementColor(UIElementType.Window));
        if (ink == WidgetTextInk.White)
            return new SolidColorBrush(Color.FromArgb(255, 66, 76, 89));
        return new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1),
            GradientStops = { new GradientStop { Offset = 0, Color = Color.FromArgb(255, 220, 231, 247) },
                new GradientStop { Offset = 1, Color = Color.FromArgb(255, 242, 233, 237) } } };
    }
}

