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
    SizeMismatch,        // Даты совпадают, но размеры отличаются (конфликт / неоднозначность)
    SourceNotFound       // Локальный файл-источник на диске отсутствует
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

                int initWidth = 1080;
                int initHeight = 620;
                bool initMaximized = false;

                if (int.TryParse(SettingsManager.GetSetting("smartsync_width"), out int savedW) && savedW >= 840) initWidth = savedW;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_height"), out int savedH) && savedH >= 520) initHeight = savedH;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_maximized"), out int savedMax) && savedMax == 1) initMaximized = true;

                using Form form = new Form()
                {
                    Width = initWidth,
                    Height = initHeight,
                    MinimumSize = new Size(880, 520),
                    FormBorderStyle = FormBorderStyle.Sizable,
                    Text = $"Умная синхронизация (Smart Sync) — \\{channelName}\\{(string.IsNullOrEmpty(folderPath) ? "" : folderPath)}",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = true,
                    MaximizeBox = true,
                    TopMost = false,
                    Font = new Font("Segoe UI", 9)
                };

                if (initMaximized)
                {
                    form.WindowState = FormWindowState.Maximized;
                }

                // Шапка
                Panel headerPanel = new Panel()
                {
                    Left = 0,
                    Top = 0,
                    Width = 840,
                    Height = 65,
                    Dock = DockStyle.Top,
                    BackColor = Color.FromArgb(245, 247, 250)
                };

                Label titleLabel = new Label()
                {
                    Left = 20,
                    Top = 10,
                    Width = 800,
                    Height = 22,
                    Text = "Сравнение версий с локальными оригиналами на ПК",
                    Font = new Font("Segoe UI", 11, FontStyle.Bold)
                };

                Label subLabel = new Label()
                {
                    Left = 20,
                    Top = 35,
                    Width = 800,
                    Height = 20,
                    ForeColor = Color.FromArgb(90, 90, 90),
                    Text = "Отслеживание актуальности файлов виртуальной подборки и оригинальных файлов на дисках ПК"
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(subLabel);
                form.Controls.Add(headerPanel);

                // Сканирование элементов из БД
                var dbFiles = db.GetFilesWithSourcePathRecursive(mountId, folderPath);
                List<SmartSyncItem> items = new();

                int localNewerCount = 0;
                int remoteNewerCount = 0;
                int missingCount = 0;
                int identicalCount = 0;
                int mismatchCount = 0;

                foreach (var file in dbFiles)
                {
                    if (string.IsNullOrEmpty(file.SourcePath)) continue;

                    var item = new SmartSyncItem { FileRecord = file };
                    string vfsFileName = file.Name;
                    string srcFileName = Path.GetFileName(file.SourcePath);
                    bool isRenamed = !string.IsNullOrEmpty(srcFileName) &&
                                     !string.Equals(vfsFileName, srcFileName, StringComparison.OrdinalIgnoreCase);

                    string renameHeader = isRenamed ? $"🏷️ [Файл переименован в VFS]\nОригинальное имя на ПК: {srcFileName}\n\n" : "";

                    DateTime remoteLocalTime = file.MTime.ToLocalTime();

                    if (!File.Exists(file.SourcePath))
                    {
                        item.Status = SyncItemStatus.SourceNotFound;
                        item.StatusText = "Не найден на ПК";
                        item.DirectionSymbol = "❌";
                        item.DirectionText = "Пропуск";
                        item.ToolTipDetails = $"{renameHeader}[❌ Файл-источник не найден на диске ПК]\n" +
                                              $"• Telegram: {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                              $"• Ожидаемый путь на ПК: {file.SourcePath}\n" +
                                              $"(возможно, диск отключен или файл был удален/перемещен)";
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
                                // Даты совпадают в пределах 2 сек
                                if (item.LocalSize == file.Size)
                                {
                                    item.Status = SyncItemStatus.Identical;
                                    item.StatusText = "Идентичны";
                                    item.DirectionSymbol = "=";
                                    item.DirectionText = "Синхронизировано";
                                    item.ToolTipDetails = $"{renameHeader}[= Идентичны]\n" +
                                                          $"• Дата: {remoteLocalTime:dd.MM.yy HH:mm:ss}\n" +
                                                          $"• Размер: {file.Size:#,##0} байт\n" +
                                                          $"• Источник: {file.SourcePath}";
                                    identicalCount++;
                                }
                                else
                                {
                                    // Даты равны, но размеры отличаются (конфликт / несовпадение размеров)
                                    item.Status = SyncItemStatus.SizeMismatch;
                                    item.StatusText = "⚠️ Разный размер";
                                    item.DirectionSymbol = "≠";
                                    item.DirectionText = "Требует решения";
                                    item.ToolTipDetails = $"{renameHeader}[⚠️ Несовпадение размеров при совпадающей дате]\n" +
                                                          $"• Диск ПК:  {item.LocalSize:#,##0} байт ({item.LocalWriteTime:dd.MM.yy HH:mm:ss})\n" +
                                                          $"• Telegram: {file.Size:#,##0} байт ({remoteLocalTime:dd.MM.yy HH:mm:ss})\n" +
                                                          $"• Разница размера: {Math.Abs(item.LocalSize - file.Size):#,##0} байт\n" +
                                                          $"• Источник: {file.SourcePath}";
                                    mismatchCount++;
                                }
                            }
                            else if (diff > 2)
                            {
                                // Файл на ПК свежее, чем в Telegram
                                item.Status = SyncItemStatus.LocalNewer;
                                item.StatusText = "На ПК новее";
                                item.DirectionSymbol = "-->>";
                                item.DirectionText = "ПК -> Telegram";

                                TimeSpan span = fi.LastWriteTimeUtc - file.MTime.ToUniversalTime();
                                string diffStr = FormatTimeSpan(span);

                                item.ToolTipDetails = $"{renameHeader}[-->> На ПК новее] (ПК -->> Telegram)\n" +
                                                      $"• Диск ПК (новее): {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                      $"• Telegram:        {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                      $"• Опережение:      на {diffStr}\n" +
                                                      $"• Источник:        {file.SourcePath}";
                                localNewerCount++;
                            }
                            else
                            {
                                // Файл в Telegram свежее, чем на ПК (diff < -2)
                                item.Status = SyncItemStatus.RemoteNewer;
                                item.StatusText = "В TG новее";
                                item.DirectionSymbol = "<<--";
                                item.DirectionText = "Telegram -> ПК";

                                TimeSpan span = file.MTime.ToUniversalTime() - fi.LastWriteTimeUtc;
                                string diffStr = FormatTimeSpan(span);

                                item.ToolTipDetails = $"{renameHeader}[<<-- В Telegram новее] (Telegram <<-- ПК)\n" +
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
                            item.ToolTipDetails = $"{renameHeader}[❌ Ошибка доступа к файлу на ПК]\n" +
                                                  $"• Ошибка: {ex.Message}\n" +
                                                  $"• Путь: {file.SourcePath}";
                            missingCount++;
                        }
                    }
                    items.Add(item);
                }

                // Информационная сводка
                string summaryText = $"Файлов с источником: {items.Count}  |  Требуют обновления: {localNewerCount + remoteNewerCount}  |  Идентичны: {identicalCount}";
                if (mismatchCount > 0)
                {
                    summaryText += $"  |  Разный размер: {mismatchCount}";
                }
                if (missingCount > 0)
                {
                    summaryText += $"  |  Не найдены на диске: {missingCount}";
                }

                Label summaryLabel = new Label()
                {
                    Left = 20,
                    Top = 75,
                    Width = 790,
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
                    Width = 1000,
                    Height = 400,
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    View = View.Details,
                    CheckBoxes = true,
                    FullRowSelect = true,
                    GridLines = true,
                    ShowItemToolTips = false // Отключаем встроенные, так как используем наш точный многострочный ToolTip для всех ячеек
                };

                // Включаем двойную буферизацию для устранения мерцания при ресайзе и растягивании колонок
                typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .SetValue(listView, true, null);

                // Загружаем сохраненную ширину 7 колонок
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

                // 7 колонок в стиле Total Commander
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

                    string vfsFileName = item.FileRecord.Name;
                    string srcFileName = !string.IsNullOrEmpty(item.FileRecord.SourcePath)
                        ? Path.GetFileName(item.FileRecord.SourcePath)
                        : "";
                    bool isRenamed = !string.IsNullOrEmpty(srcFileName) &&
                                     !string.Equals(vfsFileName, srcFileName, StringComparison.OrdinalIgnoreCase);

                    string itemText = isRenamed ? $"🏷️ {vfsDisplayPath}" : vfsDisplayPath;

                    string tgSizeText = item.FileRecord.Size.ToString("#,##0");
                    string tgDateText = item.FileRecord.MTime.ToLocalTime().ToString("dd.MM.yy HH:mm:ss");
                    string dirSymbol = item.DirectionSymbol;
                    string pcDateText = item.Status == SyncItemStatus.SourceNotFound ? "-" : item.LocalWriteTime.ToString("dd.MM.yy HH:mm:ss");
                    string pcSizeText = item.Status == SyncItemStatus.SourceNotFound ? "-" : item.LocalSize.ToString("#,##0");
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

                    if (item.Status == SyncItemStatus.LocalNewer)
                    {
                        lvi.Checked = true;
                        rowFg = Color.FromArgb(0, 110, 0); // Зеленый цвет Total Commander
                        rowBg = Color.FromArgb(235, 248, 235); // Мягкий светло-зеленый фон
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else if (item.Status == SyncItemStatus.RemoteNewer)
                    {
                        lvi.Checked = true;
                        rowFg = Color.FromArgb(0, 70, 180); // Синий цвет Total Commander
                        rowBg = Color.FromArgb(235, 244, 255); // Мягкий светло-голубой фон
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else if (item.Status == SyncItemStatus.SizeMismatch)
                    {
                        lvi.Checked = false;
                        rowFg = Color.FromArgb(180, 100, 0); // Оранжево-коричневый
                        rowBg = Color.FromArgb(255, 247, 230); // Мягкий янтарный фон
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else if (item.Status == SyncItemStatus.SourceNotFound)
                    {
                        lvi.Checked = false;
                        rowFg = Color.FromArgb(170, 0, 0); // Красный
                        rowBg = Color.FromArgb(255, 235, 235); // Мягкий светло-розовый фон
                        rowFont = new Font(listView.Font, FontStyle.Bold);
                    }
                    else
                    {
                        lvi.Checked = false;
                        rowFg = Color.FromArgb(90, 90, 90); // Серый (Идентичны)
                        rowBg = (rowIndex % 2 == 0) ? Color.White : Color.FromArgb(248, 249, 250); // Мягкая зебра
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

                    // Для центральной колонки направления (<=>) делаем крупный жирный шрифт 11pt Bold
                    lvi.SubItems[3].Font = new Font("Segoe UI", 11f, FontStyle.Bold);

                    listView.Items.Add(lvi);
                    rowIndex++;
                }

                // Интерактивные многострочные всплывающие подсказки по всей строке ListView
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
                Point lastHoverPos = Point.Empty;

                listView.MouseMove += (s, e) =>
                {
                    var hit = listView.HitTest(e.Location);
                    if (hit.Item != null)
                    {
                        if (hit.Item != lastHoveredItem)
                        {
                            lastHoveredItem = hit.Item;
                            lastHoverPos = e.Location;
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

                // Кнопки
                Button selectUpdatesBtn = new Button()
                {
                    Left = 20,
                    Top = 520,
                    Width = 230,
                    Height = 34,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                    Text = "Выбрать требующие обновления"
                };
                selectUpdatesBtn.Click += (s, e) =>
                {
                    foreach (ListViewItem lvi in listView.Items)
                    {
                        if (lvi.Tag is SmartSyncItem it)
                        {
                            lvi.Checked = (it.Status == SyncItemStatus.LocalNewer || it.Status == SyncItemStatus.RemoteNewer);
                        }
                    }
                };

                Button clearSelectionBtn = new Button()
                {
                    Left = 260,
                    Top = 520,
                    Width = 120,
                    Height = 34,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                    Text = "Снять выбор"
                };
                clearSelectionBtn.Click += (s, e) =>
                {
                    foreach (ListViewItem lvi in listView.Items) lvi.Checked = false;
                };

                Button syncBtn = new Button()
                {
                    Left = 660,
                    Top = 520,
                    Width = 240,
                    Height = 34,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    Text = "Синхронизировать выбранные",
                    BackColor = Color.FromArgb(230, 245, 230),
                    Font = new Font("Segoe UI", 9, FontStyle.Bold)
                };

                Button closeBtn = new Button()
                {
                    Left = 910,
                    Top = 520,
                    Width = 110,
                    Height = 34,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    Text = "Закрыть",
                    DialogResult = DialogResult.Cancel
                };

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

                            if (it.Status == SyncItemStatus.LocalNewer && !string.IsNullOrEmpty(it.FileRecord.SourcePath))
                            {
                                try
                                {
                                    if (File.Exists(it.FileRecord.SourcePath))
                                    {
                                        string caption = string.IsNullOrEmpty(it.FileRecord.Parent) ? it.FileRecord.Name : $"{it.FileRecord.Parent}\\{it.FileRecord.Name}";
                                        int msgId = await TelegramManager.UploadAndSendFileAsync(channelId, it.FileRecord.SourcePath, it.FileRecord.Name, caption);
                                        if (msgId > 0)
                                        {
                                            db.MoveFileToTrash(it.FileRecord.Uid);
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
                                            it.StatusText = "Обновлен в TG";
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
                        MessageBox.Show(form, $"Синхронизация завершена.\nОбновлено файлов: {updated}\nОшибок: {errors}", "Smart Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    finally
                    {
                        syncBtn.Enabled = true;
                        closeBtn.Enabled = true;
                        selectUpdatesBtn.Enabled = true;
                        clearSelectionBtn.Enabled = true;
                    }
                };

                form.Controls.Add(selectUpdatesBtn);
                form.Controls.Add(clearSelectionBtn);
                form.Controls.Add(syncBtn);
                form.Controls.Add(closeBtn);
                form.CancelButton = closeBtn;

                form.FormClosing += (s, e) =>
                {
                    try
                    {
                        // Сохраняем ширины всех 7 колонок
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

                        // Сохраняем состояние окна (развернуто или нормальное) и размеры
                        if (form.WindowState == FormWindowState.Maximized)
                        {
                            SettingsManager.SaveSetting("smartsync_maximized", "1");
                            // RestoreBounds сохраняет обычный размер до разворачивания
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
