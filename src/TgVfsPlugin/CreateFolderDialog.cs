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

                int margin = 20;
                int topMargin = 16;
                int clientWidth = 474;
                int contentWidth = clientWidth - margin * 2; // 434px
                int clientHeight = 282;

                using Form form = UiTheme.CreateDialogForm("Создать папку (Канал)", clientWidth, clientHeight);

                // 1. Название папки
                Label nameLabel = new Label() 
                { 
                    Left = margin, 
                    Top = topMargin, 
                    Height = 22, 
                    Width = contentWidth, 
                    Text = "Название папки:",
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right 
                };
                TextBox nameBox = UiTheme.CreateTextBox();
                nameBox.Left = margin;
                nameBox.Top = nameLabel.Bottom + 4;
                nameBox.Width = contentWidth;
                nameBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

                // 2. Локальный путь для зеркала + кнопка Обзор...
                Label pathLabel = new Label() 
                { 
                    Left = margin, 
                    Top = nameBox.Bottom + 10, 
                    Height = 22, 
                    Width = contentWidth, 
                    Text = "Локальный путь (только для Зеркала):",
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right 
                };
                
                int browseBtnWidth = 80;
                int spacing = 10;
                
                Button browseBtn = UiTheme.CreateButton("Обзор...", "Выбрать локальную папку для создания Зеркала", toolTip, browseBtnWidth, 30);
                browseBtn.Left = clientWidth - margin - browseBtnWidth;
                browseBtn.Top = pathLabel.Bottom + 4;
                browseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                int pathBoxWidth = browseBtn.Left - spacing - margin;
                TextBox pathBox = UiTheme.CreateTextBox(readOnly: true);
                pathBox.Left = margin;
                pathBox.Top = pathLabel.Bottom + 4;
                pathBox.Width = pathBoxWidth;
                pathBox.BackColor = SystemColors.Window;
                pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

                // 3. Режим работы
                Label modeLabel = new Label() 
                { 
                    Left = margin, 
                    Top = pathBox.Bottom + 10, 
                    Height = 22, 
                    Width = contentWidth, 
                    Text = "Режим работы папки:",
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right 
                };
                RadioButton modeMirror = new RadioButton() 
                { 
                    Left = margin, 
                    Top = modeLabel.Bottom + 4, 
                    Width = contentWidth, 
                    Height = 24,
                    Text = "Зеркало (Бэкап локальной папки)", 
                    Checked = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right 
                };
                RadioButton modeContainer = new RadioButton() 
                { 
                    Left = margin, 
                    Top = modeMirror.Bottom + 4, 
                    Width = contentWidth, 
                    Height = 24,
                    Text = "Контейнер (Обычная виртуальная папка)",
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right 
                };

                form.Layout += (s, e) =>
                {
                    browseBtn.Left = nameBox.Right - browseBtn.Width;
                    pathBox.Width = browseBtn.Left - spacing - pathBox.Left;
                };

                form.MinimumSize = form.Size;
                form.MaximumSize = form.Size;

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
                        fbd.Description = ""; // Убираем громоздкое нижнее описание, чтобы окно выглядело аккуратно
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

                Button okBtn = UiTheme.CreateButton("OK", "Создать папку/канал", toolTip, 85);
                Button cancelBtn = UiTheme.CreateButton("Отмена", "Отменить создание", toolTip, 85, dialogResult: DialogResult.Cancel);

                okBtn.Left = form.ClientSize.Width - 20 - okBtn.Width;
                okBtn.Top = 11;
                okBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                cancelBtn.Left = okBtn.Left - 10 - cancelBtn.Width;
                cancelBtn.Top = 11;
                cancelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                okBtn.Click += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {
                        MessageBox.Show("Введите название папки", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        nameBox.Focus();
                        return;
                    }

                    if (modeMirror.Checked && string.IsNullOrWhiteSpace(pathBox.Text))
                    {
                        MessageBox.Show("Для режима «Зеркало» необходимо выбрать локальную папку через кнопку «Обзор...»", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        browseBtn.Focus();
                        return;
                    }

                    result = new CreateFolderResult
                    {
                        Name = nameBox.Text.Trim(),
                        Mode = modeMirror.Checked ? 0 : 1,
                        LocalPath = modeMirror.Checked ? pathBox.Text.Trim() : ""
                    };

                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };

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

                form.ShowModalTc();
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "CreateFolderDialog Error", ex);
            }
        });

        return result;
    }
}
