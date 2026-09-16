using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TgVfsPlugin;

public static class FilePropertiesDialog
{
    public static void Show(string channelName, long channelId, string relativePath, VfsDatabase.FileRecord file)
    {
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using Form form = new Form()
                {
                    Width = 500,
                    Height = 440,
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
                    Width = 500,
                    Height = 60,
                    BackColor = Color.FromArgb(245, 247, 250)
                };

                Label titleLabel = new Label()
                {
                    Left = 20,
                    Top = 12,
                    Width = 440,
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
                    Width = 440,
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
                    Width = 445,
                    Height = 265,
                    Text = "Параметры Telegram VFS"
                };

                int curTop = 25;
                int labelWidth = 145;
                int valueWidth = 275;
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
                    Text = "Копировать инфо",
                    Left = 20,
                    Top = 355,
                    Width = 135,
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

                Button okBtn = new Button()
                {
                    Text = "Закрыть",
                    Left = 365,
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
                    okBtn.Focus();
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

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
