using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TgVfsPlugin;

public class SettingsDialogResult
{
    public StorageMode SelectedStorageMode { get; set; }
    public string CustomPath { get; set; } = "";
    public bool MigrateExistingFiles { get; set; }
    public bool StorageLocationChanged { get; set; }
}

public static class SettingsDialog
{
    public static SettingsDialogResult? Show()
    {
        SettingsDialogResult? result = null;

        FormExtensions.RunInSta(() =>
        {
            try
            {
                Logger.Info("UI", "Opening SettingsDialog...");
                Win32Api.EnsureVisualStyles();

                using Form form = new Form()
                {
                    Width = 540,
                    Height = 400,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Настройки Telegram VFS",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = new Font("Segoe UI", 9)
                };

                GroupBox storageGroup = new GroupBox()
                {
                    Text = "Расположение базы данных и сессии",
                    Left = 15,
                    Top = 12,
                    Width = 495,
                    Height = 250,
                    Font = new Font("Segoe UI", 9, FontStyle.Regular)
                };

                // 1. По умолчанию (%APPDATA%)
                RadioButton rbDefault = new RadioButton()
                {
                    Text = "По умолчанию (%APPDATA%)",
                    Left = 16,
                    Top = 22,
                    Width = 460,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.DefaultAppData
                };

                Label lblDefaultPath = new Label()
                {
                    Text = SettingsManager.DefaultAppDataDirectory,
                    Left = 38,
                    Top = 45,
                    Width = 440,
                    Height = 20,
                    ForeColor = Color.DimGray,
                    Cursor = Cursors.Hand
                };
                lblDefaultPath.Click += (s, e) => rbDefault.Checked = true;

                // 2. Портативный режим (рядом с плагином)
                RadioButton rbPortable = new RadioButton()
                {
                    Text = "Портативный режим (рядом с плагином)",
                    Left = 16,
                    Top = 72,
                    Width = 460,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Portable
                };

                Label lblPortablePath = new Label()
                {
                    Text = SettingsManager.PortableDirectory,
                    Left = 38,
                    Top = 95,
                    Width = 440,
                    Height = 20,
                    ForeColor = Color.DimGray,
                    Cursor = Cursors.Hand
                };
                lblPortablePath.Click += (s, e) => rbPortable.Checked = true;

                // 3. Пользовательская папка
                RadioButton rbCustom = new RadioButton()
                {
                    Text = "Пользовательская папка на диске:",
                    Left = 16,
                    Top = 122,
                    Width = 460,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Custom
                };

                TextBox customPathBox = new TextBox()
                {
                    Left = 38,
                    Top = 148,
                    Width = 345,
                    Text = SettingsManager.CurrentStorageMode == StorageMode.Custom 
                        ? SettingsManager.DataDirectory 
                        : (SettingsManager.GetSetting("data_path") ?? "D:\\TelegramVFS_Data"),
                    Enabled = rbCustom.Checked
                };

                Button browseBtn = new Button()
                {
                    Text = "Обзор...",
                    Left = 390,
                    Top = 146,
                    Width = 88,
                    Height = 30,
                    Enabled = rbCustom.Checked
                };

                CheckBox migrateCheck = new CheckBox()
                {
                    Text = "Перенести существующую сессию и базу данных в новую папку",
                    Left = 16,
                    Top = 190,
                    Width = 465,
                    Height = 44,
                    CheckAlign = ContentAlignment.TopLeft,
                    TextAlign = ContentAlignment.TopLeft,
                    Checked = true,
                    ForeColor = Color.DarkSlateBlue
                };

                rbCustom.CheckedChanged += (s, e) =>
                {
                    customPathBox.Enabled = rbCustom.Checked;
                    browseBtn.Enabled = rbCustom.Checked;
                };

                browseBtn.Click += (s, e) =>
                {
                    using FolderBrowserDialog fbd = new FolderBrowserDialog()
                    {
                        Description = "Выберите папку для хранения базы данных и сессии Telegram",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true
                    };
                    if (Directory.Exists(customPathBox.Text))
                    {
                        fbd.SelectedPath = customPathBox.Text;
                    }
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        customPathBox.Text = fbd.SelectedPath;
                    }
                };

                storageGroup.Controls.Add(rbDefault);
                storageGroup.Controls.Add(lblDefaultPath);
                storageGroup.Controls.Add(rbPortable);
                storageGroup.Controls.Add(lblPortablePath);
                storageGroup.Controls.Add(rbCustom);
                storageGroup.Controls.Add(customPathBox);
                storageGroup.Controls.Add(browseBtn);
                storageGroup.Controls.Add(migrateCheck);

                CheckBox oppositePanelCheck = new CheckBox()
                {
                    Text = "Быстрый переход из свойств: открывать на противоположной панели",
                    Left = 20,
                    Top = 272,
                    Width = 490,
                    Height = 22,
                    Checked = SettingsManager.PropertiesNavigationOppositePanel
                };

                Button okBtn = new Button() { Text = "Сохранить", Left = 265, Width = 115, Height = 30, Top = 310, DialogResult = DialogResult.OK };
                Button cancelBtn = new Button() { Text = "Отмена", Left = 390, Width = 115, Height = 30, Top = 310, DialogResult = DialogResult.Cancel };

                form.Controls.Add(storageGroup);
                form.Controls.Add(oppositePanelCheck);
                form.Controls.Add(okBtn);
                form.Controls.Add(cancelBtn);

                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;

                if (form.ShowModalTc() == DialogResult.OK)
                {
                    SettingsManager.PropertiesNavigationOppositePanel = oppositePanelCheck.Checked;
                    StorageMode newMode = StorageMode.DefaultAppData;
                    string newPath = "";

                    if (rbPortable.Checked)
                    {
                        newMode = StorageMode.Portable;
                    }
                    else if (rbCustom.Checked)
                    {
                        newMode = StorageMode.Custom;
                        newPath = customPathBox.Text.Trim();
                        if (string.IsNullOrWhiteSpace(newPath))
                        {
                            MessageBox.Show("Пожалуйста, укажите путь к пользовательской папке.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }

                    string oldDataDir = SettingsManager.DataDirectory;
                    string targetDataDir = newMode switch
                    {
                        StorageMode.Portable => SettingsManager.PortableDirectory,
                        StorageMode.Custom => newPath,
                        _ => SettingsManager.DefaultAppDataDirectory
                    };

                    bool pathChanged = !string.Equals(oldDataDir.TrimEnd('\\', '/'), targetDataDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

                    result = new SettingsDialogResult
                    {
                        SelectedStorageMode = newMode,
                        CustomPath = newPath,
                        MigrateExistingFiles = migrateCheck.Checked,
                        StorageLocationChanged = pathChanged
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "SettingsDialog Error", ex);
            }
        });

        return result;
    }
}
