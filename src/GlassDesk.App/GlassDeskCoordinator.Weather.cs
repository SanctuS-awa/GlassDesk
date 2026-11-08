using GlassDesk.Core;
using GlassDesk.Platform;

namespace GlassDesk.App;

internal sealed partial class GlassDeskCoordinator
{
    WeatherService weather = null!;
    WeatherSnapshot weatherSnapshot = WeatherSnapshot.Initial;

    void InitializeWeather()
    {
        weather = new WeatherService(dispatcher, diagnostics, store.DirectoryPath);
        weather.Changed += OnWeatherChanged;
        weatherSnapshot = weather.Snapshot;
        weather.Apply(Preferences.Weather);
    }

    void OnWeatherChanged(WeatherSnapshot value)
    {
        if (disposed || quitting) return;
        weatherSnapshot = value;
        TryScene(() => scene.UpdateHomeWeather(value));
        if (controller.Form == WidgetForm.Details && pages.CurrentPageId == DetailsPageController.Home) Render();
        Changed?.Invoke();
    }

    void SetWeatherHomeVisible(bool visible)
    {
        try { weather.SetHomeVisible(visible); }
        catch (Exception error) { diagnostics.RecordError("weather", error); }
    }

    void DisposeWeather()
    {
        weather.Changed -= OnWeatherChanged;
        weather.Dispose();
    }

    public string WeatherStatus => weather.StatusText;

    public void RefreshWeather()
    {
        try { weather.RefreshNow(); }
        catch (Exception error) { diagnostics.RecordError("weather", error); }
    }
}
