using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TgVfsPlugin;

public class CreateFolderResult
{
    public string Name { get; set; } = "";
    public int Mode { get; set; } // 0 = Mirror, 1 = Container
    public string LocalPath { get; set; } = "";
}

public static class CreateFolderDialog
{
    public static CreateFolderResult? Show()
    {
        CreateFolderResult? result = null;

        FormExtensions.RunInSta(() =>
        {
            try
            {
                Logger.Info("UI", "Initializing CreateFolderDialog...");
                Win32Api.EnsureVisualStyles();
                using ToolTip toolTip = UiTheme.CreateToolTip();

                using Form form = new Form()
                {
                    Width = 490,
                    Height = 310,
                    MinimumSize = new Size(490, 310),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Создать папку (Канал)",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont
                };

                // 1. Название папки
                Label nameLabel = new Label() { Left = 20, Top = 16, Width = 430, Text = "Название папки:" };
                TextBox nameBox = new TextBox() { Left = 20, Top = 38, Width = 430 };

                // 2. Локальный путь для зеркала + кнопка Обзор...
                Label pathLabel = new Label() { Left = 20, Top = 72, Width = 430, Text = "Локальный путь (только для Зеркала):" };
                
                int browseBtnWidth = 95;
                int pathBoxWidth = 430 - browseBtnWidth - 8;
                
                TextBox pathBox = new TextBox() 
                { 
                    Left = 20, 
                    Top = 94, 
                    Width = pathBoxWidth, 
                    ReadOnly = true,
                    BackColor = SystemColors.Window
                };

                Button browseBtn = UiTheme.CreateButton("Обзор...", "Выбрать локальную папку для создания Зеркала", toolTip, browseBtnWidth, 23);
                browseBtn.Left = 20 + pathBoxWidth + 8;
                browseBtn.Top = 93;
                browseBtn.Height = 25;

                // 3. Режим работы (смещен вниз)
                Label modeLabel = new Label() { Left = 20, Top = 130, Width = 430, Text = "Режим работы папки:" };
                RadioButton modeMirror = new RadioButton() { Left = 20, Top = 152, Width = 430, Text = "Зеркало (Бэкап локальной папки)", Checked = true };
                RadioButton modeContainer = new RadioButton() { Left = 20, Top = 178, Width = 430, Text = "Контейнер (Обычная виртуальная папка)" };

                // Реакция на смену режима
                void UpdateModeState()
                {
                    bool isMirror = modeMirror.Checked;
                    pathBox.Enabled = isMirror;
                    browseBtn.Enabled = isMirror;
                    pathLabel.Enabled = isMirror;
                }

                modeMirror.CheckedChanged += (s, e) => UpdateModeState();
                modeContainer.CheckedChanged += (s, e) => UpdateModeState();

                // Обработчик кнопки "Обзор..."
                browseBtn.Click += (s, e) =>
                {
                    try
                    {
                        using FolderBrowserDialog fbd = new FolderBrowserDialog();
                        fbd.Description = "Выберите локальную папку для зеркалирования в Telegram-канал:";
                        fbd.ShowNewFolderButton = true;
                        
                        if (!string.IsNullOrWhiteSpace(pathBox.Text) && Directory.Exists(pathBox.Text))
                        {
                            fbd.SelectedPath = pathBox.Text;
                        }

                        if (fbd.ShowDialog(form) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                        {
                            string selectedDir = fbd.SelectedPath.TrimEnd('\\', '/');
                            pathBox.Text = selectedDir;

                            // Если имя папки пустое - автоматически заполняем его
                            if (string.IsNullOrWhiteSpace(nameBox.Text))
                            {
                                string folderName = Path.GetFileName(selectedDir);
                                
                                // Если выбран корень диска (например "C:" или "D:")
                                if (string.IsNullOrEmpty(folderName))
                                {
                                    string driveRoot = Path.GetPathRoot(fbd.SelectedPath) ?? "";
                                    string driveLetter = driveRoot.TrimEnd('\\', '/', ':');
                                    folderName = !string.IsNullOrEmpty(driveLetter) ? $"Диск ({driveLetter})" : "Диск";
                                }

                                nameBox.Text = folderName;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("UI", "Error selecting folder in CreateFolderDialog", ex);
                        MessageBox.Show($"Ошибка выбора папки: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                // Нижняя панель
                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                Button okBtn = UiTheme.CreateButton("OK", "Создать папку/канал", toolTip, 85, dialogResult: DialogResult.OK);
                Button cancelBtn = UiTheme.CreateButton("Отмена", "Отменить создание", toolTip, 85, dialogResult: DialogResult.Cancel);

                okBtn.Left = form.ClientSize.Width - 20 - okBtn.Width;
                okBtn.Top = 11;
                okBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                cancelBtn.Left = okBtn.Left - 10 - cancelBtn.Width;
                cancelBtn.Top = 11;
                cancelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                bottomPanel.Controls.Add(okBtn);
                bottomPanel.Controls.Add(cancelBtn);

                form.Controls.Add(nameLabel);
                form.Controls.Add(nameBox);
                form.Controls.Add(pathLabel);
                form.Controls.Add(pathBox);
                form.Controls.Add(browseBtn);
                form.Controls.Add(modeLabel);
                form.Controls.Add(modeMirror);
                form.Controls.Add(modeContainer);

                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;

                form.Shown += (s, e) =>
                {
                    nameBox.Focus();
                };

                if (form.ShowModalTc() == DialogResult.OK)
                {
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {
                        MessageBox.Show("Введите название папки", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    if (modeMirror.Checked && string.IsNullOrWhiteSpace(pathBox.Text))
                    {
                        MessageBox.Show("Для режима «Зеркало» необходимо выбрать локальную папку через кнопку «Обзор...»", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    
                    result = new CreateFolderResult
                    {
                        Name = nameBox.Text.Trim(),
                        Mode = modeMirror.Checked ? 0 : 1,
                        LocalPath = modeMirror.Checked ? pathBox.Text.Trim() : ""
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "CreateFolderDialog Error", ex);
            }
        });

        return result;
    }
}
