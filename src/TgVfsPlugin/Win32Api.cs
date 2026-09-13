using System;
using System.Runtime.InteropServices;

namespace TgVfsPlugin;

/// <summary>
/// Описание структур и констант Windows API (в частности для файловых операций), 
/// необходимых для взаимодействия с Total Commander.
/// </summary>
public static class Win32Api
{
    public const int MAX_PATH = 260;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const int INVALID_HANDLE_VALUE = -1;
    
    [DllImport("kernel32.dll")]
    public static extern void SetLastError(uint dwErrCode);

    public const uint ERROR_NO_MORE_FILES = 18;

    // WFX Plugin Return Codes
    public const int FS_FILE_OK = 0;
    public const int FS_FILE_EXISTS = 1;
    public const int FS_FILE_NOTFOUND = 2;
    public const int FS_FILE_READERROR = 3;
    public const int FS_FILE_WRITEERROR = 4;
    public const int FS_FILE_USERABORT = 5;
    public const int FS_FILE_NOTSUPPORTED = 6;
    public const int FS_FILE_EXISTSRESUMEALLOWED = 7;

    // WFX Copy Flags
    public const int FS_COPYFLAGS_OVERWRITE = 1;
    public const int FS_COPYFLAGS_RESUME = 2;
    public const int FS_COPYFLAGS_MOVE = 4;
    public const int FS_COPYFLAGS_EXISTS_SAMECASE = 8;
    public const int FS_COPYFLAGS_EXISTS_DIFFERENTCASE = 16;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int ProgressProc(int pluginNr, IntPtr sourceName, IntPtr targetName, int percentDone);

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_USER = 0x0400;

    public static void RefreshActivePanel()
    {
        IntPtr tcWindow = FindWindow("TTOTAL_CMD", null!);
        if (tcWindow != IntPtr.Zero)
        {
            // Run on a background thread with a small delay so FsExecuteFile returns first
            System.Threading.Tasks.Task.Delay(100).ContinueWith(_ => {
                PostMessage(tcWindow, WM_USER + 51, new IntPtr(540), IntPtr.Zero);
            });
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    public static void ChangeInactivePanelDir(string inactivePath)
    {
        IntPtr tcWindow = FindWindow("TTOTAL_CMD", null!);
        if (tcWindow == IntPtr.Zero) return;

        // По умолчанию считаем, что активна левая панель (самый частый кейс)
        bool isLeftPanelActive = true;
        try
        {
            uint threadId = GetWindowThreadProcessId(tcWindow, IntPtr.Zero);
            if (threadId != 0)
            {
                GUITHREADINFO gui = new GUITHREADINFO();
                gui.cbSize = Marshal.SizeOf<GUITHREADINFO>();
                if (GetGUIThreadInfo(threadId, ref gui) && gui.hwndFocus != IntPtr.Zero)
                {
                    if (GetWindowRect(tcWindow, out RECT tcRect) && GetWindowRect(gui.hwndFocus, out RECT focusRect))
                    {
                        int tcMidX = tcRect.Left + (tcRect.Right - tcRect.Left) / 2;
                        int focusCenterX = focusRect.Left + (focusRect.Right - focusRect.Left) / 2;
                        isLeftPanelActive = focusCenterX < tcMidX;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error determining active panel side: {ex.Message}");
        }

        Logger.Log($"ChangeInactivePanelDir: Active panel is {(isLeftPanelActive ? "Left" : "Right")}. Sending target directory to {(isLeftPanelActive ? "Right" : "Left")} panel.");

        // В Total Commander формат команды смены директории через WM_COPYDATA ('CD'):
        // "путь_левой\rпуть_правой\0"
        // Если активна левая панель -> меняем правую: "\r" + path + "\0"
        // Если активна правая панель -> меняем левую: path + "\r\0"
        string payload = isLeftPanelActive ? ("\r" + inactivePath + "\0") : (inactivePath + "\r\0");
        byte[] payloadBytes = System.Text.Encoding.Default.GetBytes(payload);
        
        IntPtr ptr = Marshal.AllocHGlobal(payloadBytes.Length);
        try
        {
            Marshal.Copy(payloadBytes, 0, ptr, payloadBytes.Length);

            COPYDATASTRUCT cds = new COPYDATASTRUCT();
            cds.dwData = new IntPtr('C' + ('D' << 8));
            cds.cbData = payloadBytes.Length; 
            cds.lpData = ptr;

            SendMessage(tcWindow, WM_COPYDATA, IntPtr.Zero, ref cds);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4)]
    public unsafe struct WIN32_FIND_DATAA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed byte cFileName[MAX_PATH];
        public fixed byte cAlternateFileName[14];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public unsafe struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed char cFileName[MAX_PATH];
        public fixed char cAlternateFileName[14];
    }
}
