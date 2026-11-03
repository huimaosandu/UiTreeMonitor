// 输入模拟（回放）：SendInput 模拟鼠标左键/右键点击与键盘组合键
// 配合 InputHookCore 录制的 Input_Mouse / Input_Keyboard 记录完成录制→回放闭环。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace UiTreeMonitor.Core.Uia;

/// <summary>输入模拟器：录制记录（Input_Mouse/Input_Keyboard）→ SendInput 回放。</summary>
public static class InputSimulator
{
    public const string MousePattern = "Input_Mouse";
    public const string KeyboardPattern = "Input_Keyboard";
    public const string LeftClickMethod = "LeftClick";
    public const string RightClickMethod = "RightClick";

    // ==== VK ↔ 键名（录制显示与回放反查共用） ====

    private static readonly Dictionary<uint, string> VkNames = BuildVkNames();

    private static Dictionary<uint, string> BuildVkNames()
    {
        var d = new Dictionary<uint, string>
        {
            [0x08] = "Back", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x14] = "CapsLock",
            [0x1B] = "Esc", [0x20] = "Space",
            [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
            [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
            [0x5B] = "Win", [0x5C] = "RWin",
            [0x6A] = "Num*", [0x6B] = "Num+", [0x6D] = "Num-", [0x6E] = "Num.", [0x6F] = "Num/",
            [0xA0] = "Shift", [0xA1] = "Shift", [0xA2] = "Ctrl", [0xA3] = "Ctrl", [0xA4] = "Alt", [0xA5] = "Alt",
            [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/",
            [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
        };
        for (uint i = 0; i <= 9; i++) d[0x60 + i] = $"Num{i}";  // 小键盘 0-9
        for (uint i = 0; i < 24; i++) d[0x70 + i] = $"F{i + 1}";
        return d;
    }

    /// <summary>VK → 键名（字母/数字用字符本身，其余查表，未知返回 VK_XX）。</summary>
    public static string VkToName(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        return VkNames.TryGetValue(vk, out var n) ? n : $"VK_{vk:X2}";
    }

    /// <summary>键名 → VK（反查；未知返回 null）。</summary>
    public static uint? NameToVk(string name)
    {
        foreach (var (vk, n) in VkNames)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return vk;
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z') return (uint)c;
        }
        return null;
    }

    /// <summary>修饰键状态 + VK → 组合键文本（如 "Ctrl+Shift+C"）。</summary>
    public static string ComboText(bool ctrl, bool shift, bool alt, uint vk)
        => $"{(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}{VkToName(vk)}";

    /// <summary>解析组合键文本 → VK + 修饰键（如 "Ctrl+C"；分段以 '+' 分隔，最后一段是主键）。</summary>
    public static bool TryParseCombo(string? combo, out uint vk, out bool ctrl, out bool shift, out bool alt)
    {
        vk = 0; ctrl = shift = alt = false;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var main = parts[^1].Trim();
        var key = NameToVk(main);
        if (key is null) return false;
        vk = key.Value;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default: return false;
            }
        }
        return true;
    }

    // ==== SendInput ====

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const int ClickDelayMs = 40; // 移动到点击 / 键按下到抬起的间隔

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    /// <summary>模拟鼠标点击（移动到坐标后按下/抬起，right=true 为右键）。</summary>
    public static void MouseClick(int x, int y, bool rightButton = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(ClickDelayMs);
        var (down, up) = rightButton
            ? (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP)
            : (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
        Send(Mouse(INPUT_MOUSE, new INPUTUNION { mi = new MOUSEINPUT { dwFlags = down } }));
        Thread.Sleep(ClickDelayMs);
        Send(Mouse(INPUT_MOUSE, new INPUTUNION { mi = new MOUSEINPUT { dwFlags = up } }));
    }

    /// <summary>模拟键盘组合键（修饰键按下 → 主键按下/抬起 → 修饰键抬起）。</summary>
    public static void SendKeyCombo(uint vk, bool ctrl, bool shift, bool alt)
    {
        var seq = new List<INPUT>();
        if (ctrl) seq.Add(Key((uint)0xA2));               // VK_LCONTROL
        if (shift) seq.Add(Key((uint)0xA0));              // VK_LSHIFT
        if (alt) seq.Add(Key((uint)0xA4));                // VK_LMENU
        seq.Add(Key(vk));
        seq.Add(Key(vk, KEYEVENTF_KEYUP));
        if (alt) seq.Add(Key((uint)0xA4, KEYEVENTF_KEYUP));
        if (shift) seq.Add(Key((uint)0xA0, KEYEVENTF_KEYUP));
        if (ctrl) seq.Add(Key((uint)0xA2, KEYEVENTF_KEYUP));
        Send(seq.ToArray());
        Thread.Sleep(ClickDelayMs);
    }

    private static INPUT Mouse(uint type, INPUTUNION u) => new() { type = type, u = u };
    private static INPUT Key(uint vk, uint flags = 0)
        => new() { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } } };

    private static void Send(params INPUT[] inputs)
        => SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
}
