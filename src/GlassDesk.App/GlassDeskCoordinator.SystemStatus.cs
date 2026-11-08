using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GlassDesk.Core;
using GlassDesk.Platform;
using Microsoft.UI.Dispatching;

namespace GlassDesk.App;

internal sealed partial class GlassDeskCoordinator
{
    HomeSystemStatusClient homeSystemStatus = null!;
    DispatcherQueueTimer homeVolumeWriteTimer = null!;
    HomeSystemStatusSnapshot homeStatusSnapshot = HomeSystemStatusSnapshot.Unknown;
    float? pendingHomeVolume;
    long lastHomeVolumeWriteTicks;
    float? confirmedHomeVolume;
    bool homeVolumeDragging;
    bool homeSystemStatusWasExpanded;
    string? homeVolumeNotice;
    string? graphicsNotice;
    static readonly object graphicsLogGate = new();

    void InitializeHomeSystemStatus()
    {
        homeSystemStatus = new HomeSystemStatusClient(dispatcher, diagnostics);
        homeSystemStatus.Changed += OnHomeSystemStatusChanged;
        homeStatusSnapshot = homeSystemStatus.Snapshot;
        confirmedHomeVolume = homeStatusSnapshot.VolumeScalar;
        diagnostics.RecordEvent("home-volume", homeStatusSnapshot.AudioAvailable && homeStatusSnapshot.HasValidVolume
            ? "initial-snapshot-valid" : "initial-snapshot-unavailable");
        homeVolumeWriteTimer = queue.CreateTimer();
        homeVolumeWriteTimer.IsRepeating = false;
        homeVolumeWriteTimer.Tick += async (_, _) => await FlushPendingHomeVolumeAsync();
    }

    void OnHomeSystemStatusChanged(HomeSystemStatusSnapshot value)
    {
        if (disposed || quitting) return;
        homeStatusSnapshot = homeVolumeDragging && value.AudioAvailable && value.HasValidVolume
            ? value with { VolumeScalar = homeStatusSnapshot.VolumeScalar }
            : value;
        if (!homeVolumeDragging) confirmedHomeVolume = value.VolumeScalar;
        if (queue.HasThreadAccess) RefreshHomeStatusUi();
        else queue.TryEnqueue(RefreshHomeStatusUi);
    }

    void RefreshHomeStatusUi()
    {
        if (disposed || quitting) return;
        if (controller.Form == WidgetForm.Details && pages.CurrentPageId == DetailsPageController.Home)
        {
            TryScene(() => scene.UpdateHomeSystemStatus(homeStatusSnapshot, homeVolumeDragging, homeVolumeNotice));
            Render();
        }
        Changed?.Invoke();
    }

    void SetHomeSystemStatusExpanded()
    {
        try
        {
            bool expanded = !disposed && controller.Visible && controller.Form == WidgetForm.Details
                && pages.CurrentPageId == DetailsPageController.Home;
            homeSystemStatus.SetExpanded(expanded);
            // Render() calls this before scene.Render(), so sync the current snapshot now.
            var latest = homeSystemStatus.Snapshot;
            homeStatusSnapshot = homeVolumeDragging && latest.AudioAvailable && latest.HasValidVolume
                ? latest with { VolumeScalar = homeStatusSnapshot.VolumeScalar }
                : latest;
            if (!homeVolumeDragging) confirmedHomeVolume = homeStatusSnapshot.VolumeScalar;
            if (expanded)
                scene.UpdateHomeSystemStatus(homeStatusSnapshot, homeVolumeDragging, homeVolumeNotice);
            if (expanded && !homeSystemStatusWasExpanded)
                diagnostics.RecordEvent("home-volume", "scene-snapshot-synced");
            homeSystemStatusWasExpanded = expanded;
        }
        catch (Exception error) { diagnostics.RecordError("home-system-status", error); }
    }

