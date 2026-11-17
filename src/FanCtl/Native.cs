using System.Runtime.InteropServices;

namespace FanCtl;

internal static unsafe partial class Native
{
    public const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016,
        WM_TIMER = 0x0113, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203,
        WM_POWERBROADCAST = 0x0218, WM_APP = 0x8000, WM_QUIT = 0x0012;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x12;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_GRAYED = 1;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80;
    public const uint QS_ALLINPUT = 0x04FF, PM_REMOVE = 1, INFINITE = 0xFFFFFFFF;
    public const uint IMAGE_ICON = 1, LR_DEFAULTSIZE = 0x40, LR_SHARED = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("user32.dll")] public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint h);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PeekMessageW(MSG* m, nint h, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(MSG* m);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(MSG* m);
    [LibraryImport("user32.dll")] public static partial uint MsgWaitForMultipleObjects(uint count, nint* handles, int waitAll, uint ms, uint mask);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint h, nuint id);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AppendMenuW(nint m, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint m, uint flags, int x, int y, int r, nint h, nint rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint m);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(POINT* p);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string s);
    [LibraryImport("user32.dll")] public static partial nint LoadImageW(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial nint LoadIconW(nint inst, nint name);
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* data);
    [LibraryImport("kernel32.dll")] public static partial nint GetModuleHandleW(nint name);
    [LibraryImport("kernel32.dll")] public static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSizeEx(nint proc, nint min, nint max, uint flags);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessInformation(nint proc, int cls, void* info, uint size);

    public static void SetTip(ref NOTIFYICONDATAW d, string s)
    {
        fixed (char* p = d.szTip)
        {
            int n = Math.Min(s.Length, 127);
            for (int i = 0; i < n; i++) p[i] = s[i];
            p[n] = '\0';
        }
    }

    /// <summary>Release resident pages back to the OS (they fault back in cheaply when touched).</summary>
    public static void TrimWorkingSet() => SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);

    /// <summary>Opt into EcoQoS (efficiency mode): low-priority, power-efficient scheduling.</summary>
    public static void EnableEcoQoS()
    {
        // PROCESS_POWER_THROTTLING_STATE { Version=1, ControlMask=EXECUTION_SPEED(1), StateMask=1 }
        uint* s = stackalloc uint[3] { 1, 1, 1 };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, s, 12);
    }
}
