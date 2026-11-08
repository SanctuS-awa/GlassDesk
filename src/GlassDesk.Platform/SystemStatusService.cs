using System.Diagnostics;
using GlassDesk.Core;
using Windows.Networking.Connectivity;

namespace GlassDesk.Platform;

/// <summary>Publishes privacy-preserving system connectivity, battery, and default-volume state.</summary>
public sealed class SystemStatusService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan NetworkPollPeriod = TimeSpan.FromSeconds(2);
    private const int BatteryPollSeconds = 30;

    private readonly IUiDispatcher _dispatcher;
    private readonly Diagnostics? _diagnostics;
    private readonly SystemMasterVolume _masterVolume;
    private readonly Timer _expandedTimer;
    private readonly object _snapshotGate = new();
    private readonly object _lifecycleGate = new();
    private readonly object _networkRequestGate = new();
    private readonly object _batteryRequestGate = new();
    private readonly object _changedGate = new();

    private SystemStatusSnapshot _snapshot;
    private SystemStatusSnapshot? _pendingChangedSnapshot;
    private SystemPowerStatusWatcher? _powerStatusWatcher;
    private bool _networkSubscribed;
    private bool _expanded;
    private bool _networkRefreshRequested;
    private bool _networkWorkerRunning;
    private bool _networkSignalRequested;
    private bool _batteryRefreshRequested;
    private bool _batteryWorkerRunning;
    private bool _changedDispatchQueued;
    private long _nextSignalRefreshTimestamp;
    private long _nextBatteryRefreshTimestamp;
    private long _lastSignalReadTimestamp;
    private int _disposed;

    public SystemStatusService(IUiDispatcher dispatcher, Diagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _diagnostics = diagnostics;
        _snapshot = SystemStatusSnapshot.Unknown(DateTimeOffset.Now);
        _expandedTimer = new Timer(OnExpandedTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _masterVolume = new SystemMasterVolume(diagnostics);
        _masterVolume.Changed += OnAudioChanged;
        UpdateAudio(_masterVolume.Snapshot);

        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
            _networkSubscribed = true;
        }
        catch (Exception error)
        {
            RecordError("system.network.subscribe", error);
        }

        try { _powerStatusWatcher = new SystemPowerStatusWatcher(ScheduleBatteryRefresh, diagnostics); }
        catch (Exception error) { RecordError("system.power.subscribe", error); }

        ScheduleNetworkRefresh(includeSignal: false);
        ScheduleBatteryRefresh();
    }

    public event Action<SystemStatusSnapshot>? Changed;

    public SystemStatusSnapshot Snapshot
    {
        get { lock (_snapshotGate) return _snapshot; }
    }

    /// <summary>Enables 2-second network/signal and 30-second battery polling while expanded.</summary>
    public void SetExpanded(bool expanded)
    {
        ThrowIfDisposed();
        bool changed;
        lock (_lifecycleGate)
        {
            changed = _expanded != expanded;
            if (!changed) return;
            _expanded = expanded;
            if (expanded)
            {
                _nextSignalRefreshTimestamp = 0;
                _nextBatteryRefreshTimestamp = Stopwatch.GetTimestamp() + (long)BatteryPollSeconds * Stopwatch.Frequency;
                _expandedTimer.Change(NetworkPollPeriod, NetworkPollPeriod);
            }
            else
            {
                _expandedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        if (expanded)
        {
            ScheduleNetworkRefresh(includeSignal: TryReserveSignalRefresh(force: true));
            ScheduleBatteryRefresh();
        }
    }

    public Task SetMasterVolumeAsync(float scalar, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _masterVolume.SetMasterVolumeAsync(scalar, cancellationToken);
    }

    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _masterVolume.SetMuteAsync(muted, cancellationToken);
    }

    private void OnNetworkStatusChanged(object? sender) =>
        ScheduleNetworkRefresh(includeSignal: TryReserveSignalRefresh(force: false));

    private void OnExpandedTimer(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        bool batteryDue;
        lock (_lifecycleGate)
        {
            if (!_expanded) return;
            long now = Stopwatch.GetTimestamp();
            batteryDue = now >= _nextBatteryRefreshTimestamp;
            if (batteryDue)
                _nextBatteryRefreshTimestamp = now + (long)BatteryPollSeconds * Stopwatch.Frequency;
        }

        ScheduleNetworkRefresh(includeSignal: TryReserveSignalRefresh(force: false));
        if (batteryDue) ScheduleBatteryRefresh();
    }

    private bool TryReserveSignalRefresh(bool force)
    {
        lock (_lifecycleGate)
        {
            if (!_expanded) return false;
            long now = Stopwatch.GetTimestamp();
            if (!force && now < _nextSignalRefreshTimestamp) return false;
            _nextSignalRefreshTimestamp = now + (long)NetworkPollPeriod.TotalSeconds * Stopwatch.Frequency;
            return true;
        }
    }

    private void ScheduleNetworkRefresh(bool includeSignal)
    {
        bool startWorker = false;
        lock (_networkRequestGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _networkRefreshRequested = true;
            _networkSignalRequested |= includeSignal;
            if (!_networkWorkerRunning)
            {
                _networkWorkerRunning = true;
                startWorker = true;
            }
        }
        if (startWorker) _ = Task.Run(ProcessNetworkRefreshQueue);
    }

    private void ProcessNetworkRefreshQueue()
    {
        while (true)
        {
            bool includeSignal;
            lock (_networkRequestGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_networkRefreshRequested)
                {
                    _networkWorkerRunning = false;
                    return;
                }
                _networkRefreshRequested = false;
                includeSignal = _networkSignalRequested;
                _networkSignalRequested = false;
            }

            if (Volatile.Read(ref _disposed) != 0) continue;
            try
            {
                if (includeSignal)
                {
                    long now = Stopwatch.GetTimestamp();
                    long minimumSignalInterval = (long)NetworkPollPeriod.TotalSeconds * Stopwatch.Frequency;
                    if (_lastSignalReadTimestamp != 0 && now - _lastSignalReadTimestamp < minimumSignalInterval)
                        includeSignal = false;
                    else
                        _lastSignalReadTimestamp = now;
                }
                byte? previousSignal = Snapshot.SignalBars0To5;
                NetworkSnapshot network = ReadNetworkSnapshot(includeSignal, previousSignal);
                UpdateNetwork(network);
            }
            catch (Exception error)
            {
                RecordError("system.network.refresh", error);
                UpdateNetwork(NetworkSnapshot.Unknown);
            }
        }
    }

    private void ScheduleBatteryRefresh()
    {
        bool startWorker = false;
        lock (_batteryRequestGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _batteryRefreshRequested = true;
            if (!_batteryWorkerRunning)
            {
                _batteryWorkerRunning = true;
                startWorker = true;
            }
        }
        if (startWorker) _ = Task.Run(ProcessBatteryRefreshQueue);
    }

    private void ProcessBatteryRefreshQueue()
    {
        while (true)
        {
            lock (_batteryRequestGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_batteryRefreshRequested)
                {
                    _batteryWorkerRunning = false;
                    return;
                }
                _batteryRefreshRequested = false;
            }

            if (Volatile.Read(ref _disposed) != 0) continue;
            try { UpdateBattery(ReadBatterySnapshot()); }
            catch (Exception error)
            {
                RecordError("system.battery.refresh", error);
                UpdateBattery(BatterySnapshot.Unknown);
            }
        }
    }

    private NetworkSnapshot ReadNetworkSnapshot(bool includeSignal, byte? previousSignal)
    {
        ConnectionProfile? internetProfile = null;
        ProfileSnapshot? internetState = null;
        bool primaryApiFailed = false;
        bool profileReadFailed = false;
        try
        {
            internetProfile = NetworkInformation.GetInternetConnectionProfile();
            if (internetProfile is not null)
            {
                internetState = TryReadProfile(internetProfile, includeSignal, previousSignal);
                profileReadFailed = !internetState.HasValue;
                if (internetState is { IsConnected: true, Kind: SystemNetworkKind.Wifi or SystemNetworkKind.Ethernet })
                    return NetworkSnapshot.FromProfile(internetState.Value);
            }
        }
        catch (Exception error)
        {
            primaryApiFailed = true;
            RecordError("system.network.internet-profile", error);
        }

        IReadOnlyList<ConnectionProfile>? profiles = null;
        bool profilesApiFailed = false;
        try { profiles = NetworkInformation.GetConnectionProfiles(); }
        catch (Exception error)
        {
            profilesApiFailed = true;
            RecordError("system.network.profiles", error);
        }

        ProfileSnapshot? wifi = null;
        ProfileSnapshot? ethernet = null;
        ProfileSnapshot? otherConnected = null;
        if (profiles is not null)
        {
            foreach (ConnectionProfile profile in profiles)
            {
                ProfileSnapshot? state = TryReadProfile(profile, includeSignal, previousSignal);
                if (!state.HasValue) profileReadFailed = true;
                if (state is not { IsConnected: true }) continue;
                if (state.Value.Kind == SystemNetworkKind.Wifi)
                    wifi ??= state;
                else if (state.Value.Kind == SystemNetworkKind.Ethernet)
                    ethernet ??= state;
                else
                    otherConnected ??= state;
            }
        }

        if (wifi.HasValue) return NetworkSnapshot.FromProfile(wifi.Value, internetState?.HasInternet);
        if (ethernet.HasValue) return NetworkSnapshot.FromProfile(ethernet.Value, internetState?.HasInternet);
        if (internetState is { IsConnected: true })
            return NetworkSnapshot.FromProfile(internetState.Value);
        if (otherConnected.HasValue) return NetworkSnapshot.FromProfile(otherConnected.Value, internetState?.HasInternet);
        if (internetState.HasValue) return NetworkSnapshot.FromProfile(internetState.Value);
        if (primaryApiFailed || profilesApiFailed || profileReadFailed || profiles is null) return NetworkSnapshot.Unknown;
        return NetworkSnapshot.Offline;
    }

    private ProfileSnapshot? TryReadProfile(ConnectionProfile profile, bool includeSignal, byte? previousSignal)
    {
        try
        {
            NetworkConnectivityLevel level = profile.GetNetworkConnectivityLevel();
            bool isWifi = profile.IsWlanConnectionProfile;
            SystemNetworkKind kind;
            if (isWifi)
            {
                kind = SystemNetworkKind.Wifi;
            }
            else
            {
                uint interfaceType = 0;
                try { interfaceType = profile.NetworkAdapter?.IanaInterfaceType ?? 0u; }
                catch (Exception error) { RecordError("system.network.adapter", error); }
                kind = interfaceType == 6 ? SystemNetworkKind.Ethernet : SystemNetworkKind.Unknown;
            }

            bool? hasInternet = level switch
            {
                NetworkConnectivityLevel.InternetAccess => true,
                NetworkConnectivityLevel.ConstrainedInternetAccess => true,
                NetworkConnectivityLevel.LocalAccess => false,
                NetworkConnectivityLevel.None => false,
                _ => null
            };

            byte? signalBars = null;
            if (kind == SystemNetworkKind.Wifi)
            {
                if (!includeSignal)
                    signalBars = previousSignal;
                else
                {
                    try
                    {
                        signalBars = profile.GetSignalBars();
                        if (signalBars is byte bars && bars > 5) signalBars = null;
                    }
                    catch (Exception error) { RecordError("system.network.signal", error); }
                }
            }

            return new ProfileSnapshot(kind, hasInternet, signalBars, level != NetworkConnectivityLevel.None);
        }
        catch (Exception error)
        {
            RecordError("system.network.profile", error);
            return null;
        }
    }

    private BatterySnapshot ReadBatterySnapshot()
    {
        if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.PowerStatus status))
        {
            RecordEvent("system.battery", "read-failed");
            return BatterySnapshot.Unknown;
        }

        bool? onAc = status.AcLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null
        };
        bool? hasBattery = status.BatteryFlag switch
        {
            128 => false,
            255 => null,
            _ => true
        };
        byte? percentage = hasBattery == true && status.BatteryLifePercent <= 100
            ? status.BatteryLifePercent
            : null;
        bool? charging = null;
        if (hasBattery == true)
        {
            if ((status.BatteryFlag & 8) != 0)
                charging = onAc == false ? false : true;
            else if (onAc.HasValue)
                charging = false;
        }

        return new BatterySnapshot(hasBattery, percentage, charging, onAc);
    }

    private void OnAudioChanged(SystemMasterVolumeSnapshot audio) => UpdateAudio(audio);

    private void UpdateNetwork(NetworkSnapshot network) => UpdateSnapshot(current => current with
    {
        NetworkKind = network.Kind,
        HasInternet = network.HasInternet,
        SignalBars0To5 = network.SignalBars0To5
    });

    private void UpdateBattery(BatterySnapshot battery) => UpdateSnapshot(current => current with
    {
        HasBattery = battery.HasBattery,
        BatteryPercent0To100 = battery.Percentage,
        Charging = battery.Charging,
        OnAc = battery.OnAc
    });

    private void UpdateAudio(SystemMasterVolumeSnapshot audio) => UpdateSnapshot(current => current with
    {
        VolumeScalar = audio.AudioAvailable ? audio.VolumeScalar : null,
        Muted = audio.AudioAvailable ? audio.Muted : null,
        AudioAvailable = audio.AudioAvailable
    });

    private void UpdateSnapshot(Func<SystemStatusSnapshot, SystemStatusSnapshot> update)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        SystemStatusSnapshot next;
        bool changed;
        lock (_snapshotGate)
        {
            SystemStatusSnapshot current = _snapshot;
            next = update(current) with { CapturedAt = DateTimeOffset.Now };
            changed = (current with { CapturedAt = next.CapturedAt }) != next;
            _snapshot = next;
        }
        if (changed) QueueChanged(next);
    }

    private void QueueChanged(SystemStatusSnapshot snapshot)
    {
        lock (_changedGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _pendingChangedSnapshot = snapshot;
            if (_changedDispatchQueued) return;
            _changedDispatchQueued = true;
        }

        bool queued;
        try { queued = _dispatcher.TryEnqueue(DeliverChangedSnapshots); }
        catch (Exception error)
        {
            RecordError("system.status.dispatch", error);
            queued = false;
        }
        if (queued) return;

        lock (_changedGate)
        {
            _pendingChangedSnapshot = null;
            _changedDispatchQueued = false;
        }
        RecordEvent("system.status.dispatch", "ui-queue-unavailable");
    }

    private void DeliverChangedSnapshots()
    {
        while (true)
        {
            SystemStatusSnapshot snapshot;
            lock (_changedGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    _pendingChangedSnapshot = null;
                    _changedDispatchQueued = false;
                    return;
                }
                if (_pendingChangedSnapshot is not SystemStatusSnapshot pending)
                {
                    _changedDispatchQueued = false;
                    return;
                }
                snapshot = pending;
                _pendingChangedSnapshot = null;
            }

            Action<SystemStatusSnapshot>? handlers = Changed;
            if (handlers is null) continue;
            foreach (Action<SystemStatusSnapshot> handler in handlers.GetInvocationList())
            {
                try { handler(snapshot); }
                catch (Exception error) { RecordError("system.status.subscriber", error); }
            }
        }
    }

    private void RecordError(string component, Exception error)
    {
        if (_diagnostics is not null) _diagnostics.RecordError(component, error);
        else Trace.TraceWarning($"{component} failed ({error.GetType().Name}, HRESULT 0x{error.HResult:X8}).");
    }

    private void RecordEvent(string component, string eventCode)
    {
        if (_diagnostics is not null) _diagnostics.RecordEvent(component, eventCode);
        else Trace.TraceWarning($"{component}: {eventCode}.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private bool BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return false;
        _expandedTimer.Dispose();
        if (_networkSubscribed)
        {
            try { NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged; }
            catch (Exception error) { RecordError("system.network.unsubscribe", error); }
            _networkSubscribed = false;
        }
        _powerStatusWatcher?.Dispose();
        _powerStatusWatcher = null;
        _masterVolume.Changed -= OnAudioChanged;
        lock (_changedGate)
        {
            _pendingChangedSnapshot = null;
            _changedDispatchQueued = false;
        }
        return true;
    }

    public void Dispose()
    {
        if (!BeginDispose()) return;
        _masterVolume.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose()) return;
        await _masterVolume.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private readonly record struct ProfileSnapshot(SystemNetworkKind Kind, bool? HasInternet, byte? SignalBars0To5, bool IsConnected);

    private readonly record struct NetworkSnapshot(SystemNetworkKind Kind, bool? HasInternet, byte? SignalBars0To5)
    {
        internal static NetworkSnapshot Unknown => new(SystemNetworkKind.Unknown, null, null);
        internal static NetworkSnapshot Offline => new(SystemNetworkKind.Offline, false, null);
        internal static NetworkSnapshot FromProfile(ProfileSnapshot profile, bool? hasInternetOverride = null) => new(
            profile.Kind,
            hasInternetOverride ?? profile.HasInternet,
            profile.Kind == SystemNetworkKind.Wifi ? profile.SignalBars0To5 : null);
    }

    private readonly record struct BatterySnapshot(bool? HasBattery, byte? Percentage, bool? Charging, bool? OnAc)
    {
        internal static BatterySnapshot Unknown => new(null, null, null, null);
    }
}
