using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace TgVfsPlugin;

/// <summary>
/// Экспортируемые функции для Total Commander (WFX API).
/// Вызываются через Native AOT.
/// </summary>
public static unsafe class WfxExports
{
    private static VfsDatabase? _db;

    // Обязательная функция: инициализация плагина
    [UnmanagedCallersOnly(EntryPoint = "FsInit", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsInit(int pluginNumber, IntPtr pProgressProc, IntPtr pLogProc, IntPtr pRequestProc)
    {
        try
        {
            // Инициализируем базу данных при запуске плагина
            if (_db == null)
            {
                _db = new VfsDatabase();
            }
        }
        catch (Exception)
        {
            // В реальном проекте тут нужно логирование, пока игнорируем
        }
        return 0; // успех
    }

    // Обязательная функция: начало поиска файлов (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsFindFirst", CallConvs = [typeof(CallConvStdcall)])]
    public static IntPtr FsFindFirst(byte* path, IntPtr findFileData)
    {
        string pathStr = Marshal.PtrToStringAnsi((IntPtr)path) ?? "";

        // Если это запрос корня, отдаем одну фиктивную папку
        if (pathStr == "\\" || pathStr == "/")
        {
            Win32Api.WIN32_FIND_DATAA* data = (Win32Api.WIN32_FIND_DATAA*)findFileData;
            *data = default;
            data->dwFileAttributes = Win32Api.FILE_ATTRIBUTE_DIRECTORY;
            
            byte[] nameBytes = System.Text.Encoding.Default.GetBytes("My Channel");
            for (int i = 0; i < nameBytes.Length && i < Win32Api.MAX_PATH - 1; i++)
            {
                data->cFileName[i] = nameBytes[i];
            }
            
            return new IntPtr(1); // Фиктивный handle для TC
        }

        return new IntPtr(Win32Api.INVALID_HANDLE_VALUE);
    }

    // Обязательная функция: продолжение поиска файлов (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsFindNext", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsFindNext(IntPtr hdl, IntPtr findFileData)
    {
        return 0; // false (больше нет файлов)
    }

    // Обязательная функция: начало поиска файлов (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsFindFirstW", CallConvs = [typeof(CallConvStdcall)])]
    public static IntPtr FsFindFirstW(char* path, IntPtr findFileData)
    {
        string pathStr = Marshal.PtrToStringUni((IntPtr)path) ?? "";

        // Если это запрос корня, отдаем одну фиктивную папку
        if (pathStr == "\\" || pathStr == "/")
        {
            Win32Api.WIN32_FIND_DATAW* data = (Win32Api.WIN32_FIND_DATAW*)findFileData;
            *data = default;
            data->dwFileAttributes = Win32Api.FILE_ATTRIBUTE_DIRECTORY;
            
            string name = "My Channel";
            for (int i = 0; i < name.Length && i < Win32Api.MAX_PATH - 1; i++)
            {
                data->cFileName[i] = name[i];
            }
            
            return new IntPtr(1); // Фиктивный handle для TC
        }

        return new IntPtr(Win32Api.INVALID_HANDLE_VALUE);
    }

    // Обязательная функция: продолжение поиска файлов (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsFindNextW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsFindNextW(IntPtr hdl, IntPtr findFileData)
    {
        return 0; // false (больше нет файлов)
    }

    // Обязательная функция: завершение поиска
    [UnmanagedCallersOnly(EntryPoint = "FsFindClose", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsFindClose(IntPtr hdl)
    {
        return 0; // успех
    }
}
