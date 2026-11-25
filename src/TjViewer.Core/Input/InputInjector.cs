using System.Runtime.InteropServices;
using static TjViewer.Core.Input.NativeMethods;

namespace TjViewer.Core.Input;

public enum MouseButton { Left, Right, Middle }

/// <summary>
/// 键鼠注入（见 PRD FR-06）。按键用扫描码兼容快捷键；文本用 Unicode 字符注入避免键盘布局差异。
/// 注意：UAC 安全桌面/锁屏下普通权限无法注入；操控管理员窗口需被控端以管理员运行。
/// </summary>
public static class InputInjector
{
    /// <summary>绝对坐标移动（像素坐标归一化到 0..65535）。screenWidth/Height 为目标桌面尺寸。</summary>
    public static void MoveMouseAbsolute(int x, int y, int screenWidth, int screenHeight)
    {
        if (screenWidth <= 1 || screenHeight <= 1) return;
        int nx = (int)Math.Round(x * 65535.0 / (screenWidth - 1));
        int ny = (int)Math.Round(y * 65535.0 / (screenHeight - 1));
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dx = nx;
        input.U.mi.dy = ny;
        input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        Send(input);
    }

    public static void MouseButtonEvent(MouseButton button, bool down)
    {
        uint flag = (button, down) switch
        {
            (Input.MouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (Input.MouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (Input.MouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (Input.MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (Input.MouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            (Input.MouseButton.Middle, false) => MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };
        if (flag == 0) return;
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dwFlags = flag;
        Send(input);
    }

    /// <summary>滚轮：delta 正向上、负向下（WHEEL_DELTA=120 的倍数）。</summary>
    public static void Wheel(int delta)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.mouseData = unchecked((uint)delta);
        input.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
        Send(input);
    }

    /// <summary>扫描码按键。extended 标记扩展键（方向键/右 Ctrl 等）。</summary>
    public static void ScanCode(ushort scanCode, bool down, bool extended = false)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki.wScan = scanCode;
        input.U.ki.dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
        Send(input);
    }

    /// <summary>文本注入：逐字符 Unicode，规避键盘布局/输入法差异。</summary>
    public static void Text(string text)
    {
        foreach (var ch in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD };
            down.U.ki.wScan = ch;
            down.U.ki.dwFlags = KEYEVENTF_UNICODE;
            Send(down);

            var up = new INPUT { type = INPUT_KEYBOARD };
            up.U.ki.wScan = ch;
            up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            Send(up);
        }
    }

    private static void Send(INPUT input)
        => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
}
