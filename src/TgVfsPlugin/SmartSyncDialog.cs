using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TgVfsPlugin;

public enum SyncItemStatus
{
    Identical,           // Файлы совпадают (размер и время изменения)
    LocalNewer,          // На ПК файл новее -> обновить в TG
    RemoteNewer,         // В Telegram файл новее -> обновить на ПК
    LocalOnly,           // Новый файл на диске ПК (отсутствует в VFS) -> загрузить в TG
    SizeMismatch,        // Даты совпадают, но размеры отличаются (конфликт / несовпадение)
    SourceNotFound,      // Локальный файл-источник на диске отсутствует
    NoSourceConfigured   // У элемента VFS в Контейнере отсутствует привязанный локальный путь
}

public class SmartSyncItem
{
    public VfsDatabase.FileRecord FileRecord { get; set; } = null!;
    public SyncItemStatus Status { get; set; }
    public string StatusText { get; set; } = "";
    public string DirectionSymbol { get; set; } = "";
    public string DirectionText { get; set; } = "";
    public long LocalSize { get; set; }
    public DateTime LocalWriteTime { get; set; }
    public string ToolTipDetails { get; set; } = "";
}

public static class SmartSyncDialog
{
    public static void Show(string channelName, long channelId, string mountId, string folderPath, VfsDatabase db)
    {
        FormExtensions.RunInSta(() =>
        {
            try
            {
                Win32Api.EnsureVisualStyles();
                using ToolTip toolTip = UiTheme.CreateToolTip();

                var mountInfo = db.GetMountById(mountId);
                bool isMirror = mountInfo != null && mountInfo.Mode == 0 && !string.IsNullOrEmpty(mountInfo.LocalPath);
                string modeTitle = isMirror ? "Зеркало" : "Контейнер";

                int initWidth = 1080;
                int initHeight = 620;
                bool initMaximized = false;

                if (int.TryParse(SettingsManager.GetSetting("smartsync_width"), out int savedW) && savedW >= 840) initWidth = savedW;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_height"), out int savedH) && savedH >= 520) initHeight = savedH;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_maximized"), out int savedMax) && savedMax == 1) initMaximized = true;

                int margin = 20;

                using Form form = new Form()
                {
                    ClientSize = new Size(initWidth, initHeight),
                    MinimumSize = new Size(880, 520),
                    FormBorderStyle = FormBorderStyle.Sizable,
                    Text = $"Умная синхронизация (Smart Sync — {modeTitle}) — \\{channelName}\\{(string.IsNullOrEmpty(folderPath) ? "" : folderPath)}",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = true,
                    MaximizeBox = true,
                    TopMost = false,
                    Font = UiTheme.DefaultFont
                };

                if (initMaximized)
                {
                    form.WindowState = FormWindowState.Maximized;
                }

                // Шапка
                Panel headerPanel = new Panel()
                {
                    Dock = DockStyle.Top,
                    Height = 65,
                    BackColor = UiTheme.HeaderBgColor
                };

                Label titleLabel = new Label()
                {
                    Left = margin,
                    Top = 10,
                    Width = 800,
                    Height = 22,
                    Text = isMirror
                        ? $"Синхронизация папки-зеркала: {mountInfo!.LocalPath}"
                        : "Сравнение элементов контейнера с оригиналами на ПК",
                    Font = UiTheme.HeaderTitleFont,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                Label subLabel = new Label()
                {
                    Left = margin,
                    Top = 35,
                    Width = 800,
                    Height = 22,
                    ForeColor = UiTheme.LabelForeColor,
                    Text = isMirror
                        ? "Двустороннее отслеживание изменений между структурой VFS и локальным диском"
                        : "Проверка актуальности элементов виртуальной подборки и оригиналов на дисках ПК",
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(subLabel);
                form.Controls.Add(headerPanel);

                // Нижняя панель для кнопок управления
                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                List<SmartSyncItem> items = new();

                int localNewerCount = 0;
                int remoteNewerCount = 0;
                int localOnlyCount = 0;
                int missingCount = 0;
                int identicalCount = 0;
                int mismatchCount = 0;
                int noSourceCount = 0;

                if (isMirror)
                {
                    // ----------------------------------------------------
                    // Двусторонняя логика для режима «Зеркало» (Mode == 0)
                    // ----------------------------------------------------
                    string mirrorRoot = mountInfo!.LocalPath!;
                    string targetSubFolder = string.IsNullOrEmpty(folderPath) ? "" : folderPath.Trim('\\', '/').Replace('/', '\\');
                    string targetLocalDir = string.IsNullOrEmpty(targetSubFolder) ? mirrorRoot : Path.Combine(mirrorRoot, targetSubFolder);

                    var vfsFiles = db.GetAllFilesRecursive(mountId, folderPath);
                    var vfsMap = new Dictionary<string, VfsDatabase.FileRecord>(StringComparer.OrdinalIgnoreCase);

                    foreach (var file in vfsFiles)
                    {
                        string relPath = string.IsNullOrEmpty(file.Parent) ? file.Name : Path.Combine(file.Parent, file.Name);
                        vfsMap[relPath] = file;

                        string expectedPath = Path.Combine(mirrorRoot, relPath);
                        file.SourcePath = expectedPath;
                    }

                    // Сканирование физического локального диска ПК
                    if (Directory.Exists(targetLocalDir))
                    {
                        try
                        {
                            var diskFiles = Directory.EnumerateFiles(targetLocalDir, "*", SearchOption.AllDirectories);
                            foreach (var diskPath in diskFiles)
                            {
                                string relPath = Path.GetRelativePath(mirrorRoot, diskPath);

                                if (!vfsMap.ContainsKey(relPath))
                                {
                                    // Новый файл на локальном диске, которого ещё нет в VFS
                                    try
                                    {
                                        var fi = new FileInfo(diskPath);
                                        string relDir = Path.GetDirectoryName(relPath) ?? "";
                                        string fileName = Path.GetFileName(diskPath);

                                        var syntheticRecord = new VfsDatabase.FileRecord
                                        {
                                            Uid = "",
                                            MountId = mountId,
                                            IsDir = false,
                                            Name = fileName,
                                            Parent = string.IsNullOrEmpty(relDir) ? null : relDir,
                                            MTime = fi.LastWriteTimeUtc,
                                            Size = fi.Length,
                                            TgMessageId = 0,
                                            InTrash = 0,
                                            Ver = 1,
                                            SourcePath = diskPath
                                        };

                                        var newItem = new SmartSyncItem
                                        {
                                            FileRecord = syntheticRecord,
                                            Status = SyncItemStatus.LocalOnly,
                                            StatusText = "Новый на ПК",
                                            DirectionSymbol = "-->>",
                                            DirectionText = "ПК -> Telegram",
                                            LocalSize = fi.Length,
                                            LocalWriteTime = fi.LastWriteTime,
                                            ToolTipDetails = $"[-->> Новый файл на локальном диске ПК]\n" +
                                                             $"• Отсутствует в VFS и Telegram\n" +
                                                             $"• Путь на ПК: {diskPath}\n" +
                                                             $"• Размер: {fi.Length:#,##0} байт\n" +
                                                             $"• Дата: {fi.LastWriteTime:dd.MM.yy HH:mm:ss}\n" +
                                                             $"(отметьте для выгрузки в Telegram)"
                                        };

                                        items.Add(newItem);
                                        localOnlyCount++;
                                    }
                                    catch
                                    {
                                        // Игнорируем файлы без прав доступа
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("UI", $"SmartSync disk scan error in '{targetLocalDir}': {ex.Message}");
                        }
                    }

                    // Обработка всех элементов VFS
                    foreach (var file in vfsFiles)
                    {
                        var item = new SmartSyncItem { FileRecord = file };
                        DateTime remoteLocalTime = file.MTime.ToLocalTime();
                        string expectedPath = file.SourcePath!;

                        if (!File.Exists(expectedPath))
                        {
                            item.Status = SyncItemStatus.RemoteNewer;
                            item.StatusText = "Отсутствует на ПК";
                            item.DirectionSymbol = "<<--";
                            item.DirectionText = "Telegram -> ПК";
                            item.ToolTipDetails = $"[<<-- Файл отсутствует на локальном диске ПК]\n" +
                                                  $"• Telegram: {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                  $"• Ожидаемый путь: {expectedPath}\n" +
                                                  $"(отметьте для скачивания из Telegram на ПК)";
                            remoteNewerCount++;
                        }
                        else
                        {
                            try
                            {
                                var fi = new FileInfo(expectedPath);
                                item.LocalSize = fi.Length;
                                item.LocalWriteTime = fi.LastWriteTime;

                                long localUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                                long remoteUnix = new DateTimeOffset(file.MTime.ToUniversalTime()).ToUnixTimeSeconds();
                                long diff = localUnix - remoteUnix;

                                if (Math.Abs(diff) <= 2)
                                {
                                    if (item.LocalSize == file.Size)
                                    {
                                        item.Status = SyncItemStatus.Identical;
                                        item.StatusText = "Идентичны";
                                        item.DirectionSymbol = "=";
                                        item.DirectionText = "Синхронизировано";
                                        item.ToolTipDetails = $"[= Идентичны]\n" +
                                                              $"• Дата: {remoteLocalTime:dd.MM.yy HH:mm:ss}\n" +
                                                              $"• Размер: {file.Size:#,##0} байт\n" +
                                                              $"• Источник: {expectedPath}";
                                        identicalCount++;
                                    }
                                    else
                                    {
                                        item.Status = SyncItemStatus.SizeMismatch;
                                        item.StatusText = "⚠️ Разный размер";
                                        item.DirectionSymbol = "≠";
                                        item.DirectionText = "Требует решения";
                                        item.ToolTipDetails = $"[⚠️ Несовпадение размеров при совпадающей дате]\n" +
                                                              $"• Диск ПК:  {item.LocalSize:#,##0} байт ({item.LocalWriteTime:dd.MM.yy HH:mm:ss})\n" +
                                                              $"• Telegram: {file.Size:#,##0} байт ({remoteLocalTime:dd.MM.yy HH:mm:ss})\n" +
                                                              $"• Разница размера: {Math.Abs(item.LocalSize - file.Size):#,##0} байт\n" +
                                                              $"• Источник: {expectedPath}";
                                        mismatchCount++;
                                    }
                                }
                                else if (diff > 2)
                                {
                                    item.Status = SyncItemStatus.LocalNewer;
                                    item.StatusText = "На ПК новее";
                                    item.DirectionSymbol = "-->>";
                                    item.DirectionText = "ПК -> Telegram";

                                    TimeSpan span = fi.LastWriteTimeUtc - file.MTime.ToUniversalTime();
                                    string diffStr = FormatTimeSpan(span);

                                    item.ToolTipDetails = $"[-->> На ПК новее] (ПК -->> Telegram)\n" +
                                                          $"• Диск ПК (новее): {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                          $"• Telegram:        {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                          $"• Опережение:      на {diffStr}\n" +
                                                          $"• Источник:        {expectedPath}";
                                    localNewerCount++;
                                }
                                else
                                {
                                    item.Status = SyncItemStatus.RemoteNewer;
                                    item.StatusText = "В TG новее";
                                    item.DirectionSymbol = "<<--";
                                    item.DirectionText = "Telegram -> ПК";

                                    TimeSpan span = file.MTime.ToUniversalTime() - fi.LastWriteTimeUtc;
                                    string diffStr = FormatTimeSpan(span);

                                    item.ToolTipDetails = $"[<<-- В Telegram новее] (Telegram <<-- ПК)\n" +
                                                          $"• Telegram (новее): {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                          $"• Диск ПК:          {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                          $"• Опережение:       на {diffStr}\n" +
                                                          $"• Источник:         {expectedPath}";
                                    remoteNewerCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                item.Status = SyncItemStatus.SourceNotFound;
                                item.StatusText = "Ошибка доступа";
                                item.DirectionSymbol = "❌";
                                item.DirectionText = "Пропуск";
                                item.ToolTipDetails = $"[❌ Ошибка доступа к файлу на ПК]\n• Ошибка: {ex.Message}\n• Путь: {expectedPath}";
                                missingCount++;
                            }
                        }

                        items.Add(item);
                    }
                }
                else
                {
                    // ----------------------------------------------------
                    // Точечная логика для режима «Контейнер» (Mode == 1)
                    // ----------------------------------------------------
                    var dbFiles = db.GetAllFilesRecursive(mountId, folderPath);

                    foreach (var file in dbFiles)
                    {
                        var item = new SmartSyncItem { FileRecord = file };
                        DateTime remoteLocalTime = file.MTime.ToLocalTime();

                        if (string.IsNullOrEmpty(file.SourcePath))
                        {
                            item.Status = SyncItemStatus.NoSourceConfigured;
                            item.StatusText = "Виртуальный";
                            item.DirectionSymbol = "❌";
                            item.DirectionText = "Пропуск";
                            item.ToolTipDetails = $"[❌ Нет источника на ПК]\nФайл создан в VFS и не привязан к локальному файлу.";
                            noSourceCount++;
                        }
                        else if (!File.Exists(file.SourcePath))
                        {
                            item.Status = SyncItemStatus.SourceNotFound;
                            item.StatusText = "Не найден на ПК";
                            item.DirectionSymbol = "❌";
                            item.DirectionText = "Пропуск";
                            item.ToolTipDetails = $"[❌ Файл-источник не найден на диске ПК]\n" +
                                                  $"• Telegram: {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                  $"• Ожидаемый путь: {file.SourcePath}\n" +
                                                  $"(диск отключен или файл удален)";
                            missingCount++;
                        }
                        else
                        {
                            try
                            {
                                var fi = new FileInfo(file.SourcePath);
                                item.LocalSize = fi.Length;
                                item.LocalWriteTime = fi.LastWriteTime;

                                long localUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                                long remoteUnix = new DateTimeOffset(file.MTime.ToUniversalTime()).ToUnixTimeSeconds();
                                long diff = localUnix - remoteUnix;

                                if (Math.Abs(diff) <= 2)
                                {
                                    if (item.LocalSize == file.Size)
                                    {
                                        item.Status = SyncItemStatus.Identical;
                                        item.StatusText = "Идентичны";
                                        item.DirectionSymbol = "=";
                                        item.DirectionText = "Синхронизировано";
                                        item.ToolTipDetails = $"[= Идентичны]\n" +
                                                              $"• Дата: {remoteLocalTime:dd.MM.yy HH:mm:ss}\n" +
                                                              $"• Размер: {file.Size:#,##0} байт\n" +
                                                              $"• Источник: {file.SourcePath}";
                                        identicalCount++;
                                    }
                                    else
                                    {
                                        item.Status = SyncItemStatus.SizeMismatch;
                                        item.StatusText = "⚠️ Разный размер";
                                        item.DirectionSymbol = "≠";
                                        item.DirectionText = "Требует решения";
                                        item.ToolTipDetails = $"[⚠️ Несовпадение размеров при совпадающей дате]\n" +
                                                              $"• Диск ПК:  {item.LocalSize:#,##0} байт ({item.LocalWriteTime:dd.MM.yy HH:mm:ss})\n" +
                                                              $"• Telegram: {file.Size:#,##0} байт ({remoteLocalTime:dd.MM.yy HH:mm:ss})\n" +
                                                              $"• Разница размера: {Math.Abs(item.LocalSize - file.Size):#,##0} байт\n" +
                                                              $"• Источник: {file.SourcePath}";
                                        mismatchCount++;
                                    }
                                }
                                else if (diff > 2)
                                {
                                    item.Status = SyncItemStatus.LocalNewer;
                                    item.StatusText = "На ПК новее";
                                    item.DirectionSymbol = "-->>";
                                    item.DirectionText = "ПК -> Telegram";

                                    TimeSpan span = fi.LastWriteTimeUtc - file.MTime.ToUniversalTime();
                                    string diffStr = FormatTimeSpan(span);

                                    item.ToolTipDetails = $"[-->> На ПК новее] (ПК -->> Telegram)\n" +
                                                          $"• Диск ПК (новее): {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                          $"• Telegram:        {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                          $"• Опережение:      на {diffStr}\n" +
                                                          $"• Источник:        {file.SourcePath}";
                                    localNewerCount++;
                                }
                                else
                                {
                                    item.Status = SyncItemStatus.RemoteNewer;
                                    item.StatusText = "В TG новее";
                                    item.DirectionSymbol = "<<--";
                                    item.DirectionText = "Telegram -> ПК";

                                    TimeSpan span = file.MTime.ToUniversalTime() - fi.LastWriteTimeUtc;
                                    string diffStr = FormatTimeSpan(span);

                                    item.ToolTipDetails = $"[<<-- В Telegram новее] (Telegram <<-- ПК)\n" +
                                                          $"• Telegram (новее): {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                          $"• Диск ПК:          {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                          $"• Опережение:       на {diffStr}\n" +
                                                          $"• Источник:         {file.SourcePath}";
                                    remoteNewerCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                item.Status = SyncItemStatus.SourceNotFound;
                                item.StatusText = "Ошибка доступа";
                                item.DirectionSymbol = "❌";
                                item.DirectionText = "Пропуск";
                                item.ToolTipDetails = $"[❌ Ошибка доступа к файлу на ПК]\n• Ошибка: {ex.Message}\n• Путь: {file.SourcePath}";
                                missingCount++;
                            }
                        }

                        items.Add(item);
                    }
                }

                // Информационная сводка
                string summaryText = $"Режим: {(isMirror ? "Зеркало" : "Контейнер")}  |  Элементов: {items.Count}  |  К обновлению: {localNewerCount + remoteNewerCount + localOnlyCount}  |  Идентичны: {identicalCount}";
                if (localOnlyCount > 0) summaryText += $"  |  Новых на ПК: {localOnlyCount}";
                if (mismatchCount > 0) summaryText += $"  |  Разный размер: {mismatchCount}";
                if (missingCount > 0) summaryText += $"  |  Не найдены на диске: {missingCount}";
                if (noSourceCount > 0) summaryText += $"  |  Без источника: {noSourceCount}";

                Label summaryLabel = new Label()
                {
                    Left = 20,
                    Top = 75,
                    Width = form.ClientSize.Width - 40,
                    Height = 22,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Text = summaryText
                };
                form.Controls.Add(summaryLabel);

                // Список файлов ListView
                ListView listView = new ListView()
                {
                    Left = 20,
                    Top = 105,
                    Width = form.ClientSize.Width - 40,
                    Height = form.ClientSize.Height - bottomPanel.Height - 115,
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    View = View.Details,
                    CheckBoxes = true,
                    FullRowSelect = true,
                    GridLines = true,
                    ShowItemToolTips = false
                };

                typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .SetValue(listView, true, null);

                int colVfsWidth = 260;
                int colTgSizeWidth = 85;
                int colTgDateWidth = 145;
                int colDirWidth = 60;
                int colPcDateWidth = 145;
                int colPcSizeWidth = 85;
                int colSrcWidth = 320;

                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_vfs"), out int cv) && cv >= 50) colVfsWidth = cv;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_tg_size"), out int ctg_s) && ctg_s >= 40) colTgSizeWidth = ctg_s;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_tg_date"), out int ctg_d) && ctg_d >= 60) colTgDateWidth = ctg_d;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_direction"), out int cd) && cd >= 30) colDirWidth = cd;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_pc_date"), out int cpc_d) && cpc_d >= 60) colPcDateWidth = cpc_d;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_pc_size"), out int cpc_s) && cpc_s >= 40) colPcSizeWidth = cpc_s;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_source"), out int csrc) && csrc >= 50) colSrcWidth = csrc;

                listView.Columns.Add("Путь в VFS", colVfsWidth, HorizontalAlignment.Left);
                listView.Columns.Add("Размер (TG)", colTgSizeWidth, HorizontalAlignment.Right);
                listView.Columns.Add("Дата (TG)", colTgDateWidth, HorizontalAlignment.Left);
                listView.Columns.Add("<=>", colDirWidth, HorizontalAlignment.Center);
                listView.Columns.Add("Дата (ПК)", colPcDateWidth, HorizontalAlignment.Left);
                listView.Columns.Add("Размер (ПК)", colPcSizeWidth, HorizontalAlignment.Right);
                listView.Columns.Add("Оригинал на ПК", colSrcWidth, HorizontalAlignment.Left);

                int rowIndex = 0;
                foreach (var item in items)
                {
                    string vfsDisplayPath = string.IsNullOrEmpty(item.FileRecord.Parent)
                        ? item.FileRecord.Name
                        : $"{item.FileRecord.Parent}\\{item.FileRecord.Name}";

                    string itemText = vfsDisplayPath;

                    string tgSizeText = item.Status == SyncItemStatus.LocalOnly ? "-" : item.FileRecord.Size.ToString("#,##0");
                    string tgDateText = item.Status == SyncItemStatus.LocalOnly ? "-" : item.FileRecord.MTime.ToLocalTime().ToString("dd.MM.yy HH:mm:ss");
                    string dirSymbol = item.DirectionSymbol;
                    string pcDateText = (item.Status == SyncItemStatus.SourceNotFound || item.Status == SyncItemStatus.NoSourceConfigured)
                        ? "-"
                        : item.LocalWriteTime.ToString("dd.MM.yy HH:mm:ss");
                    string pcSizeText = (item.Status == SyncItemStatus.SourceNotFound || item.Status == SyncItemStatus.NoSourceConfigured)
                        ? "-"
                        : item.LocalSize.ToString("#,##0");
                    string pcPathText = item.FileRecord.SourcePath ?? "";

                    var lvi = new ListViewItem(itemText);
                    lvi.SubItems.Add(tgSizeText);
                    lvi.SubItems.Add(tgDateText);
                    lvi.SubItems.Add(dirSymbol);
                    lvi.SubItems.Add(pcDateText);
                    lvi.SubItems.Add(pcSizeText);
                    lvi.SubItems.Add(pcPathText);
                    lvi.Tag = item;

                    lvi.UseItemStyleForSubItems = false;
                    Color rowFg;
                    Color rowBg;
                    Font rowFont;

                    if (item.Status == SyncItemStatus.LocalOnly)
                    {
                        lvi.Checked = true;
                        rowFg = Color.FromArgb(0, 120, 0);
                        rowBg = Color.FromArgb(235, 248, 235);
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else if (item.Status == SyncItemStatus.LocalNewer)
                    {
                        lvi.Checked = true;
                        rowFg = Color.FromArgb(0, 110, 0);
                        rowBg = Color.FromArgb(235, 248, 235);
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else if (item.Status == SyncItemStatus.RemoteNewer)
                    {
                        lvi.Checked = true;
                        rowFg = Color.FromArgb(0, 70, 180);
                        rowBg = Color.FromArgb(235, 244, 255);
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else if (item.Status == SyncItemStatus.SizeMismatch)
                    {
                        lvi.Checked = false;
                        rowFg = Color.FromArgb(180, 100, 0);
                        rowBg = Color.FromArgb(255, 247, 230);
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else if (item.Status == SyncItemStatus.SourceNotFound || item.Status == SyncItemStatus.NoSourceConfigured)
                    {
                        lvi.Checked = false;
                        rowFg = Color.FromArgb(170, 0, 0);
                        rowBg = Color.FromArgb(255, 235, 235);
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else
                    {
                        lvi.Checked = false;
                        rowFg = Color.FromArgb(90, 90, 90);
                        rowBg = (rowIndex % 2 == 0) ? Color.White : Color.FromArgb(248, 249, 250);
                        rowFont = new Font(listView.Font, FontStyle.Regular);
                    }

                    lvi.ForeColor = rowFg;
                    lvi.BackColor = rowBg;
                    lvi.Font = rowFont;

                    foreach (ListViewItem.ListViewSubItem sub in lvi.SubItems)
                    {
                        sub.ForeColor = rowFg;
                        sub.BackColor = rowBg;
                        sub.Font = rowFont;
                    }

                    lvi.SubItems[3].Font = new Font("Segoe UI", 14f, FontStyle.Bold);

                    listView.Items.Add(lvi);
                    rowIndex++;
                }

                ToolTip rowToolTip = new ToolTip()
                {
                    AutoPopDelay = 12000,
                    InitialDelay = 200,
                    ReshowDelay = 100,
                    ShowAlways = true,
                    UseFading = true,
                    UseAnimation = true
                };

                ListViewItem? lastHoveredItem = null;

                listView.MouseMove += (s, e) =>
                {
                    var hit = listView.HitTest(e.Location);
                    if (hit.Item != null)
                    {
                        if (hit.Item != lastHoveredItem)
                        {
                            lastHoveredItem = hit.Item;
                            if (hit.Item.Tag is SmartSyncItem syncItem && !string.IsNullOrEmpty(syncItem.ToolTipDetails))
                            {
                                rowToolTip.Show(syncItem.ToolTipDetails, listView, e.X + 16, e.Y + 16, 10000);
                            }
                            else
                            {
                                rowToolTip.Hide(listView);
                            }
                        }
                    }
                    else
                    {
                        if (lastHoveredItem != null)
                        {
                            lastHoveredItem = null;
                            rowToolTip.Hide(listView);
                        }
                    }
                };

                listView.ItemMouseHover += (s, e) =>
                {
                    if (e.Item != null && e.Item.Tag is SmartSyncItem syncItem && !string.IsNullOrEmpty(syncItem.ToolTipDetails))
                    {
                        Point mousePos = listView.PointToClient(Cursor.Position);
                        rowToolTip.Show(syncItem.ToolTipDetails, listView, mousePos.X + 16, mousePos.Y + 16, 10000);
                    }
                };

                listView.MouseLeave += (s, e) =>
                {
                    lastHoveredItem = null;
                    rowToolTip.Hide(listView);
                };

                listView.MouseDown += (s, e) =>
                {
                    rowToolTip.Hide(listView);
                };

                form.Controls.Add(listView);

                // Кнопки управления в нижней панели
                Button selectUpdatesBtn = UiTheme.CreateButton("Выбрать разные", "Отметить галочками все файлы, требующие синхронизации", toolTip, 130);
                selectUpdatesBtn.Left = margin;
                selectUpdatesBtn.Top = 11;
                selectUpdatesBtn.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                selectUpdatesBtn.Click += (s, e) =>
                {
                    foreach (ListViewItem lvi in listView.Items)
                    {
                        if (lvi.Tag is SmartSyncItem it)
                        {
                            lvi.Checked = (it.Status == SyncItemStatus.LocalNewer ||
                                           it.Status == SyncItemStatus.RemoteNewer ||
                                           it.Status == SyncItemStatus.LocalOnly);
                        }
                    }
                };

                Button clearSelectionBtn = UiTheme.CreateButton("Снять выбор", "Снять отметки выбора со всех файлов в списке", toolTip, 100);
                clearSelectionBtn.Left = selectUpdatesBtn.Right + 10;
                clearSelectionBtn.Top = 11;
                clearSelectionBtn.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                clearSelectionBtn.Click += (s, e) =>
                {
                    foreach (ListViewItem lvi in listView.Items) lvi.Checked = false;
                };

                Button closeBtn = UiTheme.CreateButton("Закрыть", "Закрыть окно синхронизации", toolTip, 85, dialogResult: DialogResult.Cancel);
                closeBtn.Left = form.ClientSize.Width - margin - closeBtn.Width;
                closeBtn.Top = 11;
                closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                Button syncBtn = UiTheme.CreateButton("Синхронизировать", "Запустить обновление всех отмеченных файлов", toolTip, 130);
                syncBtn.Left = closeBtn.Left - 10 - syncBtn.Width;
                syncBtn.Top = 11;
                syncBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                syncBtn.Click += async (s, e) =>
                {
                    syncBtn.Enabled = false;
                    closeBtn.Enabled = false;
                    selectUpdatesBtn.Enabled = false;
                    clearSelectionBtn.Enabled = false;

                    int updated = 0;
                    int errors = 0;

                    try
                    {
                        foreach (ListViewItem lvi in listView.Items)
                        {
                            if (!lvi.Checked || lvi.Tag is not SmartSyncItem it) continue;

                            if ((it.Status == SyncItemStatus.LocalNewer || it.Status == SyncItemStatus.LocalOnly) && !string.IsNullOrEmpty(it.FileRecord.SourcePath))
                            {
                                try
                                {
                                    if (File.Exists(it.FileRecord.SourcePath))
                                    {
                                        string caption = string.IsNullOrEmpty(it.FileRecord.Parent) ? it.FileRecord.Name : $"{it.FileRecord.Parent}\\{it.FileRecord.Name}";
                                        int msgId = await TelegramManager.UploadAndSendFileAsync(channelId, it.FileRecord.SourcePath, it.FileRecord.Name, caption);
                                        if (msgId > 0)
                                        {
                                            if (!string.IsNullOrEmpty(it.FileRecord.Uid))
                                            {
                                                db.MoveFileToTrash(it.FileRecord.Uid);
                                            }

                                            var fi = new FileInfo(it.FileRecord.SourcePath);
                                            db.AddFile(new VfsDatabase.FileRecord
                                            {
                                                Uid = Guid.NewGuid().ToString("N"),
                                                MountId = it.FileRecord.MountId,
                                                IsDir = false,
                                                Name = it.FileRecord.Name,
                                                Parent = it.FileRecord.Parent,
                                                MTime = fi.LastWriteTimeUtc,
                                                Size = fi.Length,
                                                TgMessageId = msgId,
                                                InTrash = 0,
                                                Ver = it.FileRecord.Ver + 1,
                                                SourcePath = it.FileRecord.SourcePath
                                            });

                                            it.Status = SyncItemStatus.Identical;
                                            it.StatusText = "Загружен в TG";
                                            it.DirectionSymbol = "=";
                                            it.DirectionText = "Синхронизировано";
                                            it.FileRecord.Size = fi.Length;
                                            it.FileRecord.MTime = fi.LastWriteTimeUtc;
                                            it.LocalSize = fi.Length;
                                            it.LocalWriteTime = fi.LastWriteTime;

                                            lvi.SubItems[1].Text = fi.Length.ToString("#,##0");
                                            lvi.SubItems[2].Text = fi.LastWriteTime.ToString("dd.MM.yy HH:mm:ss");
                                            lvi.SubItems[3].Text = "=";
                                            lvi.SubItems[4].Text = fi.LastWriteTime.ToString("dd.MM.yy HH:mm:ss");
                                            lvi.SubItems[5].Text = fi.Length.ToString("#,##0");

                                            it.ToolTipDetails = $"[= Идентичны (Синхронизировано)]\n" +
                                                                  $"• Дата: {fi.LastWriteTime:dd.MM.yy HH:mm:ss}\n" +
                                                                  $"• Размер: {fi.Length:#,##0} байт\n" +
                                                                  $"• Источник: {it.FileRecord.SourcePath}";

                                            lvi.Checked = false;
                                            lvi.ForeColor = Color.FromArgb(90, 90, 90);
                                            updated++;
                                        }
                                        else
                                        {
                                            errors++;
                                        }
                                    }
                                }
                                catch
                                {
                                    errors++;
                                }
                            }
                            else if (it.Status == SyncItemStatus.RemoteNewer && !string.IsNullOrEmpty(it.FileRecord.SourcePath))
                            {
                                try
                                {
                                    string dir = Path.GetDirectoryName(it.FileRecord.SourcePath) ?? "";
                                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                    {
                                        Directory.CreateDirectory(dir);
                                    }

                                    string tempFile = it.FileRecord.SourcePath + ".tmp_sync";
                                    await TelegramManager.DownloadFileAsync(channelId, it.FileRecord.TgMessageId, tempFile);
                                    if (File.Exists(tempFile))
                                    {
                                        if (File.Exists(it.FileRecord.SourcePath))
                                        {
                                            File.Delete(it.FileRecord.SourcePath);
                                        }
                                        File.Move(tempFile, it.FileRecord.SourcePath);
                                        File.SetLastWriteTimeUtc(it.FileRecord.SourcePath, it.FileRecord.MTime.ToUniversalTime());

                                        var fi = new FileInfo(it.FileRecord.SourcePath);
                                        it.Status = SyncItemStatus.Identical;
                                        it.StatusText = "Обновлен на ПК";
                                        it.DirectionSymbol = "=";
                                        it.DirectionText = "Синхронизировано";
                                        it.LocalSize = fi.Length;
                                        it.LocalWriteTime = fi.LastWriteTime;

                                        lvi.SubItems[1].Text = it.FileRecord.Size.ToString("#,##0");
                                        lvi.SubItems[2].Text = it.FileRecord.MTime.ToLocalTime().ToString("dd.MM.yy HH:mm:ss");
                                        lvi.SubItems[3].Text = "=";
                                        lvi.SubItems[4].Text = fi.LastWriteTime.ToString("dd.MM.yy HH:mm:ss");
                                        lvi.SubItems[5].Text = fi.Length.ToString("#,##0");

                                        it.ToolTipDetails = $"[= Идентичны (Синхронизировано)]\n" +
                                                              $"• Дата: {fi.LastWriteTime:dd.MM.yy HH:mm:ss}\n" +
                                                              $"• Размер: {fi.Length:#,##0} байт\n" +
                                                              $"• Источник: {it.FileRecord.SourcePath}";

                                        lvi.Checked = false;
                                        lvi.ForeColor = Color.FromArgb(90, 90, 90);
                                        updated++;
                                    }
                                    else
                                    {
                                        errors++;
                                    }
                                }
                                catch
                                {
                                    errors++;
                                }
                            }
                        }

                        Win32Api.RefreshActivePanel();
                        MessageBox.Show(form, $"Синхронизация завершена.\nОбработано элементов: {updated}\nОшибок: {errors}", "Smart Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    finally
                    {
                        syncBtn.Enabled = true;
                        closeBtn.Enabled = true;
                        selectUpdatesBtn.Enabled = true;
                        clearSelectionBtn.Enabled = true;
                    }
                };

                bottomPanel.Controls.Add(selectUpdatesBtn);
                bottomPanel.Controls.Add(clearSelectionBtn);
                bottomPanel.Controls.Add(syncBtn);
                bottomPanel.Controls.Add(closeBtn);
                form.CancelButton = closeBtn;

                form.FormClosing += (s, e) =>
                {
                    try
                    {
                        if (listView.Columns.Count >= 7)
                        {
                            SettingsManager.SaveSetting("smartsync_col_vfs", listView.Columns[0].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_tg_size", listView.Columns[1].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_tg_date", listView.Columns[2].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_direction", listView.Columns[3].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_pc_date", listView.Columns[4].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_pc_size", listView.Columns[5].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_source", listView.Columns[6].Width.ToString());
                        }

                        if (form.WindowState == FormWindowState.Maximized)
                        {
                            SettingsManager.SaveSetting("smartsync_maximized", "1");
                            SettingsManager.SaveSetting("smartsync_width", form.RestoreBounds.Width.ToString());
                            SettingsManager.SaveSetting("smartsync_height", form.RestoreBounds.Height.ToString());
                        }
                        else if (form.WindowState == FormWindowState.Normal)
                        {
                            SettingsManager.SaveSetting("smartsync_maximized", "0");
                            SettingsManager.SaveSetting("smartsync_width", form.Width.ToString());
                            SettingsManager.SaveSetting("smartsync_height", form.Height.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("UI", $"Failed to save SmartSyncDialog geometry: {ex.Message}");
                    }
                };

                form.ShowModalTc();
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "SmartSyncDialog exception", ex);
            }
        });
    }

    private static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            int days = (int)span.TotalDays;
            int hours = span.Hours;
            return hours > 0 ? $"{days} дн. {hours} ч." : $"{days} дн.";
        }
        if (span.TotalHours >= 1)
        {
            int hours = (int)span.TotalHours;
            int mins = span.Minutes;
            return mins > 0 ? $"{hours} ч. {mins} мин." : $"{hours} ч.";
        }
        if (span.TotalMinutes >= 1)
        {
            int mins = (int)span.TotalMinutes;
            int secs = span.Seconds;
            return secs > 0 ? $"{mins} мин. {secs} сек." : $"{mins} мин.";
        }
        return $"{(int)span.TotalSeconds} сек.";
    }
}
