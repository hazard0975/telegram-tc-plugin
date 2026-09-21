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

                int margin = 20;
                int topMargin = 16;
                int clientWidth = 474;
                int contentWidth = clientWidth - margin * 2; // 434px

                using Form promptForm = new Form()
                {
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = title,
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont,
                    AutoScaleMode = AutoScaleMode.None
                };

                Label textLabel = new Label() 
                { 
                    Left = margin, 
                    Top = topMargin, 
                    Width = contentWidth, 
                    Height = 36, 
                    Text = prompt,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right 
                };

                TextBox inputBox = new TextBox() 
                { 
                    Left = margin, 
                    Top = textLabel.Bottom + 6, 
                    Width = contentWidth,
                    AutoSize = false,
                    Height = 30,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right 
                };
                
                if (isPassword)
                {
                    inputBox.UseSystemPasswordChar = true;
                }

                int bottomPanelHeight = 52;
                int contentBottom = inputBox.Bottom + topMargin;
                int clientHeight = contentBottom + bottomPanelHeight;

                promptForm.ClientSize = new Size(clientWidth, clientHeight);
                promptForm.MinimumSize = promptForm.Size;
                promptForm.MaximumSize = promptForm.Size;

                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                promptForm.Controls.Add(bottomPanel);

                Button confirmation = UiTheme.CreateButton("OK", "Подтвердить ввод", toolTip, 85, dialogResult: DialogResult.OK);
                Button cancel = UiTheme.CreateButton("Отмена", "Отменить ввод", toolTip, 85, dialogResult: DialogResult.Cancel);

                confirmation.Left = promptForm.ClientSize.Width - margin - confirmation.Width;
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
