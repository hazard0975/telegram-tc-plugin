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
                using ToolTip toolTip = UiTheme.CreateToolTip();

                using Form form = new Form()
                {
                    Width = 550,
                    Height = 375,
                    MinimumSize = new Size(550, 375),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Настройки Telegram VFS",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont
                };

                GroupBox storageGroup = new GroupBox()
                {
                    Text = "Расположение базы данных и сессии",
                    Left = 15,
                    Top = 12,
                    Width = 505,
                    Height = 250,
                    Font = UiTheme.DefaultFont
                };

                // 1. По умолчанию (%APPDATA%)
                RadioButton rbDefault = new RadioButton()
                {
                    Text = "По умолчанию (%APPDATA%)",
                    Left = 16,
                    Top = 22,
                    Width = 470,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.DefaultAppData
                };

                Label lblDefaultPath = new Label()
                {
                    Text = SettingsManager.DefaultAppDataDirectory,
                    Left = 38,
                    Top = 45,
                    Width = 450,
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
                    Width = 470,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Portable
                };

                Label lblPortablePath = new Label()
                {
                    Text = SettingsManager.PortableDirectory,
                    Left = 38,
                    Top = 95,
                    Width = 450,
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
                    Width = 470,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Custom
                };

                TextBox customPathBox = new TextBox()
                {
                    Left = 38,
                    Top = 148,
                    Width = 355,
                    Text = SettingsManager.CurrentStorageMode == StorageMode.Custom 
                        ? SettingsManager.DataDirectory 
                        : (SettingsManager.GetSetting("data_path") ?? "D:\\TelegramVFS_Data"),
                    Enabled = rbCustom.Checked
                };

                Button browseBtn = UiTheme.CreateButton("Обзор...", "Выбрать пользовательскую папку на диске", toolTip, 88);
                browseBtn.Left = 400;
                browseBtn.Top = 146;
                browseBtn.Enabled = rbCustom.Checked;

                CheckBox migrateCheck = new CheckBox()
                {
                    Text = "Перенести существующую сессию и базу данных в новую папку",
                    Left = 16,
                    Top = 190,
                    Width = 475,
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

                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                Button okBtn = UiTheme.CreateButton("Сохранить", "Сохранить настройки и применить расположение данных", toolTip, 100, dialogResult: DialogResult.OK);
                Button cancelBtn = UiTheme.CreateButton("Отмена", "Отменить изменения настроек", toolTip, 90, dialogResult: DialogResult.Cancel);

                cancelBtn.Left = form.ClientSize.Width - 20 - cancelBtn.Width;
                cancelBtn.Top = 11;
                cancelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                okBtn.Left = cancelBtn.Left - 10 - okBtn.Width;
                okBtn.Top = 11;
                okBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                bottomPanel.Controls.Add(okBtn);
                bottomPanel.Controls.Add(cancelBtn);

                form.Controls.Add(storageGroup);

                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;

                if (form.ShowModalTc() == DialogResult.OK)
                {
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
