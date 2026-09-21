using System;
using System.Drawing;
using System.Windows.Forms;

namespace TgVfsPlugin;

/// <summary>
/// Единый класс стилей и фабрики элементов интерфейса плагина.
/// Обеспечивает нативный вид Windows/Total Commander, одинаковые шрифты,
/// автоматический расчет ширины под текст и всплывающие подсказки (ToolTip).
/// </summary>
public static class UiTheme
{
    public static readonly Font DefaultFont = new Font("Segoe UI", 9f, FontStyle.Regular);
    public static readonly Font BoldFont = new Font("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font HeaderTitleFont = new Font("Segoe UI", 11f, FontStyle.Bold);
    public static readonly Font HeaderSubFont = new Font("Segoe UI", 9f, FontStyle.Regular);

    public static readonly Color HeaderBgColor = Color.FromArgb(245, 247, 250);
    public static readonly Color BottomPanelBgColor = Color.FromArgb(245, 247, 250);
    public static readonly Color BorderDividerColor = Color.FromArgb(220, 224, 230);
    public static readonly Color LabelForeColor = Color.FromArgb(70, 70, 70);

    public const int DefaultButtonHeight = 30;
    public const int DefaultBottomPanelHeight = 52;

    /// <summary>
    /// Вычисляет оптимальную ширину кнопки под длину текста со стандартными отступами.
    /// </summary>
    public static int CalcButtonWidth(string text, Font? font = null, int minWidth = 85, int padding = 28)
    {
        font ??= DefaultFont;
        int textWidth = TextRenderer.MeasureText(text, font).Width;
        return Math.Max(minWidth, textWidth + padding);
    }

    /// <summary>
    /// Создает стандартную нативную кнопку Windows Forms с авто-шириной и подсказкой.
    /// </summary>
    public static Button CreateButton(
        string text,
        string? tooltip = null,
        ToolTip? toolTipProvider = null,
        int minWidth = 85,
        int height = DefaultButtonHeight,
        Font? font = null,
        DialogResult dialogResult = DialogResult.None)
    {
        font ??= DefaultFont;
        int width = CalcButtonWidth(text, font, minWidth);

        Button btn = new Button()
        {
            Text = text,
            Width = width,
            Height = height,
            Font = font,
            TextAlign = ContentAlignment.MiddleCenter,
            ImageAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = true,
            DialogResult = dialogResult
        };

        if (!string.IsNullOrWhiteSpace(tooltip) && toolTipProvider != null)
        {
            toolTipProvider.SetToolTip(btn, tooltip);
        }

        return btn;
    }

    /// <summary>
    /// Создает стандартизированный ToolTip с анимацией и нативными задержками.
    /// </summary>
    public static ToolTip CreateToolTip()
    {
        return new ToolTip()
        {
            AutoPopDelay = 6000,
            InitialDelay = 400,
            ReshowDelay = 200,
            ShowAlways = true
        };
    }

    /// <summary>
    /// Создает нижнюю панель для кнопок диалога с тонким верхним разделителем.
    /// </summary>
    public static Panel CreateBottomPanel(int height = DefaultBottomPanelHeight)
    {
        Panel panel = new Panel()
        {
            Dock = DockStyle.Bottom,
            Height = height,
            BackColor = BottomPanelBgColor
        };

        panel.Paint += (s, e) =>
        {
            using Pen pen = new Pen(BorderDividerColor, 1);
            e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0);
        };

        return panel;
    }
}
