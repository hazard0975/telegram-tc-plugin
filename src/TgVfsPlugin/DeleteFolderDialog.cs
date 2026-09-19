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

                using Form form = new Form()
                {
                    Width = 480,
                    Height = 185,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Удалить виртуальную папку",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    TopMost = false,
                    Font = new Font("Segoe UI", 9)
                };

                Label label = new Label()
                {
                    Text = "Выберите виртуальную папку для удаления:",
                    Left = 20,
                    Top = 20,
                    Width = 420,
                    Height = 20
                };

                ComboBox combo = new ComboBox()
                {
                    Left = 20,
                    Top = 45,
                    Width = 420,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };

                foreach (var folder in folderNames)
                {
                    combo.Items.Add(folder);
                }
                combo.SelectedIndex = 0;

                Button deleteBtn = new Button()
                {
                    Text = "Удалить",
                    Left = 230,
                    Top = 95,
                    Width = 100,
                    Height = 30,
                    DialogResult = DialogResult.OK
                };

                Button cancelBtn = new Button()
                {
                    Text = "Отмена",
                    Left = 340,
                    Top = 95,
                    Width = 100,
                    Height = 30,
                    DialogResult = DialogResult.Cancel
                };

                form.Controls.Add(label);
                form.Controls.Add(combo);
                form.Controls.Add(deleteBtn);
                form.Controls.Add(cancelBtn);

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
