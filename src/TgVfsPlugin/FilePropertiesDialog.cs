using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TgVfsPlugin;

public static class FilePropertiesDialog
{
    public static void Show(string channelName, long channelId, string relativePath, VfsDatabase.FileRecord file, VfsDatabase? db = null)
    {
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using Form form = new Form()
                {
                    Width = 520,
                    Height = 450,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = $"Свойства: {file.Name}",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = new Font("Segoe UI", 9)
                };

                // Иконка и заголовок
                Panel headerPanel = new Panel()
                {
                    Left = 0,
                    Top = 0,
                    Width = 520,
                    Height = 60,
                    BackColor = Color.FromArgb(245, 247, 250)
                };

                Label titleLabel = new Label()
                {
                    Left = 20,
                    Top = 12,
                    Width = 460,
                    Height = 22,
                    Text = file.IsDir ? $"Папка: {file.Name}" : $"Файл: {file.Name}",
                    Font = new Font("Segoe UI", 11, FontStyle.Bold),
                    AutoEllipsis = true
                };

                string fullVirtualPath = string.IsNullOrEmpty(relativePath) ? $"\\{channelName}\\{file.Name}" : $"\\{channelName}\\{relativePath}";
                Label pathSubLabel = new Label()
                {
                    Left = 20,
                    Top = 35,
                    Width = 460,
                    Height = 18,
                    Text = fullVirtualPath,
                    ForeColor = Color.Gray,
                    AutoEllipsis = true
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(pathSubLabel);
                form.Controls.Add(headerPanel);

                // Группа свойств
                GroupBox infoGroup = new GroupBox()
                {
                    Left = 20,
                    Top = 75,
                    Width = 465,
                    Height = 265,
                    Text = file.IsDir ? "Параметры папки" : "Параметры Telegram VFS"
                };

                int curTop = 25;
                int labelWidth = 145;
                int valueWidth = 295;
                int rowHeight = 24;

                void AddRow(string labelText, string valueText)
                {
                    Label lbl = new Label()
                    {
                        Left = 15,
                        Top = curTop,
                        Width = labelWidth,
                        Text = labelText,
                        ForeColor = Color.FromArgb(70, 70, 70)
                    };
                    TextBox valBox = new TextBox()
                    {
                        Left = 15 + labelWidth,
                        Top = curTop - 3,
                        Width = valueWidth,
                        Text = valueText,
                        ReadOnly = true,
                        BorderStyle = BorderStyle.None,
                        BackColor = SystemColors.Control,
                        Font = new Font("Segoe UI", 9, FontStyle.Regular),
                        TabStop = false
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
                    string dirSubPath = string.IsNullOrEmpty(file.Parent) ? file.Name : $"{file.Parent}\\{file.Name}";
                    db.GetDirectoryStats(file.MountId, dirSubPath, file.InTrash == 1, out dirFilesCount, out dirDirsCount, out dirTotalSize);
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
                }

                AddRow("Статус файла:", statusStr);
                AddRow("Уникальный UID:", file.Uid);

                form.Controls.Add(infoGroup);

                // Кнопки
                Button copyBtn = new Button()
                {
                    Text = "Копировать",
                    Left = 20,
                    Top = 355,
                    Width = 115,
                    Height = 30
                };
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

                Button? restoreBtn = null;
                Button? navBtn = null;

                if (file.InTrash == 1 && db != null)
                {
                    restoreBtn = new Button()
                    {
                        Text = "Восстановить",
                        Left = 130,
                        Top = 355,
                        Width = 130,
                        Height = 30,
                        BackColor = Color.FromArgb(230, 245, 230)
                    };
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
                    form.Controls.Add(restoreBtn);

                    // Кнопка перехода к файлу или к папке из корзины в активный VFS
                    navBtn = new Button()
                    {
                        Text = "К папке",
                        Left = 270,
                        Top = 355,
                        Width = 100,
                        Height = 30
                    };
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
                            Win32Api.ChangeInactivePanelDir(targetVfsPath);
                            form.Close();
                        }
                    };
                    form.Controls.Add(navBtn);
                }
                else if (file.Uid != file.MountId) // не показываем на самом канале
                {
                    // Кнопка перехода к корзине для активного файла или папки
                    navBtn = new Button()
                    {
                        Text = "Корзина",
                        Left = 130,
                        Top = 355,
                        Width = 120,
                        Height = 30
                    };
                    navBtn.Click += (s, e) =>
                    {
                        string pluginName = Win32Api.GetPluginVfsName();
                        string targetFolder = file.IsDir ? relativePath : (file.Parent ?? "");
                        string targetVfsPath = @"\\\" + pluginName + @"\[🗑] Корзина\" + channelName + (string.IsNullOrEmpty(targetFolder) ? "" : @"\" + targetFolder);
                        Win32Api.ChangeInactivePanelDir(targetVfsPath);
                        form.Close();
                    };
                    form.Controls.Add(navBtn);
                }

                Button okBtn = new Button()
                {
                    Text = "Закрыть",
                    Left = 380,
                    Top = 355,
                    Width = 100,
                    Height = 30,
                    DialogResult = DialogResult.OK
                };

                form.Controls.Add(copyBtn);
                form.Controls.Add(okBtn);
                form.AcceptButton = okBtn;
                form.CancelButton = okBtn;

                form.Shown += (s, e) =>
                {
                    if (restoreBtn != null) restoreBtn.Focus();
                    else okBtn.Focus();
                };

                form.ShowDialog(Win32Window.GetTcOwner());
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "FilePropertiesDialog exception", ex);
            }
        });

        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
    }

    public static void ShowTrashProperties(string channelName, long channelId, string mountId, VfsDatabase db)
    {
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                db.GetTrashStats(mountId, out int filesCount, out int dirsCount, out long totalTrashSize);
                int totalItems = filesCount + dirsCount;

                using Form form = new Form()
                {
                    Width = 480,
                    Height = 350,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = $"Свойства корзины: {channelName}",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = new Font("Segoe UI", 9)
                };

                Panel headerPanel = new Panel()
                {
                    Left = 0,
                    Top = 0,
                    Width = 480,
                    Height = 60,
                    BackColor = Color.FromArgb(245, 247, 250)
                };

                Label titleLabel = new Label()
                {
                    Left = 20,
                    Top = 12,
                    Width = 440,
                    Height = 22,
                    Text = $"Корзина канала: {channelName}",
                    Font = new Font("Segoe UI", 11, FontStyle.Bold),
                    AutoEllipsis = true
                };

                Label pathSubLabel = new Label()
                {
                    Left = 20,
                    Top = 35,
                    Width = 440,
                    Height = 18,
                    Text = $"Telegram ID: {channelId}",
                    ForeColor = Color.Gray,
                    AutoEllipsis = true
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(pathSubLabel);
                form.Controls.Add(headerPanel);

                GroupBox infoGroup = new GroupBox()
                {
                    Left = 20,
                    Top = 75,
                    Width = 425,
                    Height = 165,
                    Text = "Состояние корзины"
                };

                int labelWidth = 155;
                int valLeft = 175;
                int valWidth = 235;

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
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
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
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
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
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Text = $"{FormatSize(totalTrashSize)} ({totalTrashSize:N0} байт)"
                };

                Label noteLbl = new Label()
                {
                    Left = 15,
                    Top = 104,
                    Width = 395,
                    Height = 45,
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

                Button cleanBtn = new Button()
                {
                    Text = "Очистить",
                    Left = 20,
                    Top = 255,
                    Width = 110,
                    Height = 30,
                    BackColor = Color.FromArgb(255, 235, 235),
                    Enabled = totalItems > 0
                };
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

                Button closeBtn = new Button()
                {
                    Text = "Закрыть",
                    Left = 345,
                    Top = 255,
                    Width = 100,
                    Height = 30,
                    DialogResult = DialogResult.OK
                };

                Button navBtn = new Button()
                {
                    Text = "К каналу",
                    Left = 140,
                    Top = 255,
                    Width = 110,
                    Height = 30
                };
                navBtn.Click += (s, e) =>
                {
                    string pluginName = Win32Api.GetPluginVfsName();
                    string targetVfsPath = @"\\\" + pluginName + @"\" + channelName;
                    Win32Api.ChangeInactivePanelDir(targetVfsPath);
                    form.Close();
                };

                form.Controls.Add(cleanBtn);
                form.Controls.Add(navBtn);
                form.Controls.Add(closeBtn);
                form.CancelButton = closeBtn;

                form.ShowDialog(Win32Window.GetTcOwner());
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "ShowTrashProperties exception", ex);
            }
        });

        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
