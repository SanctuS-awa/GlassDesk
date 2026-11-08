using System.Runtime.InteropServices;
using System.Diagnostics;

namespace GlassDesk.Platform;

internal static class SystemStatusNativeMethods
{
    internal const uint CoinitMultithreaded = 0x0;
    internal const uint ClsctxAll = 0x17;

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId, out nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public nint MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern int GetMessageW(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterPowerSettingNotification(nint notification);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetCurrentThreadId();
}

/// <summary>Receives power-status broadcasts on a private, invisible top-level window.</summary>
internal sealed class SystemPowerStatusWatcher : IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmQuit = 0x0012;
    private const uint PbtApmPowerStatusChange = 0x000A;
    private const uint PbtPowerSettingChange = 0x8013;
    private const uint DeviceNotifyWindowHandle = 0x00000000;

    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly Guid BatteryPercentageRemaining = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");

    private readonly Action _onPowerStatusChanged;
    private readonly Diagnostics? _diagnostics;
    private readonly string _className = $"GlassDesk.SystemStatus.{Guid.NewGuid():N}";
    private readonly SystemStatusNativeMethods.WindowProc _windowProc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private nint _window;
    private nint _acNotification;
    private nint _batteryNotification;
    private nint _instance;
    private uint _threadId;
    private int _disposed;

    internal SystemPowerStatusWatcher(Action onPowerStatusChanged, Diagnostics? diagnostics)
    {
        _onPowerStatusChanged = onPowerStatusChanged;
        _diagnostics = diagnostics;
        _windowProc = WindowProcedure;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "GlassDesk power status"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        _threadId = SystemStatusNativeMethods.GetCurrentThreadId();
        bool classRegistered = false;
        try
        {
            _instance = SystemStatusNativeMethods.GetModuleHandleW(null);
            var windowClass = new SystemStatusNativeMethods.WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<SystemStatusNativeMethods.WindowClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = _instance,
                ClassName = _className
            };

            if (SystemStatusNativeMethods.RegisterClassExW(ref windowClass) == 0)
            {
                RecordWin32Error("system.power.register-class");
                return;
            }
            classRegistered = true;

            _window = SystemStatusNativeMethods.CreateWindowExW(0, _className, string.Empty, 0,
                0, 0, 1, 1, 0, 0, _instance, 0);
            if (_window == 0)
            {
                RecordWin32Error("system.power.create-window");
                return;
            }

            Guid acGuid = AcDcPowerSource;
            _acNotification = SystemStatusNativeMethods.RegisterPowerSettingNotification(_window, ref acGuid, DeviceNotifyWindowHandle);
            if (_acNotification == 0) RecordWin32Error("system.power.register-ac-notification");

            Guid batteryGuid = BatteryPercentageRemaining;
            _batteryNotification = SystemStatusNativeMethods.RegisterPowerSettingNotification(_window, ref batteryGuid, DeviceNotifyWindowHandle);
            if (_batteryNotification == 0) RecordWin32Error("system.power.register-battery-notification");
        }
        catch (Exception error)
        {
            RecordError("system.power.watcher", error);
        }
        finally
        {
            _ready.Set();
            if (_window == 0 && classRegistered && _instance != 0)
                _ = SystemStatusNativeMethods.UnregisterClassW(_className, _instance);
        }

        if (_window == 0) return;

        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                int result = SystemStatusNativeMethods.GetMessageW(out var message, 0, 0, 0);
                if (result < 0)
                {
                    RecordWin32Error("system.power.get-message");
                    break;
                }
                if (result == 0) break;
                _ = SystemStatusNativeMethods.TranslateMessage(ref message);
                _ = SystemStatusNativeMethods.DispatchMessageW(ref message);
            }
        }
        catch (Exception error)
        {
            RecordError("system.power.message-loop", error);
        }
        finally
        {
            if (_acNotification != 0) _ = SystemStatusNativeMethods.UnregisterPowerSettingNotification(_acNotification);
            if (_batteryNotification != 0) _ = SystemStatusNativeMethods.UnregisterPowerSettingNotification(_batteryNotification);
            if (_window != 0) _ = NativeMethods.DestroyWindow(_window);
            if (_instance != 0) _ = SystemStatusNativeMethods.UnregisterClassW(_className, _instance);
            _window = 0;
        }
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == WmPowerBroadcast &&
            (wParam == (nuint)PbtApmPowerStatusChange || wParam == (nuint)PbtPowerSettingChange) &&
            Volatile.Read(ref _disposed) == 0)
        {
            try { _onPowerStatusChanged(); }
            catch (Exception error) { RecordError("system.power.callback", error); }
        }

        return SystemStatusNativeMethods.DefWindowProcW(window, message, wParam, lParam);
    }

    private void RecordWin32Error(string component)
    {
        int error = Marshal.GetLastWin32Error();
        if (_diagnostics is not null) _diagnostics.RecordWin32Error(component, error);
        else Trace.TraceWarning($"{component} failed (Win32 error {error}).");
    }

    private void RecordError(string component, Exception error)
    {
        if (_diagnostics is not null) _diagnostics.RecordError(component, error);
        else Trace.TraceWarning($"{component} failed ({error.GetType().Name}, HRESULT 0x{error.HResult:X8}).");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        uint threadId = _threadId;
        if (threadId != 0 && _thread.IsAlive)
            _ = SystemStatusNativeMethods.PostThreadMessageW(threadId, WmQuit, 0, 0);
        if (_thread.IsAlive && Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
            _thread.Join();
        _ready.Dispose();
    }
}
