using System;
using System.Runtime.InteropServices;
using System.Text;

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

    // WFX Execute File Return Codes
    public const int FS_EXEC_OK = 0;
    public const int FS_EXEC_ERROR = 1;
    public const int FS_EXEC_YOURSELF = -1;
    public const int FS_EXEC_SYMLINK = 2;

    // WFX Copy Flags
    public const int FS_COPYFLAGS_OVERWRITE = 1;
    public const int FS_COPYFLAGS_RESUME = 2;
    public const int FS_COPYFLAGS_MOVE = 4;
    public const int FS_COPYFLAGS_EXISTS_SAMECASE = 8;
    public const int FS_COPYFLAGS_EXISTS_DIFFERENTCASE = 16;

    // Background transfer flags for FsGetBackgroundFlags
    public const int BG_DOWNLOAD = 1;
    public const int BG_UPLOAD = 2;
    public const int BG_ASK_USER = 4;

    // FsStatusInfo constants
    public const int FS_STATUS_START = 0;
    public const int FS_STATUS_END = 1;

    public const int FS_STATUS_OP_LIST = 1;
    public const int FS_STATUS_OP_GET_SINGLE = 2;
    public const int FS_STATUS_OP_GET_MULTI = 3;
    public const int FS_STATUS_OP_PUT_SINGLE = 4;
    public const int FS_STATUS_OP_PUT_MULTI = 5;
    public const int FS_STATUS_OP_RENMOV_SINGLE = 6;
    public const int FS_STATUS_OP_RENMOV_MULTI = 7;
    public const int FS_STATUS_OP_DELETE = 8;
    public const int FS_STATUS_OP_ATTRIB = 9;
    public const int FS_STATUS_OP_MKDIR = 10;
    public const int FS_STATUS_OP_EXEC = 11;
    public const int FS_STATUS_OP_CALCSIZE = 12;
    public const int FS_STATUS_OP_SEARCH = 13;
    public const int FS_STATUS_OP_SEARCH_TEXT = 14;
    public const int FS_STATUS_OP_SYNC_SEARCH = 15;
    public const int FS_STATUS_OP_SYNC_GET = 16;
    public const int FS_STATUS_OP_SYNC_PUT = 17;
    public const int FS_STATUS_OP_SYNC_DELETE = 18;
    public const int FS_STATUS_OP_GET_MULTI_THREAD = 19;
    public const int FS_STATUS_OP_PUT_MULTI_THREAD = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteInfoStruct
    {
        public uint SizeLow;
        public uint SizeHigh;
        public FILETIME LastWriteTime;
        public int Attr;
    }

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
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Проверяет, нажата ли кнопка "Пауза" в диалоговом окне Total Commander.
    /// В Total Commander при нажатии на кнопку "Пауза" ее текст переключается
    /// на "Продолжить" / "Resume" / "Возобновить" (или появляется такая кнопка).
    /// </summary>
    public static bool IsTotalCommanderPaused()
    {
        try
        {
            IntPtr tcWindow = FindWindow("TTOTAL_CMD", null!);
            if (tcWindow == IntPtr.Zero) return false;

            GetWindowThreadProcessId(tcWindow, out uint tcPid);
            if (tcPid == 0) return false;

            bool isPaused = false;
            var sb = new System.Text.StringBuilder(256);

            // Перечисляем все окна верхнего уровня в системе
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint wndPid);
                if (wndPid != tcPid) return true; // Ищем окна только процесса TC

                // Для каждого окна TC ищем дочерние (кнопки)
                EnumChildWindows(hWnd, (childHwnd, childLParam) =>
                {
                    sb.Clear();
                    int len = GetWindowText(childHwnd, sb, 256);
                    if (len > 0)
                    {
                        string text = sb.ToString().Trim();
                        // Убираем возможные амперсанды (hotkeys), например "&Resume"
                        text = text.Replace("&", "");
                        
                        // Logger.Log($"Found button text: '{text}' in process {wndPid}");
                        
                        // Тексты кнопок снятия с паузы в русской, английской, немецкой и других локализациях TC
                        if (text.Equals("Продолжить", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Возобновить", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Resume", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Weiter", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("Продолж", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("Возобн", StringComparison.OrdinalIgnoreCase))
                        {
                            isPaused = true;
                            return false; // нашли, останавливаем перечисление дочерних
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                return !isPaused; // если нашли, останавливаем и перечисление окон верхнего уровня
            }, IntPtr.Zero);

            return isPaused;
        }
        catch
        {
            return false;
        }
    }

    public static void ChangeInactivePanelDir(string inactivePath)
    {
        IntPtr tcWindow = FindWindow("TTOTAL_CMD", null!);
        if (tcWindow == IntPtr.Zero) return;

        // Определяем, на какой стороне (слева или справа) открыт VFS-плагин
        bool isLeftVfs = false;
        bool isRightVfs = false;
        bool detectedByPathBox = false;

        try
        {
            if (GetWindowRect(tcWindow, out RECT tcRect))
            {
                int tcMidX = tcRect.Left + (tcRect.Right - tcRect.Left) / 2;

                EnumChildWindows(tcWindow, (hWnd, lParam) =>
                {
                    StringBuilder clsSb = new StringBuilder(256);
                    GetClassName(hWnd, clsSb, clsSb.Capacity);
                    string clsName = clsSb.ToString();

                    if (clsName.Contains("PathBox", StringComparison.OrdinalIgnoreCase) ||
                        clsName.Contains("TMyPath", StringComparison.OrdinalIgnoreCase))
                    {
                        StringBuilder textSb = new StringBuilder(512);
                        GetWindowText(hWnd, textSb, textSb.Capacity);
                        string text = textSb.ToString().Trim();

                        if (!string.IsNullOrEmpty(text))
                        {
                            if (GetWindowRect(hWnd, out RECT boxRect))
                            {
                                int boxCenterX = boxRect.Left + (boxRect.Right - boxRect.Left) / 2;
                                bool isLeftBox = boxCenterX < tcMidX;

                                bool isPluginPath = text.StartsWith("\\\\", StringComparison.Ordinal) ||
                                                   text.StartsWith("/", StringComparison.Ordinal) ||
                                                   text.Contains("tgvfs", StringComparison.OrdinalIgnoreCase) ||
                                                   text.Contains("Telegram", StringComparison.OrdinalIgnoreCase);

                                if (isLeftBox && isPluginPath)
                                {
                                    isLeftVfs = true;
                                    detectedByPathBox = true;
                                }
                                else if (!isLeftBox && isPluginPath)
                                {
                                    isRightVfs = true;
                                    detectedByPathBox = true;
                                }
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error inspecting TC PathBox controls: {ex.Message}");
        }

        bool isLeftPanelActive = true;
        if (detectedByPathBox)
        {
            // Если плагин открыт в Левой панели -> целевая неактивная панель Правая (isLeftPanelActive = true)
            // Если плагин открыт в Правой панели -> целевая неактивная панель Левая (isLeftPanelActive = false)
            if (isLeftVfs && !isRightVfs)
            {
                isLeftPanelActive = true;
            }
            else if (isRightVfs && !isLeftVfs)
            {
                isLeftPanelActive = false;
            }
        }
        else
        {
            // Резервное определение по фокусу ввода GUI
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
        }

        Logger.Log($"ChangeInactivePanelDir: Detected VFS side={(detectedByPathBox ? (isLeftVfs ? "Left" : "Right") : "ByFocus")}. Sending target directory '{inactivePath}' to {(isLeftPanelActive ? "Right" : "Left")} panel.");

        // В Total Commander формат команды смены директории через WM_COPYDATA ('CD'):
        // Поддержка Unicode (кириллицы и спецсимволов) с версии TC 7.50+: префикс UTF-8 BOM (0xEF, 0xBB, 0xBF) перед путем.
        // Если VFS слева -> меняем правую: "\r" + BOM + path + "\0"
        // Если VFS справа -> меняем левую: BOM + path + "\r\0"
        byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(inactivePath);

        byte[] payloadBytes;
        using (var ms = new System.IO.MemoryStream())
        {
            if (isLeftPanelActive)
            {
                ms.WriteByte((byte)'\r');
                ms.Write(bom, 0, bom.Length);
                ms.Write(pathBytes, 0, pathBytes.Length);
                ms.WriteByte(0);
            }
            else
            {
                ms.Write(bom, 0, bom.Length);
                ms.Write(pathBytes, 0, pathBytes.Length);
                ms.WriteByte((byte)'\r');
                ms.WriteByte(0);
            }
            payloadBytes = ms.ToArray();
        }
        
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
