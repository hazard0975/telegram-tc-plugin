using System;
using System.Drawing;
using System.Windows.Forms;

namespace TgVfsPlugin;

public static class InputDialog
{
    public static string? Show(string prompt, string title, bool isPassword = false)
    {
        string? result = null;

        FormExtensions.RunInSta(() =>
        {
            try
            {
                Logger.Debug("UI", "Initializing WinForms dialog...");
                Win32Api.EnsureVisualStyles();

                using Form promptForm = new Form()
                {
                    Width = 460,
                    Height = 200,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = title,
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = new Font("Segoe UI", 9)
                };

                Label textLabel = new Label() { Left = 20, Top = 20, Width = 400, Height = 40, Text = prompt };
                TextBox inputBox = new TextBox() { Left = 20, Top = 65, Width = 400 };
                
                if (isPassword)
                {
                    inputBox.UseSystemPasswordChar = true;
                }

                Button confirmation = new Button() { Text = "OK", Left = 320, Width = 100, Height = 30, Top = 115, DialogResult = DialogResult.OK };
                Button cancel = new Button() { Text = "Cancel", Left = 210, Width = 100, Height = 30, Top = 115, DialogResult = DialogResult.Cancel };

                promptForm.Controls.Add(textLabel);
                promptForm.Controls.Add(inputBox);
                promptForm.Controls.Add(confirmation);
                promptForm.Controls.Add(cancel);
                promptForm.AcceptButton = confirmation;
                promptForm.CancelButton = cancel;

                promptForm.Shown += (s, e) =>
                {
                    inputBox.Focus();
                    inputBox.SelectAll();
                };

                Logger.Info("UI", $"Showing input dialog: '{title}'");
                if (promptForm.ShowModalTc() == DialogResult.OK)
                {
                    result = inputBox.Text;
                    Logger.Info("UI", $"Input dialog '{title}' submitted (OK).");
                }
                else
                {
                    Logger.Info("UI", $"Input dialog '{title}' cancelled or dismissed.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UI", $"Input dialog error ('{title}')", ex);
            }
        });

        return result;
    }
}
