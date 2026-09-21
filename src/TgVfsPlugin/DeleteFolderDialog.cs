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

                int margin = 20;
                int topMargin = 16;
                int clientWidth = 474;
                int contentWidth = clientWidth - margin * 2; // 434px

                int clientHeight = 152; // 16 (top) + 22 (label) + 4 + 30 (combo) + 16 (gap) + 52 (bottom) = 140 -> 152px

                using Form form = UiTheme.CreateDialogForm("Удалить виртуальную папку", clientWidth, clientHeight);

                Label label = new Label()
                {
                    Text = "Выберите виртуальную папку для удаления:",
                    Left = margin,
                    Top = topMargin,
                    Width = contentWidth,
                    Height = 22,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                ComboBox combo = UiTheme.CreateComboBox();
                combo.Left = margin;
                combo.Top = label.Bottom + 4;
                combo.Width = contentWidth;
                combo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

                foreach (var folder in folderNames)
                {
                    combo.Items.Add(folder);
                }
                combo.SelectedIndex = 0;

                form.MinimumSize = form.Size;
                form.MaximumSize = form.Size;

                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                Button deleteBtn = UiTheme.CreateButton("Удалить", "Удалить выбранную виртуальную папку", toolTip, 85, dialogResult: DialogResult.OK);
                Button cancelBtn = UiTheme.CreateButton("Отмена", "Отменить удаление", toolTip, 85, dialogResult: DialogResult.Cancel);

                deleteBtn.Left = form.ClientSize.Width - margin - deleteBtn.Width;
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
