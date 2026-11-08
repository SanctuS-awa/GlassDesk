using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using GlassDesk.Core;
using Windows.Devices.Geolocation;
using Windows.Networking.Connectivity;

namespace GlassDesk.Platform;

public sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan RefreshPeriod = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AutoLocationMaxAge = TimeSpan.FromHours(3);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(12);
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IUiDispatcher _dispatcher;
    private readonly Diagnostics? _diagnostics;
    private readonly string _cacheDirectory;
    private readonly string _cachePath;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private WeatherSnapshot _snapshot = WeatherSnapshot.Initial;
    private WeatherPreferences? _preferences;
    private string? _cacheKey;
    private AutoLocation? _autoLocation;
    private CancellationTokenSource? _refreshCancellation;
    private bool _networkSubscribed;
    private bool _disposed;
    private bool _homeVisible;
    private bool _again;
    private bool _forceLocationNext;
    private bool _lastRefreshFailed;
    private bool _networkDebouncePending;
    private int _running;
    private int _failureCount;
    private long _generation;

    public WeatherService(IUiDispatcher dispatcher, Diagnostics? diagnostics, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _dispatcher = dispatcher;
        _diagnostics = diagnostics;
        _cacheDirectory = cacheDirectory;
        _cachePath = Path.Combine(cacheDirectory, "weather-cache.json");
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
            _networkSubscribed = true;
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    public event Action<WeatherSnapshot>? Changed;

    public WeatherSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public string StatusText
    {
        get
        {
            WeatherSnapshot snapshot = Snapshot;
            return snapshot.Status switch
            {
                WeatherStatus.Disabled => "天气已关闭",
                WeatherStatus.Loading => "正在获取天气…",
                WeatherStatus.Ready when snapshot.TemperatureC is double temperature &&
                                         snapshot.FetchedAt is DateTimeOffset fetchedAt =>
                    $"{snapshot.PlaceName} · {Math.Round(temperature).ToString(CultureInfo.InvariantCulture)}° " +
                    $"{WeatherCodes.ChineseText(snapshot.WeatherCode)} · 更新于 {fetchedAt.ToLocalTime():HH:mm}",
                WeatherStatus.Ready => "天气暂不可用",
                WeatherStatus.Stale =>
                    $"{snapshot.Message ?? "未能更新"}，显示 {LocalFetchedTime(snapshot.FetchedAt)} 的数据",
                WeatherStatus.NeedsLocation or WeatherStatus.Unavailable => snapshot.Message ?? "天气暂不可用",
                _ => "天气暂不可用"
            };
        }
    }

    public void Apply(WeatherPreferences preferences)
    {
        try
        {
            WeatherPreferences validated = (preferences ?? new WeatherPreferences()).Validated();
            WeatherPreferences? previous;
            CancellationTokenSource? cancellation;
            long generation;
            string? key;
            bool placeChanged;
            bool needsLocation;
            bool disabled;
            lock (_gate)
            {
                if (_disposed || validated == _preferences) return;
                previous = _preferences;
                placeChanged = previous is null || HasPlaceChanged(previous, validated);
                _preferences = validated;
                generation = ++_generation;
                key = CacheKey(validated);
                _cacheKey = key;
                disabled = !validated.Enabled;
                needsLocation = !disabled && validated.Mode == WeatherLocationMode.Manual && !validated.HasManualPlace;
                cancellation = _refreshCancellation;
                _again = false;
                _forceLocationNext = false;
                _lastRefreshFailed = false;
                _networkDebouncePending = false;
                if (placeChanged) _failureCount = 0;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            CancelQuietly(cancellation);
            if (disabled)
            {
                Publish(WeatherSnapshot.Off, generation);
                return;
            }
            if (needsLocation)
            {
                Publish(NewNeedsLocationSnapshot(), generation);
                return;
            }

            WeatherSnapshot current = Snapshot;
            WeatherSnapshot? cached = TryReadCache(key);
            bool cacheIsNewer = cached?.FetchedAt is DateTimeOffset cachedFetchedAt &&
                (current.FetchedAt is not DateTimeOffset currentFetchedAt || cachedFetchedAt > currentFetchedAt);
            bool shouldPublishCache = cached is not null &&
                (placeChanged || !current.HasData || current.FetchedAt is null || cacheIsNewer);
            if (shouldPublishCache)
                Publish(ApplyCacheAge(cached!), generation);
            else if (placeChanged || current.Status == WeatherStatus.Disabled)
                Publish(WeatherSnapshot.Initial, generation);

            RequestRefresh(forceLocation: false);
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    public void RefreshNow()
    {
        try
        {
            bool forceLocation;
            lock (_gate)
                forceLocation = _preferences?.Mode == WeatherLocationMode.Auto;
            RequestRefresh(forceLocation);
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    public void SetHomeVisible(bool visible)
    {
        try
        {
            bool expired;
            lock (_gate)
            {
                if (_disposed) return;
                bool becameVisible = visible && !_homeVisible;
                _homeVisible = visible;
                if (!becameVisible) return;
                expired = _snapshot.FetchedAt is DateTimeOffset fetchedAt &&
                    DateTimeOffset.Now - fetchedAt >= RefreshPeriod;
            }
            if (expired) RequestRefresh(forceLocation: false);
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        bool unsubscribe;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cancellation = _refreshCancellation;
            _refreshCancellation = null;
            unsubscribe = _networkSubscribed;
            _networkSubscribed = false;
            _again = false;
            _forceLocationNext = false;
            _networkDebouncePending = false;
        }

        _timer.Dispose();
        CancelQuietly(cancellation);
        if (unsubscribe)
        {
            try { NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged; }
            catch (Exception error) { RecordError("weather", error); }
        }
        GC.SuppressFinalize(this);
    }

    private void RequestRefresh(bool forceLocation)
    {
        bool startWorker = false;
        lock (_gate)
        {
            if (_disposed || _preferences is not { Enabled: true } preferences ||
                (preferences.Mode == WeatherLocationMode.Manual && !preferences.HasManualPlace))
                return;

            if (forceLocation) _forceLocationNext = true;
            if (Volatile.Read(ref _running) != 0)
            {
                _again = true;
                return;
            }
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _again = false;
                startWorker = true;
            }
            else
            {
                _again = true;
            }
        }

        if (!startWorker) return;
        try
        {
            _ = Task.Run(RefreshLoopAsync);
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _running, 0);
            RecordError("weather", error);
            ScheduleRetry(TimeSpan.FromMinutes(5));
        }
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (true)
            {
                WeatherPreferences preferences;
                WeatherSnapshot oldSnapshot;
                string cacheKey;
                long generation;
                bool forceLocation;
                CancellationTokenSource cancellation;
                lock (_gate)
                {
                    if (_disposed || _preferences is not { Enabled: true } current ||
                        (current.Mode == WeatherLocationMode.Manual && !current.HasManualPlace))
                        break;

                    preferences = current;
                    oldSnapshot = _snapshot;
                    cacheKey = _cacheKey ?? CacheKey(current) ?? "a";
                    generation = _generation;
                    forceLocation = _forceLocationNext;
                    _forceLocationNext = false;
                    cancellation = new CancellationTokenSource();
                    _refreshCancellation = cancellation;
                }

                bool succeeded = false;
                try
                {
                    RefreshResult result = await RefreshOnceAsync(
                        preferences, oldSnapshot, generation, forceLocation, cancellation.Token).ConfigureAwait(false);
                    if (IsCurrentGeneration(generation))
                    {
                        Publish(result.Snapshot, generation);
                        succeeded = result.Succeeded;
                        lock (_gate)
                        {
                            if (!_disposed && generation == _generation)
                            {
                                _lastRefreshFailed = !succeeded;
                                if (succeeded)
                                {
                                    _failureCount = 0;
                                    WriteCache(cacheKey, result.Snapshot);
                                }
                                else _failureCount = Math.Min(_failureCount + 1, 3);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    RecordEvent("weather", "refresh-cancelled");
                }
                catch (Exception error)
                {
                    RecordError("weather", error);
                    if (IsCurrentGeneration(generation))
                    {
                        bool offline = IsOffline();
                        string message = offline ? "网络未连接" : "天气更新失败";
                        WeatherSnapshot failed = FailureSnapshot(oldSnapshot, null, false, message);
                        Publish(failed, generation);
                        lock (_gate)
                        {
                            if (!_disposed && generation == _generation)
                            {
                                _lastRefreshFailed = true;
                                _failureCount = Math.Min(_failureCount + 1, 3);
                            }
                        }
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_refreshCancellation, cancellation))
                            _refreshCancellation = null;
                    }
                    cancellation.Dispose();
                }

                bool continueImmediately;
                lock (_gate)
                {
                    if (_disposed) break;
                    continueImmediately = _again &&
                        _preferences is { Enabled: true } active &&
                        !(active.Mode == WeatherLocationMode.Manual && !active.HasManualPlace);
                    if (continueImmediately)
                    {
                        _again = false;
                        _networkDebouncePending = false;
                    }
                    else if (generation == _generation)
                    {
                        TimeSpan delay = _networkDebouncePending
                            ? TimeSpan.FromSeconds(3)
                            : succeeded ? RefreshPeriod : BackoffDelay(_failureCount);
                        _timer.Change(delay,
                            Timeout.InfiniteTimeSpan);
                    }
                }
                if (!continueImmediately) break;
            }
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            bool restart;
            lock (_gate)
            {
                restart = !_disposed && _again && _preferences is { Enabled: true } preferences &&
                    !(preferences.Mode == WeatherLocationMode.Manual && !preferences.HasManualPlace) &&
                    Volatile.Read(ref _running) == 0;
                if (restart) _again = false;
            }
            if (restart) RequestRefresh(forceLocation: false);
        }
    }

    private async Task<RefreshResult> RefreshOnceAsync(
        WeatherPreferences preferences,
        WeatherSnapshot oldSnapshot,
        long generation,
        bool forceLocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GeoPlace? place;
        if (preferences.Mode == WeatherLocationMode.Manual)
        {
            if (!preferences.HasManualPlace)
                return new(false, NewNeedsLocationSnapshot());
            place = new GeoPlace(
                preferences.Latitude.GetValueOrDefault(),
                preferences.Longitude.GetValueOrDefault(),
                string.IsNullOrWhiteSpace(preferences.RegionName) ? "已选城市" : preferences.RegionName,
                false);
        }
        else
        {
            place = await ResolveAutoLocationAsync(forceLocation, generation, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (place is null)
        {
            const string message = "无法自动定位，请在设置中手动选择城市";
            return oldSnapshot.HasData
                ? new(false, oldSnapshot with { Status = WeatherStatus.Stale, Message = message })
                : new(false, new WeatherSnapshot(WeatherStatus.NeedsLocation, null, true,
                    null, null, null, null, null, true, null, null, null, message));
        }

        try
        {
            WeatherSnapshot snapshot = await FetchWeatherAsync(place, cancellationToken).ConfigureAwait(false);
            return new(true, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            RecordError("weather", error);
            bool offline = IsOffline();
            return new(false, FailureSnapshot(oldSnapshot, place.Name, place.AutoLocated,
                offline ? "网络未连接" : "天气更新失败"));
        }
    }

    private async Task<GeoPlace?> ResolveAutoLocationAsync(
        bool forceLocation,
        long generation,
        CancellationToken cancellationToken)
    {
        if (!forceLocation)
        {
            AutoLocation? saved;
            lock (_gate) saved = _autoLocation;
            if (saved is not null)
            {
                TimeSpan age = DateTimeOffset.UtcNow - saved.FetchedAt;
                if (age >= TimeSpan.Zero && age < AutoLocationMaxAge) return saved.Place;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        GeoPlace? place = await TryWindowsLocationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        place ??= await TryIpIpLocationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        place ??= await TryIpWhoLocationAsync(cancellationToken).ConfigureAwait(false);
        if (place is not null)
        {
            lock (_gate)
            {
                if (!_disposed && generation == _generation)
                    _autoLocation = new AutoLocation(place, DateTimeOffset.UtcNow);
            }
        }
        else
        {
            RecordEvent("weather", "location-all-sources-failed");
        }
        return place;
    }

    private async Task<GeoPlace?> TryWindowsLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            GeolocationAccessStatus access = await _dispatcher.InvokeAsync(
                async () => await Geolocator.RequestAccessAsync()).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (access != GeolocationAccessStatus.Allowed)
            {
                RecordEvent("weather", "windows-access-not-allowed");
                return null;
            }

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            Geoposition position = await locator.GetGeopositionAsync(
                TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
            cancellationToken.ThrowIfCancellationRequested();
            double latitude = position.Coordinate.Point.Position.Latitude;
            double longitude = position.Coordinate.Point.Position.Longitude;
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude)) return null;
            var nearest = ChinaRegionCatalog.Nearest(latitude, longitude);
            string name = nearest.HasValue && nearest.Value.DistanceKm <= 80
                ? nearest.Value.Region.Name
                : "当前位置";
            return new GeoPlace(latitude, longitude, name, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            RecordError("weather", error);
            return null;
        }
    }

    private async Task<GeoPlace?> TryIpIpLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                "https://myip.ipip.net/json", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("ret", out JsonElement ret) ||
                ret.ValueKind != JsonValueKind.String || !string.Equals(ret.GetString(), "ok", StringComparison.Ordinal) ||
                !document.RootElement.TryGetProperty("data", out JsonElement data) ||
                !data.TryGetProperty("location", out JsonElement location) ||
                location.ValueKind != JsonValueKind.Array || location.GetArrayLength() < 3)
                return null;
            string? country = location[0].GetString();
            string? province = location[1].GetString();
            string? city = location[2].GetString();
            if (!string.Equals(country, "中国", StringComparison.Ordinal)) return null;
            ChinaRegion? region = ChinaRegionCatalog.MatchByNames(province, city);
            return region is null ? null : new GeoPlace(region.Latitude, region.Longitude, region.Name, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            RecordError("weather", error);
            return null;
        }
    }

    private async Task<GeoPlace?> TryIpWhoLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                "https://ipwho.is/", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("success", out JsonElement success) || success.ValueKind != JsonValueKind.True)
                return null;
            double? latitude = TryReadDouble(root, "latitude");
            double? longitude = TryReadDouble(root, "longitude");
            if (latitude is not double lat || longitude is not double lon) return null;
            string? city = TryReadString(root, "city");
            var nearest = ChinaRegionCatalog.Nearest(lat, lon);
            string name = nearest.HasValue && nearest.Value.DistanceKm <= 80
                ? nearest.Value.Region.Name
                : string.IsNullOrWhiteSpace(city) ? "当前位置" : city;
            return new GeoPlace(lat, lon, name, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            RecordError("weather", error);
            return null;
        }
    }

    private async Task<WeatherSnapshot> FetchWeatherAsync(GeoPlace place, CancellationToken cancellationToken)
    {
        string latitude = place.Latitude.ToString("F4", CultureInfo.InvariantCulture);
        string longitude = place.Longitude.ToString("F4", CultureInfo.InvariantCulture);
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + latitude +
            "&longitude=" + longitude +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";
        using HttpResponseMessage response = await Http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("current", out JsonElement current))
            throw new JsonException("Weather response is missing current data.");
        JsonElement daily = root.TryGetProperty("daily", out JsonElement dailyValue) ? dailyValue : default;

        double? temperature = TryReadDouble(current, "temperature_2m");
        int? weatherCode = TryReadInt(current, "weather_code");
        if (temperature is null || weatherCode is null)
            throw new JsonException("Weather response is missing temperature or weather code.");

        double? apparent = TryReadDouble(current, "apparent_temperature");
        int? humidity = TryReadInt(current, "relative_humidity_2m");
        double? wind = TryReadDouble(current, "wind_speed_10m");
        bool isDay = TryReadInt(current, "is_day") == 1;
        double? maximum = TryReadFirstDouble(daily, "temperature_2m_max");
        double? minimum = TryReadFirstDouble(daily, "temperature_2m_min");
        return new WeatherSnapshot(WeatherStatus.Ready, place.Name, place.AutoLocated,
            temperature, apparent, humidity, wind, weatherCode, isDay, maximum, minimum, DateTimeOffset.Now, null);
    }

    private WeatherSnapshot FailureSnapshot(WeatherSnapshot oldSnapshot, string? placeName, bool autoLocated, string message)
    {
        if (oldSnapshot.HasData)
            return oldSnapshot with { Status = WeatherStatus.Stale, Message = message };
        return new WeatherSnapshot(WeatherStatus.Unavailable, placeName, autoLocated,
            null, null, null, null, null, true, null, null, null, message);
    }

    private static WeatherSnapshot NewNeedsLocationSnapshot() =>
        new(WeatherStatus.NeedsLocation, null, false, null, null, null, null, null, true,
            null, null, null, "请在 设置 → 天气 中选择城市");

    private void Publish(WeatherSnapshot value, long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _snapshot == value) return;
            _snapshot = value;
        }

        try
        {
            bool queued = _dispatcher.TryEnqueue(() =>
            {
                try { Changed?.Invoke(value); }
                catch (Exception error) { RecordError("weather", error); }
            });
            if (!queued) RecordEvent("weather", "ui-queue-unavailable");
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    private void OnTimer(object? state)
    {
        try
        {
            WeatherSnapshot? stale = null;
            long generation;
            bool active;
            lock (_gate)
            {
                if (_disposed) return;
                generation = _generation;
                _networkDebouncePending = false;
                if (_snapshot.Status == WeatherStatus.Ready &&
                    _snapshot.FetchedAt is DateTimeOffset fetchedAt &&
                    DateTimeOffset.Now - fetchedAt >= StaleAfter)
                    stale = _snapshot with { Status = WeatherStatus.Stale, Message = "未能更新" };
                active = _preferences is { Enabled: true } preferences &&
                    !(preferences.Mode == WeatherLocationMode.Manual && !preferences.HasManualPlace);
            }
            if (stale is not null) Publish(stale, generation);
            if (active) RequestRefresh(forceLocation: false);
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    private void OnNetworkStatusChanged(object? sender)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _preferences is not { Enabled: true } preferences ||
                    (preferences.Mode == WeatherLocationMode.Manual && !preferences.HasManualPlace))
                    return;
                bool oldData = _snapshot.FetchedAt is not DateTimeOffset fetchedAt ||
                    DateTimeOffset.Now - fetchedAt >= RefreshPeriod;
                if (_lastRefreshFailed || oldData)
                {
                    _networkDebouncePending = true;
                    _timer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    private void ScheduleRetry(TimeSpan delay)
    {
        try
        {
            lock (_gate)
                if (!_disposed && _preferences is { Enabled: true })
                    _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (Exception error)
        {
            RecordError("weather", error);
        }
    }

    private void WriteCache(string key, WeatherSnapshot snapshot)
    {
        string temporaryPath = _cachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string json = JsonSerializer.Serialize(new CacheEntry(key, snapshot), JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception error)
        {
            RecordError("weather", error);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception cleanupError) { RecordError("weather", cleanupError); }
        }
    }

    private WeatherSnapshot? TryReadCache(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            if (!File.Exists(_cachePath)) return null;
            string json = File.ReadAllText(_cachePath);
            CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(json, JsonOptions);
            if (entry is null || entry.Snapshot is null || !string.Equals(entry.Key, key, StringComparison.Ordinal) ||
                !entry.Snapshot.HasData || entry.Snapshot.FetchedAt is not DateTimeOffset fetchedAt)
                return null;
            TimeSpan age = DateTimeOffset.Now - fetchedAt;
            if (age < TimeSpan.Zero || age > CacheMaxAge) return null;
            return ApplyCacheAge(entry.Snapshot);
        }
        catch (Exception error)
        {
            RecordError("weather", error);
            return null;
        }
    }

    private static WeatherSnapshot ApplyCacheAge(WeatherSnapshot snapshot)
    {
        if (snapshot.FetchedAt is not DateTimeOffset fetchedAt) return snapshot with { Status = WeatherStatus.Stale };
        bool fresh = DateTimeOffset.Now - fetchedAt < RefreshPeriod;
        return snapshot with { Status = fresh ? WeatherStatus.Ready : WeatherStatus.Stale };
    }

    private bool IsOffline()
    {
        try
        {
            return NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel()
                != NetworkConnectivityLevel.InternetAccess;
        }
        catch (Exception error)
        {
            RecordError("weather", error);
            return false;
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_gate) return !_disposed && generation == _generation;
    }

    private void RecordError(string component, Exception error)
    {
        try { _diagnostics?.RecordError(component, error); }
        catch { }
    }

    private void RecordEvent(string component, string eventCode)
    {
        try { _diagnostics?.RecordEvent(component, eventCode); }
        catch { }
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (Exception) { }
    }

    private static bool HasPlaceChanged(WeatherPreferences previous, WeatherPreferences next) =>
        previous.Mode != next.Mode || previous.RegionCode != next.RegionCode ||
        previous.Latitude != next.Latitude || previous.Longitude != next.Longitude;

    private static string? CacheKey(WeatherPreferences preferences) =>
        preferences.Mode == WeatherLocationMode.Manual
            ? preferences.HasManualPlace ? $"m:{preferences.RegionCode}" : null
            : "a";

    private static TimeSpan BackoffDelay(int failureCount) => failureCount switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30)
    };

    private static string LocalFetchedTime(DateTimeOffset? fetchedAt) =>
        fetchedAt?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = HttpTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassDesk/0.2.2");
        return client;
    }

    private static double? TryReadDouble(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
            return null;
        return result;
    }

    private static int? TryReadInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            return null;
        return result;
    }

    private static double? TryReadFirstDouble(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            return null;
        JsonElement first = value[0];
        return first.ValueKind == JsonValueKind.Number && first.TryGetDouble(out double result) &&
            double.IsFinite(result) ? result : null;
    }

    private static string? TryReadString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record AutoLocation(GeoPlace Place, DateTimeOffset FetchedAt);
    private sealed record GeoPlace(double Latitude, double Longitude, string Name, bool AutoLocated);
    private sealed record RefreshResult(bool Succeeded, WeatherSnapshot Snapshot);
    private sealed record CacheEntry(string Key, WeatherSnapshot Snapshot);
}
