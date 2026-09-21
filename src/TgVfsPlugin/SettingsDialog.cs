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

                int margin = 20;
                int topMargin = 16;
                int clientWidth = 530;
                int contentWidth = clientWidth - margin * 2; // 490px

                using Form form = new Form()
                {
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Настройки Telegram VFS",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont,
                    AutoScaleMode = AutoScaleMode.None
                };

                GroupBox storageGroup = new GroupBox()
                {
                    Text = "Расположение базы данных и сессии",
                    Left = margin,
                    Top = topMargin,
                    Width = contentWidth,
                    Height = 240,
                    Font = UiTheme.DefaultFont,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                int innerMargin = 16;
                int innerContentWidth = contentWidth - innerMargin * 2; // 458px

                // 1. По умолчанию (%APPDATA%)
                RadioButton rbDefault = new RadioButton()
                {
                    Text = "По умолчанию (%APPDATA%)",
                    Left = innerMargin,
                    Top = 22,
                    Width = innerContentWidth,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.DefaultAppData,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                Label lblDefaultPath = new Label()
                {
                    Text = SettingsManager.DefaultAppDataDirectory,
                    Left = 38,
                    Top = 45,
                    Width = innerContentWidth - 22,
                    Height = 20,
                    ForeColor = Color.DimGray,
                    Cursor = Cursors.Hand,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                lblDefaultPath.Click += (s, e) => rbDefault.Checked = true;

                // 2. Портативный режим (рядом с плагином)
                RadioButton rbPortable = new RadioButton()
                {
                    Text = "Портативный режим (рядом с плагином)",
                    Left = innerMargin,
                    Top = 72,
                    Width = innerContentWidth,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Portable,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                Label lblPortablePath = new Label()
                {
                    Text = SettingsManager.PortableDirectory,
                    Left = 38,
                    Top = 95,
                    Width = innerContentWidth - 22,
                    Height = 20,
                    ForeColor = Color.DimGray,
                    Cursor = Cursors.Hand,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                lblPortablePath.Click += (s, e) => rbPortable.Checked = true;

                // 3. Пользовательская папка
                RadioButton rbCustom = new RadioButton()
                {
                    Text = "Пользовательская папка на диске:",
                    Left = innerMargin,
                    Top = 122,
                    Width = innerContentWidth,
                    Height = 22,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Custom,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                int browseBtnWidth = 85;
                Button browseBtn = UiTheme.CreateButton("Обзор...", "Выбрать пользовательскую папку на диске", toolTip, browseBtnWidth, 23);
                browseBtn.Left = contentWidth - innerMargin - browseBtnWidth;
                browseBtn.Top = 146;
                browseBtn.Enabled = rbCustom.Checked;
                browseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                TextBox customPathBox = new TextBox()
                {
                    Left = 38,
                    Top = 148,
                    Width = browseBtn.Left - 8 - 38,
                    Text = SettingsManager.CurrentStorageMode == StorageMode.Custom 
                        ? SettingsManager.DataDirectory 
                        : (SettingsManager.GetSetting("data_path") ?? "D:\\TelegramVFS_Data"),
                    Enabled = rbCustom.Checked,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                browseBtn.Height = customPathBox.Height + 2;

                CheckBox migrateCheck = new CheckBox()
                {
                    Text = "Перенести существующую сессию и базу данных в новую папку",
                    Left = innerMargin,
                    Top = 186,
                    Width = innerContentWidth,
                    Height = 40,
                    CheckAlign = ContentAlignment.TopLeft,
                    TextAlign = ContentAlignment.TopLeft,
                    Checked = true,
                    ForeColor = Color.DarkSlateBlue,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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

                int bottomPanelHeight = 52;
                int contentBottom = storageGroup.Bottom + topMargin;
                int clientHeight = contentBottom + bottomPanelHeight;

                form.ClientSize = new Size(clientWidth, clientHeight);
                form.MinimumSize = form.Size;
                form.MaximumSize = form.Size;

                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                Button okBtn = UiTheme.CreateButton("Сохранить", "Сохранить настройки и применить расположение данных", toolTip, 100, dialogResult: DialogResult.OK);
                Button cancelBtn = UiTheme.CreateButton("Отмена", "Отменить изменения настроек", toolTip, 90, dialogResult: DialogResult.Cancel);

                cancelBtn.Left = form.ClientSize.Width - margin - cancelBtn.Width;
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