    void WriteGraphicsDiagnostic(string stage, Exception? error)
    {
        string type = error?.GetType().Name ?? "none";
        Exception? nativeError = error is HomeSurfaceRenderException surface ? surface.InnerException : error?.InnerException ?? error;
        string call = error is HomeSurfaceRenderException ? "RenderSurface" : stage == "presentation"
            ? "ApplySceneFrameV1" : "SceneRenderOrHostUpdate";
        string hr = nativeError is null ? "none" : $"0x{unchecked((uint)nativeError.HResult):X8}";
        int opIndex = error is HomeSurfaceRenderException homeSurface ? homeSurface.InvalidOperationIndex : -1;
        string opKind = error is HomeSurfaceRenderException { InvalidOperation: { } operation }
            ? operation.Kind.ToString() : "unknown";
        string opRect = error is HomeSurfaceRenderException { InvalidOperation: { } op }
            ? FormattableString.Invariant($"{op.X:R},{op.Y:R},{op.Width:R},{op.Height:R}") : "unknown";
        string surfaceDip = error is HomeSurfaceRenderException sized
            ? FormattableString.Invariant($"{sized.SurfaceSize.X:R}x{sized.SurfaceSize.Y:R}") : "unknown";
        int opCount = error is HomeSurfaceRenderException count ? count.OperationCount : -1;
        string stack = error?.StackTrace ?? error?.InnerException?.StackTrace ?? "";
        stack = Regex.Replace(stack, @"(?:[A-Za-z]:\\|/)[^\r\n\)\]]+", "<path>");
        string line = FormattableString.Invariant(
            $"{DateTimeOffset.Now:O} stage={stage} component=managed-graphics call={call} geometrySequence={glassSceneSequence} glassShapeCount={lastGlassShapeCount} hresult={hr} exceptionType={type} innerType={nativeError?.GetType().Name ?? "none"} opIndex={opIndex} opKind={opKind} opRect={opRect} surfaceDip={surfaceDip} opCount={opCount} stack={stack.Replace('\r', ' ').Replace('\n', ' ')}")
            + Environment.NewLine;
        try
        {
            string directory = Path.Combine(store.DirectoryPath, "logs");
            string path = Path.Combine(directory, "liquid-glass-managed.log");
            lock (graphicsLogGate)
            {
                Directory.CreateDirectory(directory);
                const long maximumBytes = 2 * 1024 * 1024;
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > maximumBytes)
                {
                    string first = path + ".1", second = path + ".2";
                    if (File.Exists(second)) File.Delete(second);
                    if (File.Exists(first)) File.Move(first, second);
                    File.Move(path, first);
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch (Exception logError)
        {
            Debug.WriteLine($"GlassDesk managed graphics log failed: {logError.GetType().Name} 0x{unchecked((uint)logError.HResult):X8}");
        }
    }

    void QueueHomeVolumeWrite(float scalar, bool final)
    {
        if (!float.IsFinite(scalar) || !homeStatusSnapshot.AudioAvailable) return;
        scalar = Math.Clamp(scalar, 0, 1);
        homeStatusSnapshot = homeStatusSnapshot with
        {
            VolumeScalar = scalar,
            Muted = scalar <= 0 ? homeStatusSnapshot.Muted : false
        };
        if (!final)
        {
            pendingHomeVolume = scalar;
            double elapsed = lastHomeVolumeWriteTicks == 0 ? 1000
                : (Stopwatch.GetTimestamp() - lastHomeVolumeWriteTicks) * 1000d / Stopwatch.Frequency;
            if (elapsed >= 33) _ = FlushPendingHomeVolumeAsync();
            else if (!homeVolumeWriteTimer.IsRunning)
            {
                homeVolumeWriteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, 34 - elapsed));
                homeVolumeWriteTimer.Start();
            }
            return;
        }
        pendingHomeVolume = null;
        homeVolumeWriteTimer.Stop();
        _ = WriteHomeVolumeAsync(scalar, true);
    }

