using System;
using System.Drawing;
using System.Windows.Forms;

namespace TgVfsPlugin;

public static class InputDialog
{
    public static string? Show(string prompt, string title, bool isPassword = false)
    {
        string? result = null;

        // В Native AOT и плагинах TC потоки могут быть MTA или без цикла сообщений.
        // Поэтому для WinForms-окон надежнее всего создавать выделенный STA-поток.
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                Logger.Log("Initializing WinForms dialog...");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using Form promptForm = new Form()
                {
                    Width = 400,
                    Height = 180,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = title,
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = true
                };

                Label textLabel = new Label() { Left = 20, Top = 20, Width = 340, Text = prompt };
                TextBox inputBox = new TextBox() { Left = 20, Top = 50, Width = 340 };
                
                if (isPassword)
                {
                    inputBox.UseSystemPasswordChar = true;
                }

                Button confirmation = new Button() { Text = "OK", Left = 260, Width = 100, Top = 90, DialogResult = DialogResult.OK };
                Button cancel = new Button() { Text = "Cancel", Left = 150, Width = 100, Top = 90, DialogResult = DialogResult.Cancel };

                promptForm.Controls.Add(textLabel);
                promptForm.Controls.Add(inputBox);
                promptForm.Controls.Add(confirmation);
                promptForm.Controls.Add(cancel);
                promptForm.AcceptButton = confirmation;
                promptForm.CancelButton = cancel;

                Logger.Log($"Showing dialog for: {title}");
                if (promptForm.ShowDialog() == DialogResult.OK)
                {
                    result = inputBox.Text;
                    Logger.Log("Dialog closed with OK.");
                }
                else
                {
                    Logger.Log("Dialog closed with Cancel or dismissed.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UI Error: {ex}");
            }
        });

        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join(); // Ждем завершения ввода

        return result;
    }
}
