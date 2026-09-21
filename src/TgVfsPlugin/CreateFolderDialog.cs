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
                    Height = 285,
                    MinimumSize = new Size(490, 285),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Создать папку (Канал)",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont
                };

                int margin = 20;
                int contentWidth = form.ClientSize.Width - margin * 2; // точно 434px при ширине 490

                // 1. Название папки
                Label nameLabel = new Label() { Left = margin, Top = 16, Height = 18, Width = contentWidth, Text = "Название папки:" };
                TextBox nameBox = new TextBox() { Left = margin, Top = 38, Width = contentWidth };

                // 2. Локальный путь для зеркала + кнопка Обзор...
                Label pathLabel = new Label() { Left = margin, Top = 72, Height = 18, Width = contentWidth, Text = "Локальный путь (только для Зеркала):" };
                
                int browseBtnWidth = 85;
                int spacing = 8;
                int pathBoxWidth = contentWidth - browseBtnWidth - spacing;
                
                TextBox pathBox = new TextBox() 
                { 
                    Left = margin, 
                    Top = 94, 
                    Width = pathBoxWidth, 
                    ReadOnly = true,
                    BackColor = SystemColors.Window
                };

                // Высота и положение кнопки строго выравниваются по Textbox
                Button browseBtn = UiTheme.CreateButton("Обзор...", "Выбрать локальную папку для создания Зеркала", toolTip, browseBtnWidth, 23);
                browseBtn.Left = margin + pathBoxWidth + spacing;
                browseBtn.Top = 93;
                browseBtn.Height = 25;

                // 3. Режим работы (смещен вниз)
                Label modeLabel = new Label() { Left = margin, Top = 132, Height = 18, Width = contentWidth, Text = "Режим работы папки:" };
                RadioButton modeMirror = new RadioButton() { Left = margin, Top = 154, Width = contentWidth, Text = "Зеркало (Бэкап локальной папки)", Checked = true };
                RadioButton modeContainer = new RadioButton() { Left = margin, Top = 180, Width = contentWidth, Text = "Контейнер (Обычная виртуальная папка)" };

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
