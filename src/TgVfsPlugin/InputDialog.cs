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
                using ToolTip toolTip = UiTheme.CreateToolTip();

                using Form promptForm = new Form()
                {
                    Width = 460,
                    Height = 210,
                    MinimumSize = new Size(460, 210),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = title,
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont
                };

                Label textLabel = new Label() { Left = 20, Top = 20, Width = 400, Height = 40, Text = prompt };
                TextBox inputBox = new TextBox() { Left = 20, Top = 65, Width = 400 };
                
                if (isPassword)
                {
                    inputBox.UseSystemPasswordChar = true;
                }

                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                promptForm.Controls.Add(bottomPanel);

                Button confirmation = UiTheme.CreateButton("OK", "Подтвердить ввод", toolTip, 85, dialogResult: DialogResult.OK);
                Button cancel = UiTheme.CreateButton("Отмена", "Отменить ввод", toolTip, 85, dialogResult: DialogResult.Cancel);

                confirmation.Left = promptForm.ClientSize.Width - 20 - confirmation.Width;
                confirmation.Top = 11;
                confirmation.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                cancel.Left = confirmation.Left - 10 - cancel.Width;
                cancel.Top = 11;
                cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                bottomPanel.Controls.Add(confirmation);
                bottomPanel.Controls.Add(cancel);

                promptForm.Controls.Add(textLabel);
                promptForm.Controls.Add(inputBox);
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
