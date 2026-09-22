using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TgVfsPlugin;

public enum SyncItemStatus
{
    Identical,           // Файлы совпадают (размер и время изменения)
    LocalNewer,          // На ПК файл новее -> обновить в TG
    RemoteNewer,         // В Telegram файл новее -> обновить на ПК
    LocalOnly,           // Новый файл на диске ПК (отсутствует в VFS) -> загрузить в TG
    SizeMismatch,        // Даты совпадают, но размеры отличаются (конфликт / несовпадение)
    SourceNotFound,      // Локальный файл-источник на диске отсутствует
    NoSourceConfigured   // У элемента VFS в Контейнере отсутствует привязанный локальный путь
}

public class SmartSyncItem
{
    public VfsDatabase.FileRecord FileRecord { get; set; } = null!;
    public SyncItemStatus Status { get; set; }
    public string StatusText { get; set; } = "";
    public string DirectionSymbol { get; set; } = "";
    public string DirectionText { get; set; } = "";
    public long LocalSize { get; set; }
    public DateTime LocalWriteTime { get; set; }
    public string ToolTipDetails { get; set; } = "";
    public bool IsChecked { get; set; }
}

public class SmartSyncColumnComparer : IComparer
{
    private readonly int _column;
    private readonly SortOrder _order;

    public SmartSyncColumnComparer(int column, SortOrder order)
    {
        _column = column;
        _order = order;
    }

    public int Compare(object? x, object? y)
    {
        if (x is not ListViewItem itemX || y is not ListViewItem itemY) return 0;

        string textX = _column < itemX.SubItems.Count ? itemX.SubItems[_column].Text : "";
        string textY = _column < itemY.SubItems.Count ? itemY.SubItems[_column].Text : "";

        int result;

        // Колонки 1 и 5 — Размеры файлов (числа)
        if (_column == 1 || _column == 5)
        {
            long numX = ParseSize(textX);
            long numY = ParseSize(textY);
            result = numX.CompareTo(numY);
        }
        // Колонки 2 и 4 — Даты ("dd.MM.yy HH:mm:ss")
        else if (_column == 2 || _column == 4)
        {
            DateTime dtX = DateTime.TryParse(textX, out DateTime dx) ? dx : DateTime.MinValue;
            DateTime dtY = DateTime.TryParse(textY, out DateTime dy) ? dy : DateTime.MinValue;
            result = dtX.CompareTo(dtY);
        }
        else
        {
            result = string.Compare(textX, textY, StringComparison.OrdinalIgnoreCase);
        }

        return _order == SortOrder.Descending ? -result : result;
    }

    private static long ParseSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "-") return -1;
        string clean = text.Replace(" ", "").Replace(",", "").Replace(".", "").Replace("\u00A0", "");
        return long.TryParse(clean, out long val) ? val : -1;
    }
}

