using System;
using System.Runtime.InteropServices;

public class Program {
    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    public static void Main(string[] args) {
        IntPtr tc = FindWindow("TTOTAL_CMD", null);
        if (tc == IntPtr.Zero) {
            Console.WriteLine("TC not found");
            return;
        }

        string path = args.Length > 0 ? args[0] : @"c:\";
        string flags = args.Length > 1 ? args[1] : "T";
        
        string payload = "\r" + path + "\0" + flags + "\0";
        byte[] bytes = System.Text.Encoding.Default.GetBytes(payload);
        
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);

        COPYDATASTRUCT cds = new COPYDATASTRUCT();
        cds.dwData = new IntPtr('C' + ('D' << 8));
        cds.cbData = bytes.Length;
        cds.lpData = ptr;

        SendMessage(tc, 0x004A, IntPtr.Zero, ref cds);
        Marshal.FreeHGlobal(ptr);
        Console.WriteLine("Sent");
    }
}
