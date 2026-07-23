namespace Recodio;

/// <summary>
/// Rich download progress UI: overall + file bars, %, speed/ETA, queue checklist, elapsed.
/// </summary>
public sealed class DownloadProgressPanel
{
    public const int PreferredHeight = 210;

    public Panel Host { get; }
    public ProgressBar OverallBar { get; }
    public ProgressBar FileBar { get; }
    public Label PercentLabel { get; }
    public Label FilePercentLabel { get; }
    public Label StatsLabel { get; }
    public Label DetailLabel { get; }
    public Label CurrentLabel { get; }
    public Label StatusLabel { get; }
    public Label ElapsedLabel { get; }
    public LinkLabel OpenFolderLink { get; }
    public ListBox QueueList { get; }

    private readonly List<ProgressQueueItem> _items = [];
    private readonly Dictionary<string, int> _keyToIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _elapsedTimer;
    private DateTime? _startedAt;
    private string? _folderPath;
    private int _overallPct;
    private int _filePct;

    public DownloadProgressPanel(int x, int y, int width, Control parent)
    {
        Host = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(width, PreferredHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        parent.Controls.Add(Host);

        OverallBar = new ProgressBar
        {
            Location = new Point(0, 0),
            Size = new Size(width - 56, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous,
        };
        Host.Controls.Add(OverallBar);

        PercentLabel = new Label
        {
            Text = "0%",
            Location = new Point(width - 52, 1),
            Size = new Size(52, 20),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font(parent.Font.FontFamily, 11f, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        Host.Controls.Add(PercentLabel);

        FileBar = new ProgressBar
        {
            Location = new Point(0, 26),
            Size = new Size(width - 56, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous,
        };
        Host.Controls.Add(FileBar);

        FilePercentLabel = new Label
        {
            Text = "",
            Location = new Point(width - 52, 24),
            Size = new Size(52, 14),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font(parent.Font.FontFamily, 7.5f),
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        Host.Controls.Add(FilePercentLabel);

        StatsLabel = new Label
        {
            Text = "Cola: —",
            Location = new Point(0, 42),
            Size = new Size(width, 16),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font(parent.Font.FontFamily, 8.5f, FontStyle.Bold),
        };
        Host.Controls.Add(StatsLabel);

        DetailLabel = new Label
        {
            Text = "",
            Location = new Point(0, 58),
            Size = new Size(width, 15),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
            Font = new Font(parent.Font.FontFamily, 8f),
        };
        Host.Controls.Add(DetailLabel);

        CurrentLabel = new Label
        {
            Text = "",
            Location = new Point(0, 74),
            Size = new Size(width, 16),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Host.Controls.Add(CurrentLabel);

        StatusLabel = new Label
        {
            Text = "Listo.",
            Location = new Point(0, 90),
            Size = new Size(width, 16),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
        };
        Host.Controls.Add(StatusLabel);

        QueueList = new ListBox
        {
            Location = new Point(0, 110),
            Size = new Size(width, 72),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            IntegralHeight = false,
            HorizontalScrollbar = true,
            BorderStyle = BorderStyle.FixedSingle,
        };
        Host.Controls.Add(QueueList);

        ElapsedLabel = new Label
        {
            Text = "",
            Location = new Point(0, 186),
            Size = new Size(120, 16),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            ForeColor = SystemColors.GrayText,
            Font = new Font(parent.Font.FontFamily, 8f),
        };
        Host.Controls.Add(ElapsedLabel);

        OpenFolderLink = new LinkLabel
        {
            Text = "Abrir carpeta",
            Location = new Point(130, 186),
            AutoSize = true,
            Visible = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        OpenFolderLink.LinkClicked += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _folderPath,
                    UseShellExecute = true,
                });
            }
            catch { /* ignore */ }
        };
        Host.Controls.Add(OpenFolderLink);

        _elapsedTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _elapsedTimer.Tick += (_, _) => RefreshElapsed();
    }

    public void Reset(string statsText = "Cola: —", string status = "Listo.")
    {
        _startedAt = null;
        _elapsedTimer.Stop();
        _folderPath = null;
        OpenFolderLink.Visible = false;
        ElapsedLabel.Text = "";
        SetOverallPercent(0);
        SetFilePercent(0);
        StatsLabel.Text = statsText;
        DetailLabel.Text = "";
        CurrentLabel.Text = "";
        StatusLabel.Text = status;
        StatusLabel.ForeColor = SystemColors.GrayText;
        ClearQueue();
    }

    public void StartSession(string? folderPath = null)
    {
        _startedAt = DateTime.Now;
        _folderPath = folderPath;
        OpenFolderLink.Visible = false;
        _elapsedTimer.Start();
        RefreshElapsed();
    }

    public void EndSession(string summary, bool isError = false, string? folderPath = null)
    {
        _elapsedTimer.Stop();
        RefreshElapsed();
        SetOverallPercent(100);
        SetFilePercent(0);
        FilePercentLabel.Text = "";
        DetailLabel.Text = "";
        CurrentLabel.Text = "";
        StatusLabel.Text = summary;
        StatusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        if (!string.IsNullOrWhiteSpace(folderPath))
            _folderPath = folderPath;
        OpenFolderLink.Visible = !string.IsNullOrWhiteSpace(_folderPath) && Directory.Exists(_folderPath);
    }

    public void SetOverallPercent(int percent)
    {
        _overallPct = Math.Clamp(percent, 0, 100);
        OverallBar.Value = _overallPct;
        PercentLabel.Text = $"{_overallPct}%";
    }

    public void SetFilePercent(int percent)
    {
        _filePct = Math.Clamp(percent, 0, 100);
        FileBar.Value = _filePct;
        FilePercentLabel.Text = _filePct > 0 && _filePct < 100 ? $"{_filePct}%" : "";
    }

    public void SetStats(string text) => StatsLabel.Text = text;

    public void SetDetail(string text) => DetailLabel.Text = text ?? "";

    public void SetCurrent(string text) => CurrentLabel.Text = string.IsNullOrWhiteSpace(text) ? "" : text;

    public void SetStatus(string text, bool isError = false)
    {
        StatusLabel.Text = text;
        StatusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
    }

    // --- Backward-compatible helpers used by older call sites ---
    public ProgressBar Bar => OverallBar;

    public void SetPercent(int percent) => SetOverallPercent(percent);

    public void SetQueue(string text) => SetStats(text);

    public void SetDone(string message = "Completado.") => EndSession(message);

    public void SetOverallProgress(int completed, int total, int filePercent = 0)
    {
        if (total <= 0) { SetOverallPercent(filePercent); return; }
        var overall = (int)Math.Round((completed + filePercent / 100.0) * 100.0 / total);
        SetOverallPercent(overall);
        SetFilePercent(filePercent);
    }

    public void Apply(DownloadProgressUpdate u)
    {
        if (u.Total > 0)
        {
            var doneBase = u.Done + u.Skipped; // skipped count toward completion
            var overall = u.OverallPercent;
            if (overall <= 0 && u.Total > 0)
                overall = (int)Math.Round((doneBase + u.FilePercent / 100.0) * 100.0 / u.Total);
            SetOverallPercent(overall);
            SetFilePercent(u.FilePercent);

            var pending = Math.Max(0, u.Total - u.Done - u.Skipped - u.Failed);
            var parts = new List<string>();
            if (u.Done > 0) parts.Add($"{u.Done} ok");
            if (u.Skipped > 0) parts.Add($"{u.Skipped} omit");
            if (u.Failed > 0) parts.Add($"{u.Failed} err");
            if (pending > 0) parts.Add($"{pending} pend");
            if (parts.Count == 0) parts.Add($"0 / {u.Total}");
            var stats = string.Join(" · ", parts) + $" · total {u.Total}";
            if (u.CurrentIndex > 0)
                stats += $" · item {u.CurrentIndex}/{u.Total}";
            StatsLabel.Text = stats;
        }
        else if (u.OverallPercent > 0)
        {
            SetOverallPercent(u.OverallPercent);
            SetFilePercent(u.FilePercent);
        }

        var detailParts = new List<string>();
        var speed = DownloadProgressUpdate.FormatSpeed(u.SpeedBytesPerSec);
        if (!string.IsNullOrEmpty(speed)) detailParts.Add(speed);
        var eta = DownloadProgressUpdate.FormatEta(u.Eta);
        if (!string.IsNullOrEmpty(eta)) detailParts.Add($"ETA {eta}");
        if (!string.IsNullOrWhiteSpace(u.SizeInfo)) detailParts.Add(u.SizeInfo);
        if (!string.IsNullOrWhiteSpace(u.Phase)) detailParts.Add(u.Phase);
        DetailLabel.Text = string.Join(" · ", detailParts);

        if (!string.IsNullOrWhiteSpace(u.CurrentTitle))
            CurrentLabel.Text = u.CurrentTitle!;
        if (!string.IsNullOrWhiteSpace(u.Status))
            SetStatus(u.Status!, u.IsError);

        if (!string.IsNullOrEmpty(u.ItemKey) && u.ItemState is { } st)
            SetItemState(u.ItemKey, st, u.CurrentTitle);
    }

    public void ClearQueue()
    {
        _items.Clear();
        _keyToIndex.Clear();
        QueueList.Items.Clear();
    }

    public void SetQueueItems(IEnumerable<ProgressQueueItem> items)
    {
        ClearQueue();
        foreach (var it in items)
        {
            _keyToIndex[it.Key] = _items.Count;
            _items.Add(it);
            QueueList.Items.Add(it.DisplayText);
        }
    }

    public void SetItemState(string key, QueueItemState state, string? title = null)
    {
        if (!_keyToIndex.TryGetValue(key, out var idx) || idx < 0 || idx >= _items.Count)
            return;
        var it = _items[idx];
        it.State = state;
        if (!string.IsNullOrWhiteSpace(title)) it.Title = title!;
        QueueList.Items[idx] = it.DisplayText;
        if (state == QueueItemState.Downloading)
        {
            try { QueueList.TopIndex = Math.Max(0, idx - 1); } catch { /* ignore */ }
            CurrentLabel.Text = it.Title;
        }
    }

    public void SetItemStateByIndex(int index0, QueueItemState state)
    {
        if (index0 < 0 || index0 >= _items.Count) return;
        SetItemState(_items[index0].Key, state);
    }

    public IReadOnlyList<ProgressQueueItem> Items => _items;

    private void RefreshElapsed()
    {
        if (_startedAt is null)
        {
            ElapsedLabel.Text = "";
            return;
        }
        var elapsed = DateTime.Now - _startedAt.Value;
        ElapsedLabel.Text = elapsed.TotalHours >= 1
            ? $"⏱ {(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"⏱ {elapsed.Minutes}:{elapsed.Seconds:D2}";
    }
}
