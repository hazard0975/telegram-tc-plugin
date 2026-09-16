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
                    TopMost = true,
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
                    Text = "Параметры Telegram VFS"
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

                string sizeFormatted = FormatSize(file.Size);
                string sizeFull = file.IsDir ? "Каталог" : $"{file.Size:N0} байт ({sizeFormatted})";
                string dateStr = file.MTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
                string channelStr = $"{channelName} (ID: {channelId})";
                string msgIdStr = file.TgMessageId > 0 ? $"#{file.TgMessageId}" : "Локально / Виртуально";
                string versionStr = $"v{file.Ver}";
                string statusStr = file.InTrash == 1 ? "В корзине [.Trash]" : "Активный (в хранилище)";

                AddRow("Имя:", file.Name);
                AddRow("Канал Telegram:", channelStr);
                AddRow("Размер:", sizeFull);
                AddRow("Дата изменения:", dateStr);
                AddRow("ID сообщения TG:", msgIdStr);
                AddRow("Ревизия / Версия:", versionStr);
                AddRow("Статус файла:", statusStr);
                AddRow("Уникальный UID:", file.Uid);

                form.Controls.Add(infoGroup);

                // Кнопки
                Button copyBtn = new Button()
                {
                    Text = "Копировать",
                    Left = 20,
                    Top = 355,
                    Width = 110,
                    Height = 30
                };
                copyBtn.Click += (s, e) =>
                {
                    string infoReport = 
                        $"Имя: {file.Name}\r\n" +
                        $"Путь: {fullVirtualPath}\r\n" +
                        $"Канал: {channelStr}\r\n" +
                        $"Размер: {sizeFull}\r\n" +
                        $"Дата изменения: {dateStr}\r\n" +
                        $"ID сообщения TG: {msgIdStr}\r\n" +
                        $"Версия: {versionStr}\r\n" +
                        $"Статус: {statusStr}\r\n" +
                        $"UID: {file.Uid}";
                    try
                    {
                        Clipboard.SetText(infoReport);
                        MessageBox.Show(form, "Информация о файле скопирована в буфер обмена.", "Скопировано", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Clipboard copy failed: {ex.Message}");
                    }
                };

                Button? restoreBtn = null;
                if (file.InTrash == 1 && db != null)
                {
                    restoreBtn = new Button()
                    {
                        Text = "↺ Восстановить",
                        Left = 140,
                        Top = 355,
                        Width = 150,
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
                            bool success = db.RestoreFile(file.Uid);
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
                }

                Button okBtn = new Button()
                {
                    Text = "Закрыть",
                    Left = 385,
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

                form.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Log($"FilePropertiesDialog exception: {ex}");
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

                db.GetTrashStats(mountId, out int trashCount, out long totalTrashSize);

                using Form form = new Form()
                {
                    Width = 480,
                    Height = 320,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = $"Свойства корзины: {channelName}",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = true,
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
                    Height = 135,
                    Text = "Состояние корзины"
                };

                Label countLbl = new Label()
                {
                    Left = 15,
                    Top = 30,
                    Width = 140,
                    Text = "Удалённых файлов:"
                };
                Label countVal = new Label()
                {
                    Left = 160,
                    Top = 30,
                    Width = 240,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Text = $"{trashCount} шт."
                };

                Label sizeLbl = new Label()
                {
                    Left = 15,
                    Top = 60,
                    Width = 140,
                    Text = "Занимаемый объём:"
                };
                Label sizeVal = new Label()
                {
                    Left = 160,
                    Top = 60,
                    Width = 240,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Text = $"{FormatSize(totalTrashSize)} ({totalTrashSize:N0} байт)"
                };

                Label noteLbl = new Label()
                {
                    Left = 15,
                    Top = 90,
                    Width = 390,
                    Height = 35,
                    ForeColor = Color.DimGray,
                    Text = "Файлы в корзине сохраняют свои версии в Telegram и могут быть восстановлены в исходные папки."
                };

                infoGroup.Controls.Add(countLbl);
                infoGroup.Controls.Add(countVal);
                infoGroup.Controls.Add(sizeLbl);
                infoGroup.Controls.Add(sizeVal);
                infoGroup.Controls.Add(noteLbl);
                form.Controls.Add(infoGroup);

                Button cleanBtn = new Button()
                {
                    Text = "♻ Очистить корзину",
                    Left = 20,
                    Top = 230,
                    Width = 160,
                    Height = 32,
                    BackColor = Color.FromArgb(255, 235, 235),
                    Enabled = trashCount > 0
                };
                cleanBtn.Click += (s, e) =>
                {
                    var ask = MessageBox.Show(form,
                        $"Вы действительно хотите навсегда очистить корзину канала '{channelName}'?\n\n" +
                        $"⚠️ ВНИМАНИЕ: Это удалит {trashCount} файлов ({FormatSize(totalTrashSize)}) и связанные с ними сообщения из Telegram без возможности восстановления!",
                        "Подтверждение очистки корзины",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (ask == DialogResult.Yes)
                    {
                        var msgIds = db.EmptyTrash(mountId);
                        if (channelId != 0 && msgIds.Count > 0)
                        {
                            System.Threading.Tasks.Task.Run(() => TelegramManager.DeleteMessagesAsync(channelId, msgIds.ToArray()));
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
                    Top = 230,
                    Width = 100,
                    Height = 32,
                    DialogResult = DialogResult.OK
                };

                form.Controls.Add(cleanBtn);
                form.Controls.Add(closeBtn);
                form.CancelButton = closeBtn;

                form.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Log($"ShowTrashProperties exception: {ex}");
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
