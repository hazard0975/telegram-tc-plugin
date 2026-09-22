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

                int labelHeight = 36;
                int inputHeight = 30;
                int bottomPanelHeight = 52;
                int contentBottom = topMargin + labelHeight + 6 + inputHeight + topMargin;
                int clientHeight = contentBottom + bottomPanelHeight;

                using Form promptForm = UiTheme.CreateDialogForm(title, clientWidth, clientHeight);

                Label textLabel = UiTheme.CreateLabel(prompt, labelHeight);
                textLabel.Left = margin;
                textLabel.Top = topMargin;
                textLabel.Width = contentWidth;
                textLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

                TextBox inputBox = UiTheme.CreateTextBox(height: inputHeight);
                inputBox.Left = margin;
                inputBox.Top = textLabel.Bottom + 6;
                inputBox.Width = contentWidth;
                inputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                
                if (isPassword)
                {
                    inputBox.UseSystemPasswordChar = true;
                }

                Panel bottomPanel = UiTheme.CreateBottomPanel(bottomPanelHeight);
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
