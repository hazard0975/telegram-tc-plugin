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
    LocalNewer,          // На ПК файл новее или размер изменился -> обновить в TG
    RemoteNewer,         // В Telegram файл новее -> обновить на ПК
    SourceNotFound       // Локальный файл-источник на диске отсутствует
}

public class SmartSyncItem
{
    public VfsDatabase.FileRecord FileRecord { get; set; } = null!;
    public SyncItemStatus Status { get; set; }
    public string StatusText { get; set; } = "";
    public string DirectionText { get; set; } = "";
    public long LocalSize { get; set; }
    public DateTime LocalWriteTime { get; set; }
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

                int initWidth = 980;
                int initHeight = 600;
                bool initMaximized = false;

                if (int.TryParse(SettingsManager.GetSetting("smartsync_width"), out int savedW) && savedW >= 840) initWidth = savedW;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_height"), out int savedH) && savedH >= 520) initHeight = savedH;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_maximized"), out int savedMax) && savedMax == 1) initMaximized = true;

                using Form form = new Form()
                {
                    Width = initWidth,
                    Height = initHeight,
                    MinimumSize = new Size(840, 520),
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

                foreach (var file in dbFiles)
                {
                    if (string.IsNullOrEmpty(file.SourcePath)) continue;

                    var item = new SmartSyncItem { FileRecord = file };
                    if (!File.Exists(file.SourcePath))
                    {
                        item.Status = SyncItemStatus.SourceNotFound;
                        item.StatusText = "Не найден на ПК";
                        item.DirectionText = "Пропуск";
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

                            if (item.LocalSize == file.Size && Math.Abs(localUnix - remoteUnix) <= 2)
                            {
                                item.Status = SyncItemStatus.Identical;
                                item.StatusText = "Идентичны";
                                item.DirectionText = "Синхронизировано";
                                identicalCount++;
                            }
                            else if (localUnix > remoteUnix || item.LocalSize != file.Size)
                            {
                                item.Status = SyncItemStatus.LocalNewer;
                                item.StatusText = "На ПК новее";
                                item.DirectionText = "ПК -> Telegram";
                                localNewerCount++;
                            }
                            else
                            {
                                item.Status = SyncItemStatus.RemoteNewer;
                                item.StatusText = "В TG новее";
                                item.DirectionText = "Telegram -> ПК";
                                remoteNewerCount++;
                            }
                        }
                        catch
                        {
                            item.Status = SyncItemStatus.SourceNotFound;
                            item.StatusText = "Ошибка доступа";
                            item.DirectionText = "Пропуск";
                            missingCount++;
                        }
                    }
                    items.Add(item);
                }

                // Информационная сводка
                Label summaryLabel = new Label()
                {
                    Left = 20,
                    Top = 75,
                    Width = 790,
                    Height = 22,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Text = $"Файлов с источником: {items.Count}  |  Требуют обновления: {localNewerCount + remoteNewerCount}  |  Идентичны: {identicalCount}  |  Не найдены на диске: {missingCount}"
                };
                form.Controls.Add(summaryLabel);

                // Список файлов ListView
                ListView listView = new ListView()
                {
                    Left = 20,
                    Top = 105,
                    Width = 920,
                    Height = 390,
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    View = View.Details,
                    CheckBoxes = true,
                    FullRowSelect = true,
                    GridLines = true
                };

                // Включаем двойную буферизацию для устранения мерцания при ресайзе и растягивании колонок
                typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .SetValue(listView, true, null);

                int colVfsWidth = 460;
                int colStatusWidth = 120;
                int colDirectionWidth = 150;
                int colSourceWidth = 580;

                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_vfs"), out int cv) && cv >= 50) colVfsWidth = cv;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_status"), out int cst) && cst >= 50) colStatusWidth = cst;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_direction"), out int cd) && cd >= 50) colDirectionWidth = cd;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_source"), out int csrc) && csrc >= 50) colSourceWidth = csrc;

                listView.Columns.Add("Путь в VFS", colVfsWidth);
                listView.Columns.Add("Статус", colStatusWidth);
                listView.Columns.Add("Направление", colDirectionWidth);
                listView.Columns.Add("Оригинал на ПК", colSourceWidth);

                foreach (var item in items)
                {
                    string vfsDisplayPath = string.IsNullOrEmpty(item.FileRecord.Parent)
                        ? item.FileRecord.Name
                        : $"{item.FileRecord.Parent}\\{item.FileRecord.Name}";

                    var lvi = new ListViewItem(vfsDisplayPath);
                    lvi.SubItems.Add(item.StatusText);
                    lvi.SubItems.Add(item.DirectionText);
                    lvi.SubItems.Add(item.FileRecord.SourcePath ?? "");
                    lvi.Tag = item;

                    if (item.Status == SyncItemStatus.LocalNewer)
                    {
                        lvi.Checked = true;
                        lvi.ForeColor = Color.FromArgb(0, 100, 0);
                    }
                    else if (item.Status == SyncItemStatus.RemoteNewer)
                    {
                        lvi.Checked = true;
                        lvi.ForeColor = Color.FromArgb(0, 50, 160);
                    }
                    else if (item.Status == SyncItemStatus.SourceNotFound)
                    {
                        lvi.Checked = false;
                        lvi.ForeColor = Color.FromArgb(160, 0, 0);
                    }
                    else
                    {
                        lvi.Checked = false;
                        lvi.ForeColor = Color.FromArgb(100, 100, 100);
                    }

                    listView.Items.Add(lvi);
                }

                form.Controls.Add(listView);

                // Кнопки
                Button selectUpdatesBtn = new Button()
                {
                    Left = 20,
                    Top = 510,
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
                    Top = 510,
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
                    Left = 600,
                    Top = 510,
                    Width = 220,
                    Height = 34,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    Text = "Синхронизировать выбранные",
                    BackColor = Color.FromArgb(230, 245, 230),
                    Font = new Font("Segoe UI", 9, FontStyle.Bold)
                };

                Button closeBtn = new Button()
                {
                    Left = 830,
                    Top = 510,
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
                                            it.DirectionText = "Синхронизировано";
                                            lvi.SubItems[1].Text = it.StatusText;
                                            lvi.SubItems[2].Text = it.DirectionText;
                                            lvi.Checked = false;
                                            lvi.ForeColor = Color.DarkGreen;
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

                                        it.Status = SyncItemStatus.Identical;
                                        it.StatusText = "Обновлен на ПК";
                                        it.DirectionText = "Синхронизировано";
                                        lvi.SubItems[1].Text = it.StatusText;
                                        lvi.SubItems[2].Text = it.DirectionText;
                                        lvi.Checked = false;
                                        lvi.ForeColor = Color.DarkGreen;
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
                        // Сохраняем ширины колонок
                        if (listView.Columns.Count >= 4)
                        {
                            SettingsManager.SaveSetting("smartsync_col_vfs", listView.Columns[0].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_status", listView.Columns[1].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_direction", listView.Columns[2].Width.ToString());
                            SettingsManager.SaveSetting("smartsync_col_source", listView.Columns[3].Width.ToString());
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
}
