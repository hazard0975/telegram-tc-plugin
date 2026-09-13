using System;
using System.Runtime.InteropServices;

public class Program {
    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }
    
    // just dummy checks. Total commander sends \r for left/right split
}
