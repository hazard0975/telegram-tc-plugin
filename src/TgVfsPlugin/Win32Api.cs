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
        // Формат WM_COPYDATA "CD" с поддержкой UTF-8
        // Необходимо добавить BOM (0xEF, 0xBB, 0xBF) в начало строки
        // И добавить флаг T для неактивной панели: "path\0T\0"
        
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(inactivePath);
        byte[] flagBytes = System.Text.Encoding.UTF8.GetBytes("T");
        
        // BOM (3 bytes) + pathBytes + null (1 byte) + flagBytes + null (1 byte)
        int totalLen = 3 + pathBytes.Length + 1 + flagBytes.Length + 1;
        
        IntPtr ptr = Marshal.AllocHGlobal(totalLen);
        try
        {
            // Write BOM
            Marshal.WriteByte(ptr, 0, 0xEF);
            Marshal.WriteByte(ptr, 1, 0xBB);
            Marshal.WriteByte(ptr, 2, 0xBF);
            
            // Write path
            Marshal.Copy(pathBytes, 0, IntPtr.Add(ptr, 3), pathBytes.Length);
            Marshal.WriteByte(ptr, 3 + pathBytes.Length, 0); // null after path
            
            // Write flag "T"
            Marshal.Copy(flagBytes, 0, IntPtr.Add(ptr, 3 + pathBytes.Length + 1), flagBytes.Length);
            Marshal.WriteByte(ptr, totalLen - 1, 0); // final null
            
            COPYDATASTRUCT cds = new COPYDATASTRUCT();
            cds.dwData = new IntPtr('C' + ('D' << 8));
            cds.cbData = totalLen;
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
