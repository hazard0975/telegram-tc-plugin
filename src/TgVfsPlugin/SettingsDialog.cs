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

        var t = new System.Threading.Thread(() =>
        {
            try
            {
                Logger.Log("Opening SettingsDialog...");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using Form form = new Form()
                {
                    Width = 540,
                    Height = 380,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Настройки Telegram VFS",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = true,
                    Font = new Font("Segoe UI", 9)
                };

                GroupBox storageGroup = new GroupBox()
                {
                    Text = "Расположение базы данных и сессии",
                    Left = 15,
                    Top = 15,
                    Width = 495,
                    Height = 250
                };

                RadioButton rbDefault = new RadioButton()
                {
                    Text = $"По умолчанию (%APPDATA%)\n{SettingsManager.DefaultAppDataDirectory}",
                    Left = 20,
                    Top = 25,
                    Width = 455,
                    Height = 45,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.DefaultAppData
                };

                RadioButton rbPortable = new RadioButton()
                {
                    Text = $"Портативный режим (рядом с плагином)\n{SettingsManager.PortableDirectory}",
                    Left = 20,
                    Top = 75,
                    Width = 455,
                    Height = 45,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Portable
                };

                RadioButton rbCustom = new RadioButton()
                {
                    Text = "Пользовательская папка на диске:",
                    Left = 20,
                    Top = 125,
                    Width = 455,
                    Height = 25,
                    Checked = SettingsManager.CurrentStorageMode == StorageMode.Custom
                };

                TextBox customPathBox = new TextBox()
                {
                    Left = 40,
                    Top = 155,
                    Width = 340,
                    Text = SettingsManager.CurrentStorageMode == StorageMode.Custom 
                        ? SettingsManager.DataDirectory 
                        : (SettingsManager.GetSetting("data_path") ?? "D:\\TelegramVFS_Data"),
                    Enabled = rbCustom.Checked
                };

                Button browseBtn = new Button()
                {
                    Text = "Обзор...",
                    Left = 390,
                    Top = 154,
                    Width = 85,
                    Height = 25,
                    Enabled = rbCustom.Checked
                };

                CheckBox migrateCheck = new CheckBox()
                {
                    Text = "Перенести существующую сессию и базу данных в новую папку",
                    Left = 20,
                    Top = 195,
                    Width = 455,
                    Height = 35,
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
                storageGroup.Controls.Add(rbPortable);
                storageGroup.Controls.Add(rbCustom);
                storageGroup.Controls.Add(customPathBox);
                storageGroup.Controls.Add(browseBtn);
                storageGroup.Controls.Add(migrateCheck);

                Button okBtn = new Button() { Text = "Сохранить", Left = 390, Width = 120, Height = 30, Top = 285, DialogResult = DialogResult.OK };
                Button cancelBtn = new Button() { Text = "Отмена", Left = 260, Width = 120, Height = 30, Top = 285, DialogResult = DialogResult.Cancel };

                form.Controls.Add(storageGroup);
                form.Controls.Add(okBtn);
                form.Controls.Add(cancelBtn);

                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;

                if (form.ShowDialog() == DialogResult.OK)
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
                Logger.Log($"SettingsDialog Error: {ex}");
            }
        });

        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();

        return result;
    }
}
