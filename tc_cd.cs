using System;
using System.Runtime.InteropServices;
public class Program {
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    public static void Main() {}
}
