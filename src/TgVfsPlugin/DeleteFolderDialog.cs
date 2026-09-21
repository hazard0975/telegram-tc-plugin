using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TgVfsPlugin;

public static class DeleteFolderDialog
{
    public static string? Show(List<string> folderNames)
    {
        if (folderNames == null || folderNames.Count == 0)
        {
            MessageBox.Show("Нет подключенных виртуальных папок для удаления.", "Удаление папки", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        string? selectedFolder = null;

        FormExtensions.RunInSta(() =>
        {
            try
            {
                Logger.Info("UI", "Initializing DeleteFolderDialog...");
                Win32Api.EnsureVisualStyles();
                using ToolTip toolTip = UiTheme.CreateToolTip();

                using Form form = new Form()
                {
                    Width = 490,
                    Height = 205,
                    MinimumSize = new Size(490, 205),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Удалить виртуальную папку",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = UiTheme.DefaultFont
                };

                Label label = new Label()
                {
                    Text = "Выберите виртуальную папку для удаления:",
                    Left = 20,
                    Top = 20,
                    Width = 430,
                    Height = 20
                };

                ComboBox combo = new ComboBox()
                {
                    Left = 20,
                    Top = 45,
                    Width = 430,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };

                foreach (var folder in folderNames)
                {
                    combo.Items.Add(folder);
                }
                combo.SelectedIndex = 0;

                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                Button deleteBtn = UiTheme.CreateButton("Удалить", "Удалить выбранную виртуальную папку", toolTip, 85, dialogResult: DialogResult.OK);
                Button cancelBtn = UiTheme.CreateButton("Отмена", "Отменить удаление", toolTip, 85, dialogResult: DialogResult.Cancel);

                deleteBtn.Left = form.ClientSize.Width - 20 - deleteBtn.Width;
                deleteBtn.Top = 11;
                deleteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                cancelBtn.Left = deleteBtn.Left - 10 - cancelBtn.Width;
                cancelBtn.Top = 11;
                cancelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                bottomPanel.Controls.Add(deleteBtn);
                bottomPanel.Controls.Add(cancelBtn);

                form.Controls.Add(label);
                form.Controls.Add(combo);

                form.AcceptButton = deleteBtn;
                form.CancelButton = cancelBtn;

                form.Shown += (s, e) =>
                {
                    combo.Focus();
                };

                if (form.ShowModalTc() == DialogResult.OK && combo.SelectedItem != null)
                {
                    selectedFolder = combo.SelectedItem.ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "DeleteFolderDialog Error", ex);
            }
        });

        return selectedFolder;
    }
}
