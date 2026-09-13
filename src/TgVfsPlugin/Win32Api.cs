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
