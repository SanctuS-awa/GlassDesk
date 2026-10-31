using System.Runtime.InteropServices;
using GlassDesk.Core;

namespace GlassDesk.Platform;

/// <summary>Observes only button-down events. Never consumes or synthesizes input.</summary>
public sealed class OutsidePointerObserver : IDisposable
{
    readonly IUiDispatcher dispatcher;
    readonly Action<OutsidePointerPress> notify;
    readonly HookProc callback;
    readonly Action<Exception>? failed;
    readonly object gate = new();
    Thread? thread;
    uint threadId;
    OutsidePointerSnapshot? snapshot;
    bool disposed;
    bool installFailed;
    public OutsidePointerObserver(IUiDispatcher dispatcher, Action<OutsidePointerPress> notify,Action<Exception>? failed=null)
    { this.dispatcher=dispatcher;this.notify=notify;this.failed=failed;callback=OnMouse; }
    public void Update(OutsidePointerSnapshot? value)
    {
        Volatile.Write(ref snapshot,value);
        lock(gate)
        {
            if(disposed)return;
            if(value is null)installFailed=false;
            if(value is not null && thread is null&&!installFailed)
            {
                thread=new Thread(Run){IsBackground=true,Name="GlassDesk outside pointer"};thread.Start();
            }
            else if(value is null && threadId!=0)PostThreadMessage(threadId,0x12,0,0);
        }
    }
    void Run()
    {
        nint hook=0;bool installed=false;
        try
        {
            PeekMessage(out _,0,0,0,0);
            lock(gate){threadId=GetCurrentThreadId();if(disposed||Volatile.Read(ref snapshot) is null)return;}
            hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
            if(hook==0)
            {
                var error=new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Outside pointer observation could not be installed; tray collapse remains available.");
                lock(gate)installFailed=true;
                dispatcher.TryEnqueue(()=>failed?.Invoke(error));return;
            }
            installed=true;
            while(GetMessage(out var message,0,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}
        }
        finally
        {
            if(hook!=0)UnhookWindowsHookEx(hook);
            lock(gate)
            {
                threadId=0;thread=null;
                if(installed&&!disposed&&Volatile.Read(ref snapshot) is not null)
                {thread=new Thread(Run){IsBackground=true,Name="GlassDesk outside pointer"};thread.Start();}
            }
        }
    }
    nint OnMouse(int code,nuint message,nint data)
    {
        try
        {
            if(code>=0 && message is 0x201 or 0x204 or 0x207)
            {
                var state=Volatile.Read(ref snapshot);
                var mouse=Marshal.PtrToStructure<MouseData>(data);
                if(state?.IsOutsidePress(mouse.X,mouse.Y)==true)
                {
                    var press=new OutsidePointerPress(state.OpenSequence,state.GeometryVersion,mouse.X,mouse.Y,
                        message==0x201?OutsidePointerButton.Left:message==0x204?OutsidePointerButton.Right:OutsidePointerButton.Middle,
                        (mouse.Flags&1)!=0,state.IsOutsideRegisteredRegion(mouse.X,mouse.Y),state.IsOutsideDismissRegion(mouse.X,mouse.Y));
                    dispatcher.TryEnqueue(()=>notify(press));
                }
            }
        }
        catch { /* A broken observer must never block another application's input. */ }
        return CallNextHookEx(0,code,message,data);
    }
    public void Dispose()
    {
        Thread? pending;
        lock(gate){disposed=true;Volatile.Write(ref snapshot,null);pending=thread;if(threadId!=0)PostThreadMessage(threadId,0x12,0,0);}
        if(pending!=Thread.CurrentThread)pending?.Join(1000);
    }
    delegate nint HookProc(int code,nuint message,nint data);
    [StructLayout(LayoutKind.Sequential)] struct MouseData {public int X,Y;public uint MouseInfo,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)] struct Message {public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public int X,Y;public uint Private;}
    [DllImport("user32.dll",EntryPoint="SetWindowsHookExW",SetLastError=true)] static extern nint SetWindowsHookEx(int id,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook,int code,nuint message,nint data);
    [DllImport("user32.dll",EntryPoint="GetMessageW")] static extern int GetMessage(out Message message,nint hwnd,uint min,uint max);
    [DllImport("user32.dll",EntryPoint="PeekMessageW")] static extern bool PeekMessage(out Message message,nint hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")] static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="PostThreadMessageW")] static extern bool PostThreadMessage(uint id,uint message,nuint wParam,nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)] static extern nint GetModuleHandle(string? module);
}
