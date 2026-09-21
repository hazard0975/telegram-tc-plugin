using System;
using System.Drawing;
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
                    Height = 345,
                    MinimumSize = new Size(490, 345),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Создать папку (Канал)",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont
                };

                Label nameLabel = new Label() { Left = 20, Top = 20, Width = 430, Text = "Название папки:" };
                TextBox nameBox = new TextBox() { Left = 20, Top = 45, Width = 430 };

                RadioButton modeMirror = new RadioButton() { Left = 20, Top = 85, Width = 430, Text = "Зеркало (Бэкап локальной папки)", Checked = true };
                RadioButton modeContainer = new RadioButton() { Left = 20, Top = 115, Width = 430, Text = "Контейнер (Обычная виртуальная папка)" };

                Label pathLabel = new Label() { Left = 20, Top = 155, Width = 430, Text = "Локальный путь (только для Зеркала):" };
                TextBox pathBox = new TextBox() { Left = 20, Top = 180, Width = 430 };

                modeMirror.CheckedChanged += (s, e) => {
                    pathBox.Enabled = modeMirror.Checked;
                };

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
                form.Controls.Add(modeMirror);
                form.Controls.Add(modeContainer);
                form.Controls.Add(pathLabel);
                form.Controls.Add(pathBox);

                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;

                form.Shown += (s, e) =>
                {
                    nameBox.Focus();
                };

                if (form.ShowModalTc() == DialogResult.OK)
                {
                    if (string.IsNullOrWhiteSpace(nameBox.Text)) {
                        MessageBox.Show("Введите название папки", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
