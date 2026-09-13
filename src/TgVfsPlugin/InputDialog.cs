using System;
using System.Drawing;
using System.Windows.Forms;

namespace TgVfsPlugin;

public static class InputDialog
{
    public static string? Show(string prompt, string title, bool isPassword = false)
    {
        Form promptForm = new Form()
        {
            Width = 400,
            Height = 180,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false
        };

        Label textLabel = new Label() { Left = 20, Top = 20, Width = 340, Text = prompt };
        TextBox inputBox = new TextBox() { Left = 20, Top = 50, Width = 340 };
        
        if (isPassword)
        {
            inputBox.UseSystemPasswordChar = true;
        }

        Button confirmation = new Button() { Text = "OK", Left = 260, Width = 100, Top = 90, DialogResult = DialogResult.OK };
        Button cancel = new Button() { Text = "Cancel", Left = 150, Width = 100, Top = 90, DialogResult = DialogResult.Cancel };

        confirmation.Click += (sender, e) => { promptForm.Close(); };
        cancel.Click += (sender, e) => { promptForm.Close(); };

        promptForm.Controls.Add(textLabel);
        promptForm.Controls.Add(inputBox);
        promptForm.Controls.Add(confirmation);
        promptForm.Controls.Add(cancel);
        promptForm.AcceptButton = confirmation;
        promptForm.CancelButton = cancel;

        // Ensure the form shows on top of Total Commander
        promptForm.TopMost = true;

        string? result = null;
        if (promptForm.ShowDialog() == DialogResult.OK)
        {
            result = inputBox.Text;
        }

        return result;
    }
}