    async Task FlushPendingHomeVolumeAsync()
    {
        if (pendingHomeVolume is not float value || disposed) return;
        double elapsed = lastHomeVolumeWriteTicks == 0 ? 1000
            : (Stopwatch.GetTimestamp() - lastHomeVolumeWriteTicks) * 1000d / Stopwatch.Frequency;
        if (elapsed < 33)
        {
            homeVolumeWriteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, 34 - elapsed));
            homeVolumeWriteTimer.Start();
            return;
        }
        pendingHomeVolume = null;
        await WriteHomeVolumeAsync(value, false);
    }

    async Task WriteHomeVolumeAsync(float scalar, bool final)
    {
        try
        {
            lastHomeVolumeWriteTicks = Stopwatch.GetTimestamp();
            await homeSystemStatus.SetMasterVolumeAsync(scalar, CancellationToken.None);
            HomeSystemStatusSnapshot confirmation = homeSystemStatus.Snapshot;
            if (!confirmation.AudioAvailable || !confirmation.HasValidVolume)
                throw new InvalidOperationException("系统没有返回可确认的音量值。");
            confirmedHomeVolume = confirmation.VolumeScalar;
            homeStatusSnapshot = homeVolumeDragging && !final
                ? confirmation with { VolumeScalar = homeStatusSnapshot.VolumeScalar }
                : confirmation;
            homeVolumeNotice = null;
        }
        catch (Exception error)
        {
            diagnostics.RecordError("home-volume", error);
            homeVolumeNotice = "音量暂不可用";
            HomeSystemStatusSnapshot latest = homeSystemStatus.Snapshot;
            homeStatusSnapshot = latest with
            {
                VolumeScalar = latest.AudioAvailable ? confirmedHomeVolume : null
            };
        }
        if (!disposed) RefreshHomeStatusUi();
    }

    async void ToggleHomeMuteAsync()
    {
        if (!homeStatusSnapshot.AudioAvailable || homeStatusSnapshot.Muted is not bool muted) return;
        try
        {
            await homeSystemStatus.SetMuteAsync(!muted, CancellationToken.None);
            HomeSystemStatusSnapshot confirmation = homeSystemStatus.Snapshot;
            if (!confirmation.AudioAvailable || confirmation.Muted is not bool confirmedMute)
                throw new InvalidOperationException("系统没有返回可确认的静音状态。");
            homeStatusSnapshot = confirmation with { Muted = confirmedMute };
            homeVolumeNotice = null;
        }
        catch (Exception error)
        {
            diagnostics.RecordError("home-mute", error);
            homeVolumeNotice = "音量暂不可用";
        }
        RefreshHomeStatusUi();
    }

    bool RouteHomeVolumePointer(HostPointerEvent e, int id)
    {
        bool volume = id == WidgetActions.HomeVolume || homeVolumeDragging;
        bool mute = id == WidgetActions.HomeMute;
        if (!volume && !mute) return false;

        if (e.Kind == HostEventKind.RangeValue && id == WidgetActions.HomeVolume)
        {
            QueueHomeVolumeWrite((float)Math.Clamp(e.Value / 100d, 0, 1), true);
            TryScene(() => scene.UpdateHomeSystemStatus(homeStatusSnapshot, false, homeVolumeNotice));
            Render();
            return true;
        }
        if (e.Kind == HostEventKind.Wheel && volume)
        {
            if (homeStatusSnapshot.AudioAvailable && homeStatusSnapshot.VolumeScalar is float current)
            {
                QueueHomeVolumeWrite(Math.Clamp(current + Math.Sign(e.Value) * .02f, 0, 1), true);
                TryScene(() => scene.UpdateHomeSystemStatus(homeStatusSnapshot, homeVolumeDragging, homeVolumeNotice));
                Render();
            }
            return true;
        }
        if (mute)
        {
            if (e.Kind == HostEventKind.Invoke) { ToggleHomeMuteAsync(); return true; }
            if (e.Kind == HostEventKind.Down) { homePressedAction = id; scene.SetHomePointerFeedback(id); Render(); return true; }
            if (e.Kind == HostEventKind.Up)
            {
                bool activate = homePressedAction == id;
                homePressedAction = -1; scene.SetHomePointerFeedback(-1);
                if (activate) ToggleHomeMuteAsync();
                Render(); return true;
            }
            if (e.Kind == HostEventKind.CaptureLost)
            { homePressedAction = -1; scene.SetHomePointerFeedback(-1); Render(); return true; }
            return true;
        }

        var track = scene.HomeVolumeTrack;
        if (e.Kind == HostEventKind.Down)
        {
            if (!homeStatusSnapshot.AudioAvailable) return true;
            homeVolumeDragging = true;
            scene.SetCapture(PointerCaptureOwner.Timeline, Now);
            UpdateHomeVolumeFromPointer(e.Y, track);
            TryScene(() => scene.UpdateHomeSystemStatus(homeStatusSnapshot, true, homeVolumeNotice));
            Render();
            return true;
        }
        if (e.Kind == HostEventKind.Move && homeVolumeDragging)
        {
            UpdateHomeVolumeFromPointer(e.Y, track);
            TryScene(() => scene.UpdateHomeSystemStatus(homeStatusSnapshot, true, homeVolumeNotice));
            Render(); return true;
        }
        if (e.Kind == HostEventKind.Up && homeVolumeDragging)
        {
            UpdateHomeVolumeFromPointer(e.Y, track, true);
            homeVolumeDragging = false;
            scene.SetCapture(PointerCaptureOwner.None, Now);
            TryScene(() => scene.UpdateHomeSystemStatus(homeStatusSnapshot, false, homeVolumeNotice));
            Render(); return true;
        }
        if (e.Kind == HostEventKind.CaptureLost && homeVolumeDragging)
        {
            float? finalValue = pendingHomeVolume ?? homeStatusSnapshot.VolumeScalar;
            homeVolumeDragging = false; pendingHomeVolume = null; homeVolumeWriteTimer.Stop();
            if (finalValue is float value) QueueHomeVolumeWrite(value, true);
            scene.SetCapture(PointerCaptureOwner.None, Now);
            TryScene(() => scene.UpdateHomeSystemStatus(homeStatusSnapshot, false, homeVolumeNotice));
            Render(); return true;
        }
        return true;
    }

    void UpdateHomeVolumeFromPointer(float pointerY, FloatRect track, bool final = false)
    {
        if (!float.IsFinite(pointerY) || track.Height <= 0) return;
        float scalar = 1 - Math.Clamp((pointerY - track.Y) / track.Height, 0, 1);
        homeStatusSnapshot = homeStatusSnapshot with { VolumeScalar = scalar, Muted = scalar <= 0 ? homeStatusSnapshot.Muted : false };
        QueueHomeVolumeWrite(scalar, final);
    }

    sealed class HomeSystemStatusClient : IDisposable
    {
        readonly SystemStatusService service;
        public event Action<HomeSystemStatusSnapshot>? Changed;
        public HomeSystemStatusClient(IUiDispatcher dispatcher, Diagnostics diagnostics)
        {
            service = new SystemStatusService(dispatcher, diagnostics);
            service.Changed += OnChanged;
        }
        static HomeSystemStatusSnapshot ConvertSnapshot(SystemStatusSnapshot value) => new(
            value.CapturedAt, value.NetworkKind.ToString(), value.HasInternet, value.SignalBars0To5,
            value.HasBattery, value.BatteryPercent0To100, value.Charging, value.OnAc,
            value.VolumeScalar, value.Muted, value.AudioAvailable);
        void OnChanged(SystemStatusSnapshot value) => Changed?.Invoke(ConvertSnapshot(value));
        public HomeSystemStatusSnapshot Snapshot => ConvertSnapshot(service.Snapshot);
        public void SetExpanded(bool expanded) => service.SetExpanded(expanded);
        public Task SetMasterVolumeAsync(float scalar, CancellationToken token) => service.SetMasterVolumeAsync(scalar, token);
        public Task SetMuteAsync(bool muted, CancellationToken token) => service.SetMuteAsync(muted, token);
        public void Dispose()
        {
            service.Changed -= OnChanged;
            service.Dispose();
        }
    }

}

internal sealed record HomeSystemStatusSnapshot(DateTimeOffset CapturedAt, string NetworkKind,
    bool? HasInternet, byte? SignalBars0To5, bool? HasBattery, byte? BatteryPercent0To100,
    bool? Charging, bool? OnAc, float? VolumeScalar, bool? Muted, bool AudioAvailable)
{
    public bool HasValidVolume => VolumeScalar is float scalar && float.IsFinite(scalar) && scalar >= 0 && scalar <= 1;
    public static HomeSystemStatusSnapshot Unknown { get; } = new(DateTimeOffset.MinValue, "Unknown",
        null, null, null, null, null, null, null, null, false);
}
