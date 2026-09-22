using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TgVfsPlugin;

public static class FilePropertiesDialog
{
    public static void Show(string channelName, long channelId, string relativePath, VfsDatabase.FileRecord file, VfsDatabase? db = null)
    {
        bool isLeftPanel = Win32Api.IsActivePanelLeft();
        FormExtensions.RunInSta(() =>
        {
            try
            {
                Win32Api.EnsureVisualStyles();
                using ToolTip toolTip = UiTheme.CreateToolTip();

                int margin = 20;
                int clientWidth = 514;
                int contentWidth = clientWidth - margin * 2; // 474px
                int clientHeight = 468; // 75 (header) + 325 (infoGroup) + 16 (gap) + 52 (bottom) = 468px

                using Form form = UiTheme.CreateDialogForm($"Свойства: {file.Name}", clientWidth, clientHeight);

                // Иконка и заголовок
                Panel headerPanel = new Panel()
                {
                    Left = 0,
                    Top = 0,
                    Width = clientWidth,
                    Height = 60,
                    BackColor = UiTheme.HeaderBgColor,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                bool isMountRoot = file.Uid == file.MountId || string.IsNullOrEmpty(relativePath);

                Label titleLabel = new Label()
                {
                    Left = margin,
                    Top = 12,
                    Width = contentWidth,
                    Height = 22,
                    Text = isMountRoot ? $"Канал: {channelName}" : (file.IsDir ? $"Папка: {file.Name}" : $"Файл: {file.Name}"),
                    Font = UiTheme.HeaderTitleFont,
                    AutoEllipsis = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                string fullVirtualPath = isMountRoot ? $"\\{channelName}" : (string.IsNullOrEmpty(relativePath) ? $"\\{channelName}\\{file.Name}" : $"\\{channelName}\\{relativePath}");
                Label pathSubLabel = new Label()
                {
                    Left = margin,
                    Top = 35,
                    Width = contentWidth,
                    Height = 22,
                    Text = fullVirtualPath,
                    ForeColor = Color.Gray,
                    AutoEllipsis = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(pathSubLabel);
                form.Controls.Add(headerPanel);

                // Группа свойств
                GroupBox infoGroup = new GroupBox()
                {
                    Left = margin,
                    Top = 75,
                    Width = contentWidth,
                    Height = 325,
                    Text = isMountRoot ? "Параметры канала" : (file.IsDir ? "Параметры папки" : "Параметры Telegram VFS"),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                int curTop = 24;
                int labelWidth = 145;
                int valueWidth = contentWidth - 30 - labelWidth;
                int rowHeight = 23;

                void AddRow(string labelText, string valueText)
                {
                    Label lbl = new Label()
                    {
                        Left = 15,
                        Top = curTop,
                        Width = labelWidth,
                        Text = labelText,
                        ForeColor = UiTheme.LabelForeColor
                    };
                    TextBox valBox = new TextBox()
                    {
                        Left = 15 + labelWidth,
                        Top = curTop - 2,
                        Width = valueWidth,
                        Text = valueText,
                        ReadOnly = true,
                        BorderStyle = BorderStyle.None,
                        BackColor = SystemColors.Control,
                        Font = UiTheme.DefaultFont,
                        TabStop = false,
                        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                    };
                    infoGroup.Controls.Add(lbl);
                    infoGroup.Controls.Add(valBox);
                    curTop += rowHeight;
                }

                int dirFilesCount = 0;
                int dirDirsCount = 0;
                long dirTotalSize = 0;
                if (file.IsDir && db != null)
                {
                    string dirSubPath = isMountRoot ? "" : (string.IsNullOrEmpty(file.Parent) ? file.Name : $"{file.Parent}\\{file.Name}");
                    db.GetDirectoryStats(file.MountId, dirSubPath, file.InTrash == 1, out dirFilesCount, out dirDirsCount, out dirTotalSize);
                }

                string folderModeStr = "Контейнер";
                if (db != null)
                {
                    var mount = db.GetMountById(file.MountId) ?? db.GetMountByName(channelName);
                    if (mount != null && mount.Mode == 0)
                    {
                        folderModeStr = "Зеркало";
                    }
                }

                string dateStr = file.MTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
                string channelStr = $"{channelName} (ID: {channelId})";
                string statusStr = file.InTrash == 1 ? "В корзине [.Trash]" : "Активный (в хранилище)";

                AddRow("Имя:", file.Name);
                AddRow("Канал Telegram:", channelStr);

                string sizeReport;
                string msgIdReport = file.TgMessageId > 0 ? $"#{file.TgMessageId}" : "Локально / Виртуально";
                string versionReport = $"v{file.Ver}";

                if (file.IsDir)
                {
                    sizeReport = $"{FormatSize(dirTotalSize)} ({dirTotalSize:N0} байт)";
                    AddRow("Файлов:", $"{dirFilesCount} шт.");
                    AddRow("Папок:", $"{dirDirsCount} шт.");
                    AddRow("Размер:", sizeReport);
                }
                else
                {
                    string sizeFormatted = FormatSize(file.Size);
                    sizeReport = $"{file.Size:N0} байт ({sizeFormatted})";
                    AddRow("Размер:", sizeReport);
                }

                AddRow("Дата изменения:", dateStr);

                if (!file.IsDir)
                {
                    AddRow("ID сообщения TG:", msgIdReport);
                    AddRow("Ревизия / Версия:", versionReport);
                    if (!string.IsNullOrEmpty(file.SourcePath))
                    {
                        AddRow("Источник на ПК:", file.SourcePath);
                    }
                }

                AddRow("Статус файла:", statusStr);
                AddRow("Режим папки:", folderModeStr);
                AddRow("Уникальный UID:", file.Uid);

                // Кнопка копирования свойств прямо внутри блока свойств
                Button copyBtn = UiTheme.CreateButton("Копировать свойства", "Скопировать всю текстовую информацию о свойствах в буфер обмена", toolTip, 150, 30);
                copyBtn.Left = 15;
                copyBtn.Top = infoGroup.Height - 40;
                copyBtn.Click += (s, e) =>
                {
                    string infoReport = file.IsDir ?
                        $"Имя: {file.Name}\r\n" +
                        $"Путь: {fullVirtualPath}\r\n" +
                        $"Канал: {channelStr}\r\n" +
                        $"Файлов: {dirFilesCount}\r\n" +
                        $"Папок: {dirDirsCount}\r\n" +
                        $"Размер: {sizeReport}\r\n" +
                        $"Дата изменения: {dateStr}\r\n" +
                        $"Статус: {statusStr}\r\n" +
                        $"UID: {file.Uid}"
                        :
                        $"Имя: {file.Name}\r\n" +
                        $"Путь: {fullVirtualPath}\r\n" +
                        $"Канал: {channelStr}\r\n" +
                        $"Размер: {sizeReport}\r\n" +
                        $"Дата изменения: {dateStr}\r\n" +
                        $"ID сообщения TG: {msgIdReport}\r\n" +
                        $"Версия: {versionReport}\r\n" +
                        $"Источник на ПК: {(file.SourcePath ?? "нет")}\r\n" +
                        $"Статус: {statusStr}\r\n" +
                        $"UID: {file.Uid}";
                    try
                    {
                        Clipboard.SetText(infoReport);
                        MessageBox.Show(form, "Информация о файле скопирована в буфер обмена.", "Скопировано", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("UI", $"Clipboard copy failed: {ex.Message}");
                    }
                };
                infoGroup.Controls.Add(copyBtn);
                form.Controls.Add(infoGroup);

                form.MinimumSize = form.Size;
                form.MaximumSize = form.Size;

                // Нижняя панель с кнопками
                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                Button? smartSyncBtn = null;
                Button? restoreBtn = null;
                Button? openLocBtn = null;
                Button? navBtn = null;

                // Кнопка Smart Sync слева внизу для папок/канала
                if (file.IsDir && file.InTrash != 1 && db != null)
                {
                    smartSyncBtn = UiTheme.CreateButton("Smart Sync", "Сравнить файлы и папки с оригиналами на дисках ПК и синхронизировать", toolTip, 105);
                    smartSyncBtn.Left = 20;
                    smartSyncBtn.Top = 11;
                    smartSyncBtn.Click += (s, e) =>
                    {
                        string targetFolder = file.Uid == file.MountId ? "" : relativePath;
                        SmartSyncDialog.Show(channelName, channelId, file.MountId, targetFolder, db);
                    };
                    bottomPanel.Controls.Add(smartSyncBtn);
                }

                // Дополнительные кнопки действий
                if (!file.IsDir && !string.IsNullOrEmpty(file.SourcePath))
                {
                    openLocBtn = UiTheme.CreateButton("Найти на ПК", "Открыть папку с оригиналом файла в Проводнике Windows", toolTip, 100);
                    openLocBtn.Left = smartSyncBtn != null ? smartSyncBtn.Right + 10 : 20;
                    openLocBtn.Top = 11;
                    openLocBtn.Click += (s, e) =>
                    {
                        try
                        {
                            if (File.Exists(file.SourcePath))
                            {
                                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file.SourcePath}\"");
                            }
                            else
                            {
                                string dir = Path.GetDirectoryName(file.SourcePath) ?? "";
                                if (Directory.Exists(dir))
                                {
                                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                                }
                                else
                                {
                                    MessageBox.Show(form, $"Файл или папка не найдены на ПК:\n{file.SourcePath}", "Источник не найден", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(form, $"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    };
                    bottomPanel.Controls.Add(openLocBtn);
                }

                if (file.InTrash == 1 && db != null)
                {
                    restoreBtn = UiTheme.CreateButton("Восстановить", "Восстановить этот файл из корзины в его исходную папку", toolTip, 110);
                    restoreBtn.Left = 20;
                    restoreBtn.Top = 11;
                    restoreBtn.Click += (s, e) =>
                    {
                        string targetDir = string.IsNullOrEmpty(file.Parent) ? "\\" : $"\\{file.Parent}\\";
                        var ask = MessageBox.Show(form,
                            $"Восстановить '{file.Name}' в исходное расположение: '{targetDir}'?",
                            "Подтверждение восстановления",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);

                        if (ask == DialogResult.Yes)
                        {
                            bool success = db.RestoreFile(file);
                            if (success)
                            {
                                Win32Api.RefreshActivePanel();
                                MessageBox.Show(form, $"Файл '{file.Name}' успешно восстановлен в '{targetDir}'.", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                form.Close();
                            }
                            else
                            {
                                MessageBox.Show(form, "Не удалось восстановить файл.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    };
                    bottomPanel.Controls.Add(restoreBtn);

                    navBtn = UiTheme.CreateButton("К папке", "Перейти к исходной папке в активном хранилище Total Commander", toolTip, 90);
                    navBtn.Left = restoreBtn.Right + 10;
                    navBtn.Top = 11;
                    navBtn.Click += (s, e) =>
                    {
                        if (db != null && !db.ActiveFolderExists(file.MountId, file.Parent))
                        {
                            MessageBox.Show(form, "Исходное расположение больше не существует в активном хранилище.", "Ошибка навигации", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            string pluginName = Win32Api.GetPluginVfsName();
                            string targetVfsPath = @"\\\" + pluginName + @"\" + channelName + (string.IsNullOrEmpty(file.Parent) ? "" : @"\" + file.Parent);
                            Win32Api.NavigateToVfsPath(targetVfsPath, isLeftPanel);
                            form.Close();
                        }
                    };
                    bottomPanel.Controls.Add(navBtn);
                }
                else if (file.Uid != file.MountId) // кнопка корзины для файла/папки
                {
                    int leftPos = openLocBtn != null ? openLocBtn.Right + 10 : (smartSyncBtn != null ? smartSyncBtn.Right + 10 : 20);
                    navBtn = UiTheme.CreateButton("Корзина", "Перейти в папку корзины этого каталога в Total Commander", toolTip, 85);
                    navBtn.Left = leftPos;
                    navBtn.Top = 11;
                    navBtn.Click += (s, e) =>
                    {
                        string pluginName = Win32Api.GetPluginVfsName();
                        string targetFolder = file.IsDir ? relativePath : (file.Parent ?? "");
                        string deepestFolder = db != null ? db.GetDeepestTrashFolder(file.MountId, targetFolder) : targetFolder;
                        string targetVfsPath = @"\\\" + pluginName + @"\[🗑] Корзина\" + channelName + (string.IsNullOrEmpty(deepestFolder) ? "" : @"\" + deepestFolder);
                        Win32Api.NavigateToVfsPath(targetVfsPath, isLeftPanel);
                        form.Close();
                    };
                    bottomPanel.Controls.Add(navBtn);
                }

                // Кнопка Закрыть справа
                Button closeBtn = UiTheme.CreateButton("Закрыть", "Закрыть окно свойств", toolTip, 90, dialogResult: DialogResult.OK);
                closeBtn.Left = form.ClientSize.Width - 20 - closeBtn.Width;
                closeBtn.Top = 11;
                closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                bottomPanel.Controls.Add(closeBtn);

                form.AcceptButton = closeBtn;
                form.CancelButton = closeBtn;

                form.Shown += (s, e) =>
                {
                    if (restoreBtn != null) restoreBtn.Focus();
                    else closeBtn.Focus();
                };

                form.ShowModalTc();
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "FilePropertiesDialog exception", ex);
            }
        });
    }

    public static void ShowTrashProperties(string channelName, long channelId, string mountId, VfsDatabase db)
    {
        bool isLeftPanel = Win32Api.IsActivePanelLeft();
        FormExtensions.RunInSta(() =>
        {
            try
            {
                Win32Api.EnsureVisualStyles();
                using ToolTip toolTip = UiTheme.CreateToolTip();

                db.GetTrashStats(mountId, out int filesCount, out int dirsCount, out long totalTrashSize);
                int totalItems = filesCount + dirsCount;

                int margin = 20;
                int topMargin = 16;
                int clientWidth = 474;
                int contentWidth = clientWidth - margin * 2; // 434px

                using Form form = new Form()
                {
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = $"Свойства корзины: {channelName}",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont,
                    AutoScaleMode = AutoScaleMode.None
                };

                Panel headerPanel = new Panel()
                {
                    Left = 0,
                    Top = 0,
                    Width = clientWidth,
                    Height = 60,
                    BackColor = UiTheme.HeaderBgColor,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                Label titleLabel = new Label()
                {
                    Left = margin,
                    Top = 12,
                    Width = contentWidth,
                    Height = 22,
                    Text = $"Корзина канала: {channelName}",
                    Font = UiTheme.HeaderTitleFont,
                    AutoEllipsis = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                Label pathSubLabel = new Label()
                {
                    Left = margin,
                    Top = 35,
                    Width = contentWidth,
                    Height = 22,
                    Text = $"Telegram ID: {channelId}",
                    ForeColor = Color.Gray,
                    AutoEllipsis = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(pathSubLabel);
                form.Controls.Add(headerPanel);

                GroupBox infoGroup = new GroupBox()
                {
                    Left = margin,
                    Top = 75,
                    Width = contentWidth,
                    Height = 175,
                    Text = "Состояние корзины",
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                int labelWidth = 155;
                int valLeft = 175;
                int valWidth = 245;

                Label filesLbl = new Label()
                {
                    Left = 15,
                    Top = 26,
                    Width = labelWidth,
                    Text = "Файлов:"
                };
                Label filesVal = new Label()
                {
                    Left = valLeft,
                    Top = 26,
                    Width = valWidth,
                    Font = UiTheme.BoldFont,
                    Text = $"{filesCount} шт."
                };

                Label dirsLbl = new Label()
                {
                    Left = 15,
                    Top = 50,
                    Width = labelWidth,
                    Text = "Папок:"
                };
                Label dirsVal = new Label()
                {
                    Left = valLeft,
                    Top = 50,
                    Width = valWidth,
                    Font = UiTheme.BoldFont,
                    Text = $"{dirsCount} шт."
                };

                Label sizeLbl = new Label()
                {
                    Left = 15,
                    Top = 74,
                    Width = labelWidth,
                    Text = "Занимаемый объём:"
                };
                Label sizeVal = new Label()
                {
                    Left = valLeft,
                    Top = 74,
                    Width = valWidth,
                    Font = UiTheme.BoldFont,
                    Text = $"{FormatSize(totalTrashSize)} ({totalTrashSize:N0} байт)"
                };

                Label noteLbl = new Label()
                {
                    Left = 15,
                    Top = 104,
                    Width = 405,
                    Height = 55,
                    ForeColor = Color.DimGray,
                    Text = "Файлы в корзине сохраняют свои версии в Telegram и могут быть восстановлены в исходные папки."
                };

                infoGroup.Controls.Add(filesLbl);
                infoGroup.Controls.Add(filesVal);
                infoGroup.Controls.Add(dirsLbl);
                infoGroup.Controls.Add(dirsVal);
                infoGroup.Controls.Add(sizeLbl);
                infoGroup.Controls.Add(sizeVal);
                infoGroup.Controls.Add(noteLbl);
                form.Controls.Add(infoGroup);

                int bottomPanelHeight = 52;
                int contentBottom = infoGroup.Bottom + topMargin;
                int clientHeight = contentBottom + bottomPanelHeight;

                form.ClientSize = new Size(clientWidth, clientHeight);
                form.MinimumSize = form.Size;
                form.MaximumSize = form.Size;

                // Нижняя панель
                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                Button restoreAllBtn = UiTheme.CreateButton("Восстановить всё", "Восстановить все файлы и папки из корзины в их исходные места в активном канале", toolTip, 130);
                restoreAllBtn.Left = 20;
                restoreAllBtn.Top = 11;
                restoreAllBtn.Enabled = totalItems > 0;
                restoreAllBtn.Click += (s, e) =>
                {
                    string details = filesCount > 0 && dirsCount > 0
                        ? $"{filesCount} файлов и {dirsCount} папок"
                        : (filesCount > 0 ? $"{filesCount} файлов" : $"{dirsCount} папок");

                    var ask = MessageBox.Show(form,
                        $"Вы действительно хотите восстановить все {details} ({FormatSize(totalTrashSize)}) из корзины канала '{channelName}' в их исходные места?",
                        "Подтверждение восстановления",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (ask == DialogResult.Yes)
                    {
                        int restoredCount = db.RestoreAllTrash(mountId);
                        Win32Api.RefreshActivePanel();
                        MessageBox.Show(form, "Восстановление корзины успешно завершено.", "Восстановление завершено", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        form.Close();
                    }
                };

                Button cleanBtn = UiTheme.CreateButton("Очистить", "Безвозвратно удалить все файлы корзины и их сообщения в Telegram", toolTip, 85);
                cleanBtn.Left = restoreAllBtn.Right + 10;
                cleanBtn.Top = 11;
                cleanBtn.Enabled = totalItems > 0;
                cleanBtn.Click += (s, e) =>
                {
                    string details = filesCount > 0 && dirsCount > 0
                        ? $"{filesCount} файлов и {dirsCount} папок"
                        : (filesCount > 0 ? $"{filesCount} файлов" : $"{dirsCount} папок");

                    var ask = MessageBox.Show(form,
                        $"Вы действительно хотите навсегда очистить корзину канала '{channelName}'?\n\n" +
                        $"⚠️ ВНИМАНИЕ: Это безвозвратно удалит {details} ({FormatSize(totalTrashSize)}) и связанные с ними сообщения из Telegram!",
                        "Подтверждение очистки корзины",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (ask == DialogResult.Yes)
                    {
                        var trashRecords = db.GetTrashFileRecords(mountId);
                        if (trashRecords.Count > 0)
                        {
                            WfxExports.PurgeTrashRecords(mountId, channelId, trashRecords);
                        }
                        Win32Api.RefreshActivePanel();
                        MessageBox.Show(form, "Корзина успешно очищена.", "Очистка завершена", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        form.Close();
                    }
                };

                Button navBtn = UiTheme.CreateButton("К каналу", "Перейти в корень активного канала в Total Commander", toolTip, 85);
                navBtn.Left = cleanBtn.Right + 10;
                navBtn.Top = 11;
                navBtn.Click += (s, e) =>
                {
                    string pluginName = Win32Api.GetPluginVfsName();
                    string targetVfsPath = @"\\\" + pluginName + @"\" + channelName;
                    Win32Api.NavigateToVfsPath(targetVfsPath, isLeftPanel);
                    form.Close();
                };

                Button closeBtn = UiTheme.CreateButton("Закрыть", "Закрыть окно свойств корзины", toolTip, 85, dialogResult: DialogResult.OK);
                closeBtn.Left = form.ClientSize.Width - margin - closeBtn.Width;
                closeBtn.Top = 11;
                closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                bottomPanel.Controls.Add(restoreAllBtn);
                bottomPanel.Controls.Add(cleanBtn);
                bottomPanel.Controls.Add(navBtn);
                bottomPanel.Controls.Add(closeBtn);
                form.CancelButton = closeBtn;

                form.ShowModalTc();
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "ShowTrashProperties exception", ex);
            }
        });
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
