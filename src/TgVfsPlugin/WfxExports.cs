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
    // Обязательная функция: инициализация плагина
    [UnmanagedCallersOnly(EntryPoint = "FsInit", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsInit(int pluginNumber, IntPtr pProgressProc, IntPtr pLogProc, IntPtr pRequestProc)
    {
        // Пока просто возвращаем 0 (успех)
        return 0;
    }

    // Обязательная функция: начало поиска файлов
    [UnmanagedCallersOnly(EntryPoint = "FsFindFirst", CallConvs = [typeof(CallConvStdcall)])]
    public static IntPtr FsFindFirst(byte* path, IntPtr findFileData)
    {
        // Возвращаем -1 (INVALID_HANDLE_VALUE), чтобы TC видел пустую папку (пока нет БД)
        return new IntPtr(-1); 
    }

    // Обязательная функция: продолжение поиска файлов
    [UnmanagedCallersOnly(EntryPoint = "FsFindNext", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsFindNext(IntPtr hdl, IntPtr findFileData)
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
