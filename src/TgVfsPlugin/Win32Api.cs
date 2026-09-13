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

    public static void ChangeInactivePanelDir(string inactivePath)
    {
        IntPtr tcWindow = FindWindow("TTOTAL_CMD", null!);
        if (tcWindow == IntPtr.Zero) return;

        // Для смены папки только в неактивной панели в Total Commander формат: "путь_левой\rпуть_правой\0"
        // По результатам тестов пользователя, команда CD в консоли с флагом /T работает с багами (добавляет лишние слеши и пробелы).
        // Поэтому вместо отправки сырого CD через WM_COPYDATA, который страдает от тех же проблем парсинга,
        // мы можем использовать другой способ: команду EMCD (пользовательские команды),
        // либо старый-добрый разделитель \r для левой и правой панели.
        
        // В документации TC есть четкий формат для левой и правой панели (без флагох S/T, которые глючат):
        // "путь_левой\rпуть_правой\0"
        // Если мы хотим изменить только правую панель: "\rпуть_правой\0"
        // Если только левую: "путь_левой\r\0"
        
        // В 90% случаев пользователь заходит в папку-зеркало в левой панели, а правая неактивна.
        // Поэтому для тестов давайте отправим команду на смену ИМЕННО ПРАВОЙ панели.
        string payload = "\r" + inactivePath + "\0";
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
