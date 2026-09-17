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

        var t = new System.Threading.Thread(() =>
        {
            try
            {
                Logger.Info("UI", "Initializing CreateFolderDialog...");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using Form form = new Form()
                {
                    Width = 480,
                    Height = 330,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Создать папку (Канал)",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = true,
                    Font = new Font("Segoe UI", 9)
                };

                Label nameLabel = new Label() { Left = 20, Top = 20, Width = 420, Text = "Название папки:" };
                TextBox nameBox = new TextBox() { Left = 20, Top = 45, Width = 420 };

                RadioButton modeMirror = new RadioButton() { Left = 20, Top = 85, Width = 420, Text = "Зеркало (Бэкап локальной папки)", Checked = true };
                RadioButton modeContainer = new RadioButton() { Left = 20, Top = 115, Width = 420, Text = "Контейнер (Обычная виртуальная папка)" };

                Label pathLabel = new Label() { Left = 20, Top = 155, Width = 420, Text = "Локальный путь (только для Зеркала):" };
                TextBox pathBox = new TextBox() { Left = 20, Top = 180, Width = 420 };

                modeMirror.CheckedChanged += (s, e) => {
                    pathBox.Enabled = modeMirror.Checked;
                };

                Button okBtn = new Button() { Text = "OK", Left = 340, Width = 100, Height = 30, Top = 235, DialogResult = DialogResult.OK };
                Button cancelBtn = new Button() { Text = "Отмена", Left = 230, Width = 100, Height = 30, Top = 235, DialogResult = DialogResult.Cancel };

                form.Controls.Add(nameLabel);
                form.Controls.Add(nameBox);
                form.Controls.Add(modeMirror);
                form.Controls.Add(modeContainer);
                form.Controls.Add(pathLabel);
                form.Controls.Add(pathBox);
                form.Controls.Add(okBtn);
                form.Controls.Add(cancelBtn);

                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;

                if (form.ShowDialog() == DialogResult.OK)
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

        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();

        return result;
    }
}