public static class SmartSyncDialog
{
    public static void Show(string channelName, long channelId, string mountId, string folderPath, VfsDatabase db)
    {
        FormExtensions.RunInSta(() =>
        {
            try
            {
                Win32Api.EnsureVisualStyles();
                using ToolTip toolTip = UiTheme.CreateToolTip();

                var mountInfo = db.GetMountById(mountId);
                bool isMirror = mountInfo != null && mountInfo.Mode == 0 && !string.IsNullOrEmpty(mountInfo.LocalPath);
                string modeTitle = isMirror ? "Зеркало" : "Контейнер";

                int initWidth = 1080;
                int initHeight = 660;
                bool initMaximized = false;

                if (int.TryParse(SettingsManager.GetSetting("smartsync_width"), out int savedW) && savedW >= 840) initWidth = savedW;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_height"), out int savedH) && savedH >= 520) initHeight = savedH;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_maximized"), out int savedMax) && savedMax == 1) initMaximized = true;

                bool initHideIdentical = false;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_hide_identical"), out int savedHide) && savedHide == 1) initHideIdentical = true;

                int margin = 20;

                using Form form = new Form()
                {
                    ClientSize = new Size(initWidth, initHeight),
                    MinimumSize = new Size(880, 560),
                    FormBorderStyle = FormBorderStyle.Sizable,
                    Text = $"Умная синхронизация (Smart Sync — {modeTitle}) — \\{channelName}\\{(string.IsNullOrEmpty(folderPath) ? "" : folderPath)}",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = true,
                    MaximizeBox = true,
                    TopMost = false,
                    Font = UiTheme.DefaultFont,
                    AutoScaleMode = AutoScaleMode.None
                };

                if (initMaximized)
                {
                    form.WindowState = FormWindowState.Maximized;
                }

                // Шапка
                Panel headerPanel = new Panel()
                {
                    Dock = DockStyle.Top,
                    Height = 65,
                    BackColor = UiTheme.HeaderBgColor
                };

                Label titleLabel = new Label()
                {
                    Left = margin,
                    Top = 10,
                    Width = 800,
                    Height = 22,
                    Text = isMirror
                        ? $"Синхронизация папки-зеркала: {mountInfo!.LocalPath}"
                        : "Сравнение элементов контейнера с оригиналами на ПК",
                    Font = UiTheme.HeaderTitleFont,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                Label subLabel = new Label()
                {
                    Left = margin,
                    Top = 35,
                    Width = 800,
                    Height = 22,
                    ForeColor = UiTheme.LabelForeColor,
                    Text = isMirror
                        ? "Двустороннее отслеживание изменений между структурой VFS и локальным диском"
                        : "Проверка актуальности элементов виртуальной подборки и оригиналов на дисках ПК",
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(subLabel);
                form.Controls.Add(headerPanel);

                // Нижняя панель для кнопок управления
                Panel bottomPanel = UiTheme.CreateBottomPanel(52);
                form.Controls.Add(bottomPanel);

                // Информационная сводка вверху
                Label summaryLabel = new Label()
                {
                    Left = 20,
                    Top = 75,
                    Width = form.ClientSize.Width - 280,
                    Height = 22,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Text = "Сканирование и подготовка данных..."
                };
                form.Controls.Add(summaryLabel);

                // Чекбокс «Скрыть идентичные файлы»
                CheckBox hideIdenticalCb = UiTheme.CreateCheckBox("Скрыть идентичные", 220);
                hideIdenticalCb.Left = form.ClientSize.Width - margin - hideIdenticalCb.Width;
                hideIdenticalCb.Top = 73;
                hideIdenticalCb.Height = 24;
                hideIdenticalCb.Checked = initHideIdentical;
                hideIdenticalCb.Enabled = false;
                hideIdenticalCb.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                toolTip.SetToolTip(hideIdenticalCb, "Скрыть из списка все файлы, содержимое и даты которых полностью совпадают");
                form.Controls.Add(hideIdenticalCb);

                // Список файлов ListView
                ListView listView = new ListView()
                {
                    Left = 20,
                    Top = 105,
                    Width = form.ClientSize.Width - 40,
                    Height = form.ClientSize.Height - bottomPanel.Height - 115,
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    View = View.Details,
                    CheckBoxes = true,
                    FullRowSelect = true,
                    GridLines = true,
                    ShowItemToolTips = false
                };

                typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .SetValue(listView, true, null);

                int colVfsWidth = 260;
                int colTgSizeWidth = 85;
                int colTgDateWidth = 145;
                int colDirWidth = 105;
                int colPcDateWidth = 145;
                int colPcSizeWidth = 85;
                int colSrcWidth = 320;

                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_vfs"), out int cv) && cv >= 50) colVfsWidth = cv;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_tg_size"), out int ctg_s) && ctg_s >= 40) colTgSizeWidth = ctg_s;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_tg_date"), out int ctg_d) && ctg_d >= 60) colTgDateWidth = ctg_d;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_direction"), out int cd) && cd >= 50) colDirWidth = cd;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_pc_date"), out int cpc_d) && cpc_d >= 60) colPcDateWidth = cpc_d;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_pc_size"), out int cpc_s) && cpc_s >= 40) colPcSizeWidth = cpc_s;
                if (int.TryParse(SettingsManager.GetSetting("smartsync_col_source"), out int csrc) && csrc >= 50) colSrcWidth = csrc;

                listView.Columns.Add("Путь в VFS", colVfsWidth, HorizontalAlignment.Left);
                listView.Columns.Add("Размер (TG)", colTgSizeWidth, HorizontalAlignment.Right);
                listView.Columns.Add("Дата (TG)", colTgDateWidth, HorizontalAlignment.Left);
                listView.Columns.Add("TG <=> ПК", colDirWidth, HorizontalAlignment.Center);
                listView.Columns.Add("Дата (ПК)", colPcDateWidth, HorizontalAlignment.Left);
                listView.Columns.Add("Размер (ПК)", colPcSizeWidth, HorizontalAlignment.Right);
                listView.Columns.Add("Оригинал на ПК", colSrcWidth, HorizontalAlignment.Left);

                form.Controls.Add(listView);

                // Оверлей загрузки (Лоадер при открытии)
                Panel loadingPanel = new Panel()
                {
                    Left = listView.Left,
                    Top = listView.Top,
                    Width = listView.Width,
                    Height = listView.Height,
                    BackColor = Color.White,
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
                };

                Label loadingTitle = new Label()
                {
                    Text = "Сканирование файлов VFS и диска ПК...",
                    Font = new Font("Segoe UI", 11, FontStyle.Bold),
                    ForeColor = Color.FromArgb(0, 102, 204),
                    AutoSize = true
                };

                ProgressBar loadingSpinner = new ProgressBar()
                {
                    Style = ProgressBarStyle.Marquee,
                    MarqueeAnimationSpeed = 30,
                    Width = 320,
                    Height = 20
                };

                Label loadingDetail = new Label()
                {
                    Text = "Анализ файлов и вычисление статусов синхронизации...",
                    Font = UiTheme.DefaultFont,
                    ForeColor = Color.DimGray,
                    AutoSize = true
                };

                loadingPanel.Controls.Add(loadingTitle);
                loadingPanel.Controls.Add(loadingSpinner);
                loadingPanel.Controls.Add(loadingDetail);

                void CenterLoadingPanelControls()
                {
                    int centerX = loadingPanel.Width / 2;
                    int centerY = loadingPanel.Height / 2;

                    loadingTitle.Left = centerX - (loadingTitle.Width / 2);
                    loadingTitle.Top = centerY - 45;

                    loadingSpinner.Left = centerX - (loadingSpinner.Width / 2);
                    loadingSpinner.Top = centerY - 10;

                    loadingDetail.Left = centerX - (loadingDetail.Width / 2);
                    loadingDetail.Top = centerY + 20;
                }

                loadingPanel.Resize += (s, e) => CenterLoadingPanelControls();
                form.Controls.Add(loadingPanel);
                loadingPanel.BringToFront();
                CenterLoadingPanelControls();

                // -------------------------------------------------------------
                // Панель подробной информации о передаче файлов (при синхронизации)
                // -------------------------------------------------------------
                Panel syncProgressPanel = new Panel()
                {
                    Left = 20,
                    Top = form.ClientSize.Height - bottomPanel.Height - 200,
                    Width = form.ClientSize.Width - 40,
                    Height = 190,
                    Visible = false,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
                };

                Label lblOperationTitle = new Label()
                {
                    Left = 0,
                    Top = 0,
                    Width = syncProgressPanel.Width - 240,
                    Height = 22,
                    Font = new Font("Segoe UI", 10, FontStyle.Bold),
                    Text = "Подготовка к передаче..."
                };

                ProgressBar pbCurrentFile = new ProgressBar()
                {
                    Left = 0,
                    Top = 24,
                    Width = syncProgressPanel.Width,
                    Height = 16,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                Panel cardPanel = new Panel()
                {
                    Left = 0,
                    Top = 44,
                    Width = syncProgressPanel.Width,
                    Height = 142,
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Color.FromArgb(250, 250, 252),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                int lblY = 6;
                int lblStep = 18;

                Label CreateCardField(string prefix, int topY)
                {
                    Label titleLbl = new Label()
                    {
                        Left = 10,
                        Top = topY,
                        Width = 125,
                        Height = 20,
                        Text = prefix,
                        Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                        ForeColor = Color.FromArgb(100, 100, 100)
                    };

                    Label valLbl = new Label()
                    {
                        Left = 135,
                        Top = topY,
                        Width = cardPanel.Width - 260,
                        Height = 20,
                        Text = "-",
                        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                        ForeColor = Color.FromArgb(30, 30, 30),
                        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                    };

                    cardPanel.Controls.Add(titleLbl);
                    cardPanel.Controls.Add(valLbl);
                    return valLbl;
                }

                Label lblCurFileName = CreateCardField("Текущий файл:", lblY);
                Label lblCurFileBytes = CreateCardField("Загружено:", lblY + lblStep);
                Label lblFilesCount = CreateCardField("Файлы:", lblY + (lblStep * 2));
                Label lblTotalBytes = CreateCardField("Объем данных:", lblY + (lblStep * 3));
                Label lblSpeed = CreateCardField("Скорость:", lblY + (lblStep * 4));
                Label lblTime = CreateCardField("Прошло времени:", lblY + (lblStep * 5));
                Label lblDirection = CreateCardField("Направление:", lblY + (lblStep * 6));

                Button pauseBtn = UiTheme.CreateButton("⏸ Пауза", "Приостановить или возобновить передачу данных", toolTip, 110);
                pauseBtn.Left = cardPanel.Width - 125;
                pauseBtn.Top = 15;
                pauseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                cardPanel.Controls.Add(pauseBtn);

                syncProgressPanel.Controls.Add(lblOperationTitle);
                syncProgressPanel.Controls.Add(pbCurrentFile);
                syncProgressPanel.Controls.Add(cardPanel);
                form.Controls.Add(syncProgressPanel);

                void UpdateSyncLayout()
                {
                    if (form.IsDisposed) return;

                    syncProgressPanel.Left = 20;
                    syncProgressPanel.Width = form.ClientSize.Width - 40;
                    syncProgressPanel.Top = bottomPanel.Top - syncProgressPanel.Height - 10;

                    cardPanel.Width = syncProgressPanel.Width;
                    pbCurrentFile.Width = syncProgressPanel.Width;
                    pauseBtn.Left = cardPanel.Width - pauseBtn.Width - 15;

                    if (syncProgressPanel.Visible)
                    {
                        listView.Height = syncProgressPanel.Top - listView.Top - 8;
                    }
                    else
                    {
                        listView.Height = bottomPanel.Top - listView.Top - 10;
                    }
                }

                form.Resize += (s, e) => UpdateSyncLayout();

                // Кнопки управления в нижней панели
                Button selectUpdatesBtn = UiTheme.CreateButton("Выбрать разные", "Отметить галочками все файлы, требующие синхронизации", toolTip, 130);
                selectUpdatesBtn.Left = margin;
                selectUpdatesBtn.Top = 11;
                selectUpdatesBtn.Enabled = false;
                selectUpdatesBtn.Anchor = AnchorStyles.Top | AnchorStyles.Left;

                Button clearSelectionBtn = UiTheme.CreateButton("Снять выбор", "Снять отметки выбора со всех файлов в списке", toolTip, 100);
                clearSelectionBtn.Left = selectUpdatesBtn.Right + 10;
                clearSelectionBtn.Top = 11;
                clearSelectionBtn.Enabled = false;
                clearSelectionBtn.Anchor = AnchorStyles.Top | AnchorStyles.Left;

                Button closeBtn = UiTheme.CreateButton("Закрыть", "Закрыть окно синхронизации", toolTip, 85, dialogResult: DialogResult.Cancel);
                closeBtn.Left = bottomPanel.Width - margin - closeBtn.Width;
                closeBtn.Top = 11;
                closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                Button syncBtn = UiTheme.CreateButton("Синхронизировать", "Запустить обновление всех отмеченных файлов", toolTip, 130);
                syncBtn.Left = closeBtn.Left - 10 - syncBtn.Width;
                syncBtn.Top = 11;
                syncBtn.Enabled = false;
                syncBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                bottomPanel.Controls.Add(selectUpdatesBtn);
                bottomPanel.Controls.Add(clearSelectionBtn);
                bottomPanel.Controls.Add(syncBtn);
                bottomPanel.Controls.Add(closeBtn);
                bottomPanel.BringToFront();

                List<SmartSyncItem> items = new();
                int localNewerCount = 0;
                int remoteNewerCount = 0;
                int localOnlyCount = 0;
                int missingCount = 0;
                int identicalCount = 0;
                int mismatchCount = 0;
                int noSourceCount = 0;

                int sortColumn = -1;
                SortOrder sortOrder = SortOrder.None;

                void UpdateSummaryLabel()
                {
                    int lNewer = 0, rNewer = 0, lOnly = 0, missing = 0, ident = 0, mismatch = 0, noSrc = 0;
                    foreach (var it in items)
                    {
                        switch (it.Status)
                        {
                            case SyncItemStatus.LocalNewer: lNewer++; break;
                            case SyncItemStatus.RemoteNewer: rNewer++; break;
                            case SyncItemStatus.LocalOnly: lOnly++; break;
                            case SyncItemStatus.Identical: ident++; break;
                            case SyncItemStatus.SizeMismatch: mismatch++; break;
                            case SyncItemStatus.SourceNotFound: missing++; break;
                            case SyncItemStatus.NoSourceConfigured: noSrc++; break;
                        }
                    }

                    localNewerCount = lNewer;
                    remoteNewerCount = rNewer;
                    localOnlyCount = lOnly;
                    missingCount = missing;
                    identicalCount = ident;
                    mismatchCount = mismatch;
                    noSourceCount = noSrc;

                    string sumText = $"Режим: {(isMirror ? "Зеркало" : "Контейнер")}  |  Всего: {items.Count}  |  К обновлению: {localNewerCount + remoteNewerCount + localOnlyCount}  |  Идентичны: {identicalCount}";
                    if (localOnlyCount > 0) sumText += $"  |  Новых на ПК: {localOnlyCount}";
                    if (mismatchCount > 0) sumText += $"  |  Разный размер: {mismatchCount}";
                    if (missingCount > 0) sumText += $"  |  Не найдены: {missingCount}";
                    if (noSourceCount > 0) sumText += $"  |  Без привязки: {noSourceCount}";

                    summaryLabel.Text = sumText;
                }

                void PopulateListView()
                {
                    UpdateSummaryLabel();

                    listView.BeginUpdate();
                    listView.Items.Clear();

                    bool hideIdentical = hideIdenticalCb.Checked;
                    int rowIndex = 0;

                    foreach (var item in items)
                    {
                        if (hideIdentical && item.Status == SyncItemStatus.Identical)
                        {
                            continue;
                        }

                        string vfsDisplayPath = string.IsNullOrEmpty(item.FileRecord.Parent)
                            ? item.FileRecord.Name
                            : $"{item.FileRecord.Parent}\\{item.FileRecord.Name}";

                        if (!string.IsNullOrEmpty(item.FileRecord.SourcePath))
                        {
                            string origName = Path.GetFileName(item.FileRecord.SourcePath);
                            if (!string.Equals(origName, item.FileRecord.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                vfsDisplayPath += " 🏷️";
                                if (!string.IsNullOrEmpty(item.ToolTipDetails) && !item.ToolTipDetails.Contains("🏷️"))
                                {
                                    item.ToolTipDetails += $"\n• 🏷️ Переименован в VFS (на ПК: {origName})";
                                }
                            }
                        }

                        string itemText = vfsDisplayPath;
                        string tgSizeText = item.Status == SyncItemStatus.LocalOnly ? "-" : item.FileRecord.Size.ToString("#,##0");
                        string tgDateText = item.Status == SyncItemStatus.LocalOnly ? "-" : item.FileRecord.MTime.ToLocalTime().ToString("dd.MM.yy HH:mm:ss");
                        string dirSymbol = item.DirectionSymbol;
                        string pcDateText = (item.Status == SyncItemStatus.SourceNotFound || item.Status == SyncItemStatus.NoSourceConfigured)
                            ? "-"
                            : item.LocalWriteTime.ToString("dd.MM.yy HH:mm:ss");
                        string pcSizeText = (item.Status == SyncItemStatus.SourceNotFound || item.Status == SyncItemStatus.NoSourceConfigured)
                            ? "-"
                            : item.LocalSize.ToString("#,##0");
                        string pcPathText = item.FileRecord.SourcePath ?? "";

                        var lvi = new ListViewItem(itemText)
                        {
                            Checked = item.IsChecked,
                            Tag = item
                        };

                        lvi.SubItems.Add(tgSizeText);
                        lvi.SubItems.Add(tgDateText);
                        lvi.SubItems.Add(dirSymbol);
                        lvi.SubItems.Add(pcDateText);
                        lvi.SubItems.Add(pcSizeText);
                        lvi.SubItems.Add(pcPathText);

                        lvi.UseItemStyleForSubItems = false;
                        Color rowFg;
                        Color rowBg;
                        Font rowFont;

                        if (item.Status == SyncItemStatus.LocalOnly || item.Status == SyncItemStatus.LocalNewer)
                        {
                            rowFg = Color.FromArgb(0, 110, 0);
                            rowBg = Color.FromArgb(235, 248, 235);
                            rowFont = new Font(listView.Font, FontStyle.Bold);
                        }
                        else if (item.Status == SyncItemStatus.RemoteNewer)
                        {
                            rowFg = Color.FromArgb(0, 70, 180);
                            rowBg = Color.FromArgb(235, 244, 255);
                            rowFont = new Font(listView.Font, FontStyle.Bold);
                        }
                        else if (item.Status == SyncItemStatus.SizeMismatch)
                        {
                            rowFg = Color.FromArgb(180, 100, 0);
                            rowBg = Color.FromArgb(255, 247, 230);
                            rowFont = new Font(listView.Font, FontStyle.Bold);
                        }
                        else if (item.Status == SyncItemStatus.SourceNotFound || item.Status == SyncItemStatus.NoSourceConfigured)
                        {
                            rowFg = Color.FromArgb(170, 0, 0);
                            rowBg = Color.FromArgb(255, 235, 235);
                            rowFont = new Font(listView.Font, FontStyle.Bold);
                        }
                        else
                        {
                            rowFg = Color.FromArgb(90, 90, 90);
                            rowBg = (rowIndex % 2 == 0) ? Color.White : Color.FromArgb(248, 249, 250);
                            rowFont = new Font(listView.Font, FontStyle.Regular);
                        }

                        lvi.ForeColor = rowFg;
                        lvi.BackColor = rowBg;
                        lvi.Font = rowFont;

                        foreach (ListViewItem.ListViewSubItem sub in lvi.SubItems)
                        {
                            sub.ForeColor = rowFg;
                            sub.BackColor = rowBg;
                            sub.Font = rowFont;
                        }

                        lvi.SubItems[3].Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);

                        listView.Items.Add(lvi);
                        rowIndex++;
                    }

                    if (sortColumn >= 0 && sortOrder != SortOrder.None)
                    {
                        listView.ListViewItemSorter = new SmartSyncColumnComparer(sortColumn, sortOrder);
                        listView.Sort();
                    }

                    listView.EndUpdate();
                }

                // Отслеживание изменений галочек
                listView.ItemChecked += (s, e) =>
                {
                    if (e.Item.Tag is SmartSyncItem item)
                    {
                        item.IsChecked = e.Item.Checked;
                    }
                };

                hideIdenticalCb.CheckedChanged += (s, e) => PopulateListView();

                listView.ColumnClick += (s, e) =>
                {
                    if (e.Column == sortColumn)
                    {
                        sortOrder = (sortOrder == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;
                    }
                    else
                    {
                        sortColumn = e.Column;
                        sortOrder = SortOrder.Ascending;
                    }

                    listView.ListViewItemSorter = new SmartSyncColumnComparer(sortColumn, sortOrder);
                    listView.Sort();
                };

                selectUpdatesBtn.Click += (s, e) =>
                {
                    foreach (var it in items)
                    {
                        it.IsChecked = (it.Status == SyncItemStatus.LocalNewer ||
                                        it.Status == SyncItemStatus.RemoteNewer ||
                                        it.Status == SyncItemStatus.LocalOnly);
                    }
                    PopulateListView();
                };

                clearSelectionBtn.Click += (s, e) =>
                {
                    foreach (var it in items) it.IsChecked = false;
                    PopulateListView();
                };

                // Фоновое сканирование при открытии формы
                form.Shown += async (s, e) =>
                {
                    await Task.Run(() =>
                    {
                        var scannedItems = new List<SmartSyncItem>();
                        int lNewer = 0, rNewer = 0, lOnly = 0, missing = 0, ident = 0, mismatch = 0, noSrc = 0;

                        if (isMirror)
                        {
                            string mirrorRoot = mountInfo!.LocalPath!;
                            string targetSubFolder = string.IsNullOrEmpty(folderPath) ? "" : folderPath.Trim('\\', '/').Replace('/', '\\');
                            string targetLocalDir = string.IsNullOrEmpty(targetSubFolder) ? mirrorRoot : Path.Combine(mirrorRoot, targetSubFolder);

                            var vfsFiles = db.GetAllFilesRecursive(mountId, folderPath);
                            var vfsMap = new Dictionary<string, VfsDatabase.FileRecord>(StringComparer.OrdinalIgnoreCase);

                            foreach (var file in vfsFiles)
                            {
                                string relPath = string.IsNullOrEmpty(file.Parent) ? file.Name : Path.Combine(file.Parent, file.Name);
                                vfsMap[relPath] = file;

                                string expectedPath = Path.Combine(mirrorRoot, relPath);
                                file.SourcePath = expectedPath;
                            }

                            if (Directory.Exists(targetLocalDir))
                            {
                                try
                                {
                                    var diskFiles = Directory.EnumerateFiles(targetLocalDir, "*", SearchOption.AllDirectories);
                                    int count = 0;
                                    foreach (var diskPath in diskFiles)
                                    {
                                        count++;
                                        if (count % 25 == 0 && form.IsHandleCreated && !form.IsDisposed)
                                        {
                                            form.BeginInvoke(() =>
                                            {
                                                if (!loadingPanel.IsDisposed)
                                                {
                                                    loadingDetail.Text = $"Сканирование диска ПК... Проверено локальных файлов: {count:#,##0}";
                                                    CenterLoadingPanelControls();
                                                }
                                            });
                                        }

                                        string relPath = Path.GetRelativePath(mirrorRoot, diskPath);

                                        if (!vfsMap.ContainsKey(relPath))
                                        {
                                            try
                                            {
                                                var fi = new FileInfo(diskPath);
                                                string relDir = Path.GetDirectoryName(relPath) ?? "";
                                                string fileName = Path.GetFileName(diskPath);

                                                var syntheticRecord = new VfsDatabase.FileRecord
                                                {
                                                    Uid = "",
                                                    MountId = mountId,
                                                    IsDir = false,
                                                    Name = fileName,
                                                    Parent = string.IsNullOrEmpty(relDir) ? null : relDir,
                                                    MTime = fi.LastWriteTimeUtc,
                                                    Size = fi.Length,
                                                    TgMessageId = 0,
                                                    InTrash = 0,
                                                    Ver = 1,
                                                    SourcePath = diskPath
                                                };

                                                var newItem = new SmartSyncItem
                                                {
                                                    FileRecord = syntheticRecord,
                                                    Status = SyncItemStatus.LocalOnly,
                                                    StatusText = "Новый на ПК",
                                                    DirectionSymbol = "TG <<-- ПК",
                                                    DirectionText = "ПК -> TG",
                                                    LocalSize = fi.Length,
                                                    LocalWriteTime = fi.LastWriteTime,
                                                    ToolTipDetails = $"[TG <<-- ПК] (Новый файл на локальном диске ПК)\n" +
                                                                     $"• Отсутствует в VFS и Telegram\n" +
                                                                     $"• Путь на ПК: {diskPath}\n" +
                                                                     $"• Размер: {fi.Length:#,##0} байт\n" +
                                                                     $"• Дата: {fi.LastWriteTime:dd.MM.yy HH:mm:ss}\n" +
                                                                     $"(отметьте для выгрузки с ПК в Telegram)",
                                                    IsChecked = true
                                                };

                                                scannedItems.Add(newItem);
                                                lOnly++;
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn("UI", $"SmartSync disk scan error in '{targetLocalDir}': {ex.Message}");
                                }
                            }

                            foreach (var file in vfsFiles)
                            {
                                var item = new SmartSyncItem { FileRecord = file };
                                DateTime remoteLocalTime = file.MTime.ToLocalTime();
                                string expectedPath = file.SourcePath!;

                                if (!File.Exists(expectedPath))
                                {
                                    item.Status = SyncItemStatus.RemoteNewer;
                                    item.StatusText = "Отсутствует на ПК";
                                    item.DirectionSymbol = "TG -->> ПК";
                                    item.DirectionText = "TG -> ПК";
                                    item.ToolTipDetails = $"[TG -->> ПК] (Файл отсутствует на локальном диске ПК)\n" +
                                                          $"• Telegram: {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                          $"• Ожидаемый путь: {expectedPath}\n" +
                                                          $"(отметьте для скачивания из Telegram на ПК)";
                                    item.IsChecked = true;
                                    rNewer++;
                                }
                                else
                                {
                                    try
                                    {
                                        var fi = new FileInfo(expectedPath);
                                        item.LocalSize = fi.Length;
                                        item.LocalWriteTime = fi.LastWriteTime;

                                        long localUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                                        long remoteUnix = new DateTimeOffset(file.MTime.ToUniversalTime()).ToUnixTimeSeconds();
                                        long diff = localUnix - remoteUnix;

                                        if (Math.Abs(diff) <= 2)
                                        {
                                            if (item.LocalSize == file.Size)
                                            {
                                                item.Status = SyncItemStatus.Identical;
                                                item.StatusText = "Идентичны";
                                                item.DirectionSymbol = "TG  =  ПК";
                                                item.DirectionText = "Синхронизировано";
                                                item.ToolTipDetails = $"[TG  =  ПК] (Идентичны)\n" +
                                                                      $"• Дата: {remoteLocalTime:dd.MM.yy HH:mm:ss}\n" +
                                                                      $"• Размер: {file.Size:#,##0} байт\n" +
                                                                      $"• Источник: {expectedPath}";
                                                item.IsChecked = false;
                                                ident++;
                                            }
                                            else
                                            {
                                                item.Status = SyncItemStatus.SizeMismatch;
                                                item.StatusText = "⚠️ Разный размер";
                                                item.DirectionSymbol = "TG  ≠  ПК";
                                                item.DirectionText = "Требует решения";
                                                item.ToolTipDetails = $"[TG  ≠  ПК] (Несовпадение размеров при совпадающей дате)\n" +
                                                                      $"• Диск ПК:  {item.LocalSize:#,##0} байт ({item.LocalWriteTime:dd.MM.yy HH:mm:ss})\n" +
                                                                      $"• Telegram: {file.Size:#,##0} байт ({remoteLocalTime:dd.MM.yy HH:mm:ss})\n" +
                                                                      $"• Разница размера: {Math.Abs(item.LocalSize - file.Size):#,##0} байт\n" +
                                                                      $"• Источник: {expectedPath}";
                                                item.IsChecked = false;
                                                mismatch++;
                                            }
                                        }
                                        else if (diff > 2)
                                        {
                                            item.Status = SyncItemStatus.LocalNewer;
                                            item.StatusText = "На ПК новее";
                                            item.DirectionSymbol = "TG <<-- ПК";
                                            item.DirectionText = "ПК -> TG";

                                            TimeSpan span = fi.LastWriteTimeUtc - file.MTime.ToUniversalTime();
                                            string diffStr = FormatTimeSpan(span);

                                            item.ToolTipDetails = $"[TG <<-- ПК] (На ПК новее — выгрузка в TG)\n" +
                                                                  $"• Диск ПК (новее): {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                                  $"• Telegram:        {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                                  $"• Опережение:      на {diffStr}\n" +
                                                                  $"• Источник:        {expectedPath}";
                                            item.IsChecked = true;
                                            lNewer++;
                                        }
                                        else
                                        {
                                            item.Status = SyncItemStatus.RemoteNewer;
                                            item.StatusText = "В TG новее";
                                            item.DirectionSymbol = "TG -->> ПК";
                                            item.DirectionText = "TG -> ПК";

                                            TimeSpan span = file.MTime.ToUniversalTime() - fi.LastWriteTimeUtc;
                                            string diffStr = FormatTimeSpan(span);

                                            item.ToolTipDetails = $"[TG -->> ПК] (В Telegram новее — скачивание на ПК)\n" +
                                                                  $"• Telegram (новее): {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                                  $"• Диск ПК:          {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                                  $"• Опережение:       на {diffStr}\n" +
                                                                  $"• Источник:         {expectedPath}";
                                            item.IsChecked = true;
                                            rNewer++;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        item.Status = SyncItemStatus.SourceNotFound;
                                        item.StatusText = "Ошибка доступа";
                                        item.DirectionSymbol = "TG  ❌  ПК";
                                        item.DirectionText = "Пропуск";
                                        item.ToolTipDetails = $"[TG  ❌  ПК] (Ошибка доступа к файлу на ПК)\n• Ошибка: {ex.Message}\n• Путь: {expectedPath}";
                                        item.IsChecked = false;
                                        missing++;
                                    }
                                }

                                scannedItems.Add(item);
                            }
                        }
                        else
                        {
                            var dbFiles = db.GetAllFilesRecursive(mountId, folderPath);

                            foreach (var file in dbFiles)
                            {
                                var item = new SmartSyncItem { FileRecord = file };
                                DateTime remoteLocalTime = file.MTime.ToLocalTime();

                                if (string.IsNullOrEmpty(file.SourcePath))
                                {
                                    item.Status = SyncItemStatus.NoSourceConfigured;
                                    item.StatusText = "Виртуальный";
                                    item.DirectionSymbol = "TG  ❌  ПК";
                                    item.DirectionText = "Пропуск";
                                    item.ToolTipDetails = $"[TG  ❌  ПК] (Нет источника на ПК)\nФайл создан в VFS и не привязан к локальному файлу.";
                                    item.IsChecked = false;
                                    noSrc++;
                                }
                                else if (!File.Exists(file.SourcePath))
                                {
                                    item.Status = SyncItemStatus.SourceNotFound;
                                    item.StatusText = "Не найден на ПК";
                                    item.DirectionSymbol = "TG  ❌  ПК";
                                    item.DirectionText = "Пропуск";
                                    item.ToolTipDetails = $"[TG  ❌  ПК] (Файл-источник не найден на диске ПК)\n" +
                                                          $"• Telegram: {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                          $"• Ожидаемый путь: {file.SourcePath}\n" +
                                                          $"(диск отключен или файл удален)";
                                    item.IsChecked = false;
                                    missing++;
                                }
                                else
                                {
                                    try
                                    {
                                        var fi = new FileInfo(file.SourcePath);
                                        item.LocalSize = fi.Length;
                                        item.LocalWriteTime = fi.LastWriteTime;

                                        long localUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                                        long remoteUnix = new DateTimeOffset(file.MTime.ToUniversalTime()).ToUnixTimeSeconds();
                                        long diff = localUnix - remoteUnix;

                                        if (Math.Abs(diff) <= 2)
                                        {
                                            if (item.LocalSize == file.Size)
                                            {
                                                item.Status = SyncItemStatus.Identical;
                                                item.StatusText = "Идентичны";
                                                item.DirectionSymbol = "TG  =  ПК";
                                                item.DirectionText = "Синхронизировано";
                                                item.ToolTipDetails = $"[TG  =  ПК] (Идентичны)\n" +
                                                                      $"• Дата: {remoteLocalTime:dd.MM.yy HH:mm:ss}\n" +
                                                                      $"• Размер: {file.Size:#,##0} байт\n" +
                                                                      $"• Источник: {file.SourcePath}";
                                                item.IsChecked = false;
                                                ident++;
                                            }
                                            else
                                            {
                                                item.Status = SyncItemStatus.SizeMismatch;
                                                item.StatusText = "⚠️ Разный размер";
                                                item.DirectionSymbol = "TG  ≠  ПК";
                                                item.DirectionText = "Требует решения";
                                                item.ToolTipDetails = $"[TG  ≠  ПК] (Несовпадение размеров при совпадающей дате)\n" +
                                                                      $"• Диск ПК:  {item.LocalSize:#,##0} байт ({item.LocalWriteTime:dd.MM.yy HH:mm:ss})\n" +
                                                                      $"• Telegram: {file.Size:#,##0} байт ({remoteLocalTime:dd.MM.yy HH:mm:ss})\n" +
                                                                      $"• Разница размера: {Math.Abs(item.LocalSize - file.Size):#,##0} байт\n" +
                                                                      $"• Источник: {file.SourcePath}";
                                                item.IsChecked = false;
                                                mismatch++;
                                            }
                                        }
                                        else if (diff > 2)
                                        {
                                            item.Status = SyncItemStatus.LocalNewer;
                                            item.StatusText = "На ПК новее";
                                            item.DirectionSymbol = "TG <<-- ПК";
                                            item.DirectionText = "ПК -> TG";

                                            TimeSpan span = fi.LastWriteTimeUtc - file.MTime.ToUniversalTime();
                                            string diffStr = FormatTimeSpan(span);

                                            item.ToolTipDetails = $"[TG <<-- ПК] (На ПК новее — выгрузка в TG)\n" +
                                                                  $"• Диск ПК (новее): {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                                  $"• Telegram:        {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                                  $"• Опережение:      на {diffStr}\n" +
                                                                  $"• Источник:        {file.SourcePath}";
                                            item.IsChecked = true;
                                            lNewer++;
                                        }
                                        else
                                        {
                                            item.Status = SyncItemStatus.RemoteNewer;
                                            item.StatusText = "В TG новее";
                                            item.DirectionSymbol = "TG -->> ПК";
                                            item.DirectionText = "TG -> ПК";

                                            TimeSpan span = file.MTime.ToUniversalTime() - fi.LastWriteTimeUtc;
                                            string diffStr = FormatTimeSpan(span);

                                            item.ToolTipDetails = $"[TG -->> ПК] (В Telegram новее — скачивание на ПК)\n" +
                                                                  $"• Telegram (новее): {remoteLocalTime:dd.MM.yy HH:mm:ss} ({file.Size:#,##0} байт)\n" +
                                                                  $"• Диск ПК:          {item.LocalWriteTime:dd.MM.yy HH:mm:ss} ({item.LocalSize:#,##0} байт)\n" +
                                                                  $"• Опережение:       на {diffStr}\n" +
                                                                  $"• Источник:         {file.SourcePath}";
                                            item.IsChecked = true;
                                            rNewer++;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        item.Status = SyncItemStatus.SourceNotFound;
                                        item.StatusText = "Ошибка доступа";
                                        item.DirectionSymbol = "TG  ❌  ПК";
                                        item.DirectionText = "Пропуск";
                                        item.ToolTipDetails = $"[TG  ❌  ПК] (Ошибка доступа к файлу на ПК)\n• Ошибка: {ex.Message}\n• Путь: {file.SourcePath}";
                                        item.IsChecked = false;
                                        missing++;
                                    }
                                }

                                scannedItems.Add(item);
                            }
                        }

                        if (!form.IsDisposed && form.IsHandleCreated)
                        {
                            form.BeginInvoke(() =>
                            {
                                items = scannedItems;
                                PopulateListView();

                                loadingPanel.Visible = false;
                                hideIdenticalCb.Enabled = true;
                                selectUpdatesBtn.Enabled = true;
                                clearSelectionBtn.Enabled = true;
                                syncBtn.Enabled = true;
                            });
                        }
                    });
                };

                ToolTip rowToolTip = new ToolTip()
                {
                    AutoPopDelay = 12000,
                    InitialDelay = 200,
                    ReshowDelay = 100,
                    ShowAlways = true,
                    UseFading = true,
                    UseAnimation = true
                };

                ListViewItem? lastHoveredItem = null;

                listView.MouseMove += (s, e) =>
                {
                    var hit = listView.HitTest(e.Location);
                    if (hit.Item != null)
                    {
                        if (hit.Item != lastHoveredItem)
                        {
                            lastHoveredItem = hit.Item;
                            if (hit.Item.Tag is SmartSyncItem syncItem && !string.IsNullOrEmpty(syncItem.ToolTipDetails))
                            {
                                rowToolTip.Show(syncItem.ToolTipDetails, listView, e.X + 16, e.Y + 16, 10000);
                            }
                            else
                            {
                                rowToolTip.Hide(listView);
                            }
                        }
                    }
                    else
                    {
                        if (lastHoveredItem != null)
                        {
                            lastHoveredItem = null;
                            rowToolTip.Hide(listView);
                        }
                    }
                };

                listView.ItemMouseHover += (s, e) =>
                {
                    if (e.Item != null && e.Item.Tag is SmartSyncItem syncItem && !string.IsNullOrEmpty(syncItem.ToolTipDetails))
                    {
                        Point mousePos = listView.PointToClient(Cursor.Position);
                        rowToolTip.Show(syncItem.ToolTipDetails, listView, mousePos.X + 16, mousePos.Y + 16, 10000);
                    }
                };

                listView.MouseLeave += (s, e) =>
                {
                    lastHoveredItem = null;
                    rowToolTip.Hide(listView);
                };

                listView.MouseDown += (s, e) =>
                {
                    rowToolTip.Hide(listView);
                };

                // Переменные паузы и отмены
                ManualResetEventSlim pauseGate = new ManualResetEventSlim(true);
                CancellationTokenSource? cts = null;
                bool isPaused = false;
                bool cancellationRequested = false;

                pauseBtn.Click += (s, e) =>
                {
                    if (!isPaused)
                    {
                        pauseGate.Reset();
                        isPaused = true;
                        pauseBtn.Text = "▶ Продолжить";
                        toolTip.SetToolTip(pauseBtn, "Возобновить передачу данных");
                        lblSpeed.Text = "Пауза";
                    }
                    else
                    {
                        pauseGate.Set();
                        isPaused = false;
                        pauseBtn.Text = "⏸ Пауза";
                        toolTip.SetToolTip(pauseBtn, "Приостановить передачу данных");
                    }
                };

                closeBtn.Click += (s, e) =>
                {
                    if (closeBtn.Text == "Отмена")
                    {
                        cancellationRequested = true;
                        cts?.Cancel();
                        pauseGate.Set();
                    }
                };

                syncBtn.Click += async (s, e) =>
                {
                    int totalChecked = 0;
                    long totalBytesAllFiles = 0;

                    foreach (var it in items)
                    {
                        if (it.IsChecked)
                        {
                            totalChecked++;
                            long sz = (it.Status == SyncItemStatus.LocalOnly || it.Status == SyncItemStatus.LocalNewer)
                                ? it.LocalSize
                                : it.FileRecord.Size;
                            totalBytesAllFiles += Math.Max(0, sz);
                        }
                    }

                    if (totalChecked == 0)
                    {
                        MessageBox.Show(form, "Отметьте хотя бы один файл для синхронизации.", "Smart Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    cancellationRequested = false;
                    isPaused = false;
                    pauseGate.Set();
                    pauseBtn.Text = "⏸ Пауза";

                    cts?.Dispose();
                    cts = new CancellationTokenSource();

                    syncBtn.Enabled = false;
                    selectUpdatesBtn.Enabled = false;
                    clearSelectionBtn.Enabled = false;
                    hideIdenticalCb.Enabled = false;
                    listView.Enabled = false;

                    closeBtn.Text = "Отмена";
                    toolTip.SetToolTip(closeBtn, "Прервать выполняемую синхронизацию");
                    closeBtn.DialogResult = DialogResult.None;

                    // Разворачиваем информационную панель передач и пересчитываем геометрию
                    syncProgressPanel.Visible = true;
                    UpdateSyncLayout();

                    int updated = 0;
                    int errors = 0;
                    int processed = 0;
                    long completedBytesAllFiles = 0;

                    var totalTimer = System.Diagnostics.Stopwatch.StartNew();
                    var speedTimer = System.Diagnostics.Stopwatch.StartNew();
                    long lastSampledTotalBytes = 0;
                    double currentSpeedBytesPerSec = 0;

                    await Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var it in items)
                            {
                                if (cancellationRequested || cts.Token.IsCancellationRequested)
                                {
                                    break;
                                }

                                if (!it.IsChecked) continue;

                                processed++;
                                long currentFileTotalSize = (it.Status == SyncItemStatus.LocalOnly || it.Status == SyncItemStatus.LocalNewer)
                                    ? it.LocalSize
                                    : it.FileRecord.Size;

                                bool isUpload = (it.Status == SyncItemStatus.LocalNewer || it.Status == SyncItemStatus.LocalOnly);

                                if (form.IsHandleCreated && !form.IsDisposed)
                                {
                                    form.BeginInvoke(() =>
                                    {
                                        if (form.IsDisposed) return;
                                        lblOperationTitle.Text = isUpload ? "Загрузка в Telegram..." : "Скачивание из Telegram...";
                                        pbCurrentFile.Value = 0;
                                        lblCurFileName.Text = it.FileRecord.Name;
                                        lblFilesCount.Text = $"{processed} из {totalChecked}";
                                        lblDirection.Text = isUpload ? "Диск ПК → Telegram Cloud (VFS)" : "Telegram Cloud (VFS) → Диск ПК";
                                    });
                                }

                                Func<long, long, bool> progressHandler = (transferred, total) =>
                                {
                                    if (cancellationRequested || cts.Token.IsCancellationRequested)
                                    {
                                        return true;
                                    }

                                    if (speedTimer.ElapsedMilliseconds >= 400)
                                    {
                                        double sec = speedTimer.Elapsed.TotalSeconds;
                                        long currentTotal = completedBytesAllFiles + transferred;
                                        long diff = currentTotal - lastSampledTotalBytes;
                                        currentSpeedBytesPerSec = sec > 0 ? (diff / sec) : 0;
                                        lastSampledTotalBytes = currentTotal;
                                        speedTimer.Restart();
                                    }

                                    int filePct = total > 0 ? (int)Math.Clamp((transferred * 100) / total, 0, 100) : 0;
                                    long currentTotalBytes = completedBytesAllFiles + transferred;
                                    int overallPct = totalBytesAllFiles > 0 ? (int)Math.Clamp((currentTotalBytes * 100) / totalBytesAllFiles, 0, 100) : 0;

                                    if (form.IsHandleCreated && !form.IsDisposed)
                                    {
                                        form.BeginInvoke(() =>
                                        {
                                            if (form.IsDisposed) return;

                                            pbCurrentFile.Value = filePct;
                                            lblOperationTitle.Text = $"{(isUpload ? "Загрузка в Telegram..." : "Скачивание из Telegram...")} ({filePct}%)";

                                            lblCurFileBytes.Text = $"{filePct}% ({Logger.FormatBytes(transferred)} / {Logger.FormatBytes(total)})";
                                            lblTotalBytes.Text = $"{overallPct}% ({Logger.FormatBytes(currentTotalBytes)} / {Logger.FormatBytes(totalBytesAllFiles)})";

                                            if (isPaused)
                                            {
                                                lblSpeed.Text = "Пауза";
                                            }
                                            else
                                            {
                                                lblSpeed.Text = $"{FormatSpeed(currentSpeedBytesPerSec)} ({FormatBits(currentSpeedBytesPerSec * 8)})";
                                            }

                                            TimeSpan elapsed = totalTimer.Elapsed;
                                            string elapsedStr = elapsed.ToString(@"hh\:mm\:ss");
                                            if (currentSpeedBytesPerSec > 0 && totalBytesAllFiles > currentTotalBytes)
                                            {
                                                double remainingSec = (totalBytesAllFiles - currentTotalBytes) / currentSpeedBytesPerSec;
                                                TimeSpan eta = TimeSpan.FromSeconds(remainingSec);
                                                lblTime.Text = $"{elapsedStr}  (осталось: ~{eta:hh\\:mm\\:ss})";
                                            }
                                            else
                                            {
                                                lblTime.Text = elapsedStr;
                                            }
                                        });
                                    }

                                    return false;
                                };

                                if (isUpload && !string.IsNullOrEmpty(it.FileRecord.SourcePath))
                                {
                                    try
                                    {
                                        if (File.Exists(it.FileRecord.SourcePath))
                                        {
                                            string caption = string.IsNullOrEmpty(it.FileRecord.Parent) ? it.FileRecord.Name : $"{it.FileRecord.Parent}\\{it.FileRecord.Name}";
                                            int msgId = await TelegramManager.UploadAndSendFileAsync(
                                                channelId,
                                                it.FileRecord.SourcePath,
                                                it.FileRecord.Name,
                                                caption,
                                                onProgress: progressHandler,
                                                cancellationToken: cts.Token,
                                                pauseGate: pauseGate);

                                            if (msgId > 0)
                                            {
                                                if (!string.IsNullOrEmpty(it.FileRecord.Uid))
                                                {
                                                    db.MoveFileToTrash(it.FileRecord.Uid);
                                                }

                                                db.EnsureParentDirectoriesExist(it.FileRecord.MountId, it.FileRecord.Parent);
                                                var fi = new FileInfo(it.FileRecord.SourcePath);
                                                int newVer = string.IsNullOrEmpty(it.FileRecord.Uid) ? 1 : it.FileRecord.Ver + 1;

                                                db.AddFile(new VfsDatabase.FileRecord
                                                {
                                                    Uid = Guid.NewGuid().ToString("N"),
                                                    MountId = it.FileRecord.MountId,
                                                    IsDir = false,
                                                    Name = it.FileRecord.Name,
                                                    Parent = it.FileRecord.Parent,
                                                    MTime = fi.LastWriteTimeUtc,
                                                    Size = fi.Length,
                                                    TgMessageId = msgId,
                                                    InTrash = 0,
                                                    Ver = newVer,
                                                    SourcePath = it.FileRecord.SourcePath
                                                });

                                                it.Status = SyncItemStatus.Identical;
                                                it.StatusText = "Загружен в TG";
                                                it.DirectionSymbol = "TG  =  ПК";
                                                it.DirectionText = "Синхронизировано";
                                                it.FileRecord.Ver = newVer;
                                                it.FileRecord.Size = fi.Length;
                                                it.FileRecord.MTime = fi.LastWriteTimeUtc;
                                                it.LocalSize = fi.Length;
                                                it.LocalWriteTime = fi.LastWriteTime;
                                                it.IsChecked = false;

                                                it.ToolTipDetails = $"[TG  =  ПК] (Идентичны / Синхронизировано)\n" +
                                                                      $"• Дата: {fi.LastWriteTime:dd.MM.yy HH:mm:ss}\n" +
                                                                      $"• Размер: {fi.Length:#,##0} байт\n" +
                                                                      $"• Источник: {it.FileRecord.SourcePath}";

                                                updated++;
                                                completedBytesAllFiles += currentFileTotalSize;
                                            }
                                            else
                                            {
                                                errors++;
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        cancellationRequested = true;
                                        break;
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Error("UI", $"Upload error for '{it.FileRecord.Name}': {ex.Message}", ex);
                                        errors++;
                                    }
                                }
                                else if (it.Status == SyncItemStatus.RemoteNewer && !string.IsNullOrEmpty(it.FileRecord.SourcePath))
                                {
                                    try
                                    {
                                        string dir = Path.GetDirectoryName(it.FileRecord.SourcePath) ?? "";
                                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                        {
                                            Directory.CreateDirectory(dir);
                                        }

                                        string tempFile = it.FileRecord.SourcePath + ".tmp_sync";
                                        await TelegramManager.DownloadFileAsync(
                                            channelId,
                                            it.FileRecord.TgMessageId,
                                            tempFile,
                                            onProgress: progressHandler,
                                            cancellationToken: cts.Token,
                                            pauseGate: pauseGate);

                                        if (File.Exists(tempFile))
                                        {
                                            if (File.Exists(it.FileRecord.SourcePath))
                                            {
                                                File.Delete(it.FileRecord.SourcePath);
                                            }
                                            File.Move(tempFile, it.FileRecord.SourcePath);
                                            File.SetLastWriteTimeUtc(it.FileRecord.SourcePath, it.FileRecord.MTime.ToUniversalTime());

                                            var fi = new FileInfo(it.FileRecord.SourcePath);
                                            it.Status = SyncItemStatus.Identical;
                                            it.StatusText = "Обновлен на ПК";
                                            it.DirectionSymbol = "TG  =  ПК";
                                            it.DirectionText = "Синхронизировано";
                                            it.LocalSize = fi.Length;
                                            it.LocalWriteTime = fi.LastWriteTime;
                                            it.IsChecked = false;

                                            it.ToolTipDetails = $"[TG  =  ПК] (Идентичны / Синхронизировано)\n" +
                                                                  $"• Дата: {fi.LastWriteTime:dd.MM.yy HH:mm:ss}\n" +
                                                                  $"• Размер: {fi.Length:#,##0} байт\n" +
                                                                  $"• Источник: {it.FileRecord.SourcePath}";

                                            updated++;
                                            completedBytesAllFiles += currentFileTotalSize;
                                        }
                                        else
                                        {
                                            errors++;
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        cancellationRequested = true;
                                        break;
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Error("UI", $"Download error for '{it.FileRecord.Name}': {ex.Message}", ex);
                                        errors++;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (form.IsHandleCreated && !form.IsDisposed)
                            {
                                form.Invoke(() =>
                                {
                                    if (form.IsDisposed) return;

                                    Win32Api.RefreshActivePanel();
                                    PopulateListView();

                                    string statusMsg = cancellationRequested
                                        ? $"Синхронизация отменена пользователем.\nУспешно обработано: {updated}\nОшибок: {errors}"
                                        : $"Синхронизация завершена.\nУспешно обработано: {updated}\nОшибок: {errors}";

                                    MessageBox.Show(form, statusMsg, "Smart Sync", MessageBoxButtons.OK, cancellationRequested ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

                                    syncProgressPanel.Visible = false;
                                    UpdateSyncLayout();

                                    syncBtn.Enabled = true;
                                    selectUpdatesBtn.Enabled = true;
                                    clearSelectionBtn.Enabled = true;
                                    hideIdenticalCb.Enabled = true;
                                    listView.Enabled = true;

                                    closeBtn.Text = "Закрыть";
                                    toolTip.SetToolTip(closeBtn, "Закрыть окно синхронизации");
                                    closeBtn.DialogResult = DialogResult.Cancel;
                                });
                            }
                        }
                    });
                };

                // Сохранение положения формы при закрытии
                form.FormClosing += (s, e) =>
                {
                    if (closeBtn.Text == "Отмена")
                    {
                        cancellationRequested = true;
                        cts?.Cancel();
                        pauseGate.Set();
                    }

                    bool isMax = form.WindowState == FormWindowState.Maximized;
                    SettingsManager.SaveSetting("smartsync_maximized", isMax ? "1" : "0");
                    SettingsManager.SaveSetting("smartsync_hide_identical", hideIdenticalCb.Checked ? "1" : "0");

                    if (!isMax)
                    {
                        SettingsManager.SaveSetting("smartsync_width", form.ClientSize.Width.ToString());
                        SettingsManager.SaveSetting("smartsync_height", form.ClientSize.Height.ToString());
                    }

                    if (listView.Columns.Count >= 7)
                    {
                        SettingsManager.SaveSetting("smartsync_col_vfs", listView.Columns[0].Width.ToString());
                        SettingsManager.SaveSetting("smartsync_col_tg_size", listView.Columns[1].Width.ToString());
                        SettingsManager.SaveSetting("smartsync_col_tg_date", listView.Columns[2].Width.ToString());
                        SettingsManager.SaveSetting("smartsync_col_direction", listView.Columns[3].Width.ToString());
                        SettingsManager.SaveSetting("smartsync_col_pc_date", listView.Columns[4].Width.ToString());
                        SettingsManager.SaveSetting("smartsync_col_pc_size", listView.Columns[5].Width.ToString());
                        SettingsManager.SaveSetting("smartsync_col_source", listView.Columns[6].Width.ToString());
                    }
                };

                form.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Error("UI", $"Fatal error in SmartSyncDialog: {ex.Message}", ex);
            }
        });
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 Б/с";
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):0.0} МБ/с";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:0.0} КБ/с";
        return $"{bytesPerSec:0} Б/с";
    }

    private static string FormatBits(double bitsPerSec)
    {
        if (bitsPerSec <= 0) return "0 Мбит/с";
        if (bitsPerSec >= 1_000_000)
            return $"{bitsPerSec / 1_000_000:0.0} Мбит/с";
        if (bitsPerSec >= 1_000)
            return $"{bitsPerSec / 1_000:0.0} Кбит/с";
        return $"{bitsPerSec:0} бит/с";
    }

    private static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{span.Days} дн {span.Hours} ч";
        if (span.TotalHours >= 1) return $"{span.Hours} ч {span.Minutes} мин";
        if (span.TotalMinutes >= 1) return $"{span.Minutes} мин {span.Seconds} сек";
        return $"{span.Seconds} сек";
    }
}
