// 低级输入钩子：全局捕获鼠标左键/右键点击与键盘按键（录制"动作"用）
// 机制：WH_MOUSE_LL / WH_KEYBOARD_LL 低级钩子（SetWindowsHookEx），
//   钩子线程必须常驻并泵消息（GetMessage 循环），否则钩子被系统摘除；
//   回调内只做轻量组装+转发（低级钩子超时 ~300ms），UIA 元素解析留给订阅方在 UI 线程做。
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace UiTreeMonitor.Core.Uia;

/// <summary>捕获的输入动作类型。</summary>
public enum InputActionKind
{
    MouseLeftClick,
    MouseRightClick,
    MouseLeftDrag,      // 长按拖动（按下→移动→抬起，位移超过阈值）
    MouseRightDrag,
    KeyDown,
}

/// <summary>一次输入动作（钩子线程产出；元素信息由订阅方另行解析）。</summary>
public sealed record InputActionRecord(
    DateTime Timestamp,
    InputActionKind Kind,
    int X, int Y,          // 鼠标动作起点（点击点 / 拖动按下点）
    int EndX, int EndY,    // 拖动终点（仅 Drag 类有效）
    uint VkCode,           // 键盘虚拟键码（VK_*）
    bool Ctrl, bool Shift, bool Alt);

/// <summary>录制捕获选项（设置持久化，MainWindow 注入到 RecordingPanel）。
/// Mode: "uia"=UIA 语义事件模式（默认，可回放 Invoke/SetValue/Select 等）；"input"=鼠标键盘动作模式（坐标+左右键/拖动/按键）。</summary>
public sealed record CaptureOptions(
    string Mode = "uia",
    bool MouseLeft = true,
    bool MouseRight = true,
    bool Keyboard = true)
{
    public bool IsUiaMode => !string.Equals(Mode, "input", StringComparison.OrdinalIgnoreCase);
    public const string ModeUia = "uia";
    public const string ModeInput = "input";
}

/// <summary>
/// 全局低级输入钩子：捕获鼠标左键/右键点击与键盘按键。
/// 回调在专用钩子线程触发（非 UI 线程），订阅方需自行 Dispatcher 切换。
/// </summary>
public sealed class InputHookCore : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;
    private const uint VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;      // 修饰键
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;                          // Win 键
    private const int RepeatSuppressMs = 300;                                   // 自动重复去重窗口
    private const int DragThresholdPx = 5;                                      // 按下→抬起位移超过该值判定为拖动

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    // ★ 委托必须以字段持有：SetWindowsHookEx 后委托被 GC 回收 → 回调失效/崩溃
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyboardHook;
    private volatile bool _running;
    private int _captured;                       // 诊断计数（Stop 时输出）
    private uint _lastVk;                        // 键盘自动重复去重（钩子线程单线程访问）
    private long _lastVkTick;
    private bool _downValid;                     // 鼠标按下暂存（钩子线程单线程访问）：抬起时判定点击/拖动
    private int _downX, _downY;
    private bool _downRight;

    /// <summary>捕获开关（按设置生效；钩子常驻期间修改即时生效）。</summary>
    public bool CaptureMouseLeft { get; set; } = true;
    public bool CaptureMouseRight { get; set; } = true;
    public bool CaptureKeyboard { get; set; } = true;

    /// <summary>捕获到输入动作（钩子线程回调，订阅方自行切线程）。</summary>
    public event Action<InputActionRecord>? ActionCaptured;

    public InputHookCore()
    {
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _running = true;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
            ready.Set();
            AppLog.Write("RECORD", $"InputHook started (mouse={_mouseHook != IntPtr.Zero}, keyboard={_keyboardHook != IntPtr.Zero}, tid={_threadId}, left={CaptureMouseLeft}, right={CaptureMouseRight}, keys={CaptureKeyboard})");
            // 消息泵：低级钩子要求安装线程持续取消息，退出靠 PostThreadMessage(WM_QUIT)
            while (_running && GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        })
        { IsBackground = true, Name = "InputHook" };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        if (_thread is null) return;
        _running = false;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
        AppLog.Write("RECORD", $"InputHook stopped (captured={_captured})");
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (uint)wParam.ToInt64();
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // 按下暂存、抬起判定：位移 ≤ 阈值 = 点击；> 阈值 = 长按拖动（起点+终点）
            switch (msg)
            {
                case WM_LBUTTONDOWN when CaptureMouseLeft:
                    _downValid = true; _downRight = false; _downX = m.pt.X; _downY = m.pt.Y;
                    break;
                case WM_RBUTTONDOWN when CaptureMouseRight:
                    _downValid = true; _downRight = true; _downX = m.pt.X; _downY = m.pt.Y;
                    break;
                case WM_LBUTTONUP when _downValid && !_downRight:
                case WM_RBUTTONUP when _downValid && _downRight:
                    RaiseMouseAction(m.pt.X, m.pt.Y);
                    break;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>按下→抬起完成：按位移判定点击或拖动并产出记录。</summary>
    private void RaiseMouseAction(int upX, int upY)
    {
        _downValid = false;
        var dx = upX - _downX;
        var dy = upY - _downY;
        var isDrag = dx * dx + dy * dy >= DragThresholdPx * DragThresholdPx;
        var kind = (_downRight, isDrag) switch
        {
            (false, false) => InputActionKind.MouseLeftClick,
            (false, true) => InputActionKind.MouseLeftDrag,
            (true, false) => InputActionKind.MouseRightClick,
            (true, true) => InputActionKind.MouseRightDrag,
        };
        Raise(new InputActionRecord(
            DateTime.Now, kind, _downX, _downY, upX, upY, 0, false, false, false));
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && CaptureKeyboard)
        {
            var msg = (uint)wParam.ToInt64();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = k.vkCode;

                // 修饰键不单独记录（仅作为组合前缀出现在下一个键），Win 键同理过滤
                if (vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN)
                    return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

                // 自动重复去重：按住不放时系统连发 KeyDown（~30ms 间隔）
                var now = Environment.TickCount64;
                if (vk == _lastVk && now - _lastVkTick < RepeatSuppressMs)
                    return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                _lastVk = vk;
                _lastVkTick = now;

                Raise(new InputActionRecord(
                    DateTime.Now, InputActionKind.KeyDown, 0, 0, 0, 0, vk,
                    GetAsyncKeyState((int)VK_CONTROL) < 0,
                    GetAsyncKeyState((int)VK_SHIFT) < 0,
                    GetAsyncKeyState((int)VK_MENU) < 0));
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void Raise(InputActionRecord r)
    {
        _captured++;
        try { ActionCaptured?.Invoke(r); }
        catch { /* 订阅方异常绝不影响钩子链 */ }
    }

    public void Dispose() => Stop();
}
