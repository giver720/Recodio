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
    private readonly ToolTip _itemTip = new() { AutoPopDelay = 8000, InitialDelay = 300, ReshowDelay = 100 };
    private readonly Font _boldQueueFont;
    private int _lastTipIndex = -1;
    private DateTime? _startedAt;
    private string? _folderPath;
    private int _overallPct;
    private int _filePct;

    // Preserve last detail across status-only updates
    private double? _lastSpeed;
    private TimeSpan? _lastEta;
    private string? _lastSize;

    // Dual-stream (video then audio): keep high-water file % until a new item starts
    private string? _fileSegmentKey;
    private int _fileHighWater;

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

        _boldQueueFont = new Font(parent.Font, FontStyle.Bold);

        QueueList = new ListBox
        {
            Location = new Point(0, 110),
            Size = new Size(width, 72),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            IntegralHeight = false,
            HorizontalScrollbar = true,
            BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Math.Max(15, parent.Font.Height + 4),
        };
        QueueList.DrawItem += QueueList_DrawItem;
        // Hover tooltip: shows the last error line for a failed item without digging in the log.
        QueueList.MouseMove += QueueList_MouseMove;
        QueueList.MouseLeave += (_, _) =>
        {
            _itemTip.Hide(QueueList);
            _lastTipIndex = -1;
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
        _lastSpeed = null;
        _lastEta = null;
        _lastSize = null;
        _fileSegmentKey = null;
        _fileHighWater = 0;
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
        _lastSpeed = null;
        _lastEta = null;
        _lastSize = null;
        _fileSegmentKey = null;
        _fileHighWater = 0;
        _elapsedTimer.Start();
        RefreshElapsed();
    }

    /// <param name="markComplete">If true, force overall bar to 100%. False on cancel/error.</param>
    public void EndSession(string summary, bool isError = false, string? folderPath = null, bool markComplete = true)
    {
        _elapsedTimer.Stop();
        RefreshElapsed();
        if (markComplete)
            SetOverallPercent(100);
        SetFilePercent(0, force: true);
        FilePercentLabel.Text = "";
        _lastSpeed = null;
        _lastEta = null;
        _lastSize = null;
        _fileSegmentKey = null;
        _fileHighWater = 0;
        DetailLabel.Text = "";
        CurrentLabel.Text = "";
        StatusLabel.Text = summary;
        StatusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        if (!string.IsNullOrWhiteSpace(folderPath))
            _folderPath = folderPath;
        OpenFolderLink.Visible = !string.IsNullOrWhiteSpace(_folderPath) && Directory.Exists(_folderPath);
    }

    /// <summary>Stop the elapsed timer (call from FormClosed).</summary>
    public void StopTimer()
    {
        try { _elapsedTimer.Stop(); } catch { /* dispose race */ }
    }

    public void SetOverallPercent(int percent)
    {
        _overallPct = Math.Clamp(percent, 0, 100);
        try
        {
            if (!OverallBar.IsDisposed)
                OverallBar.Value = _overallPct;
            if (!PercentLabel.IsDisposed)
                PercentLabel.Text = $"{_overallPct}%";
        }
        catch (ObjectDisposedException) { /* closing */ }
    }

    /// <param name="force">If false, dual-stream: never lower the bar until a new segment/item.</param>
    public void SetFilePercent(int percent, bool force = false, string? segmentKey = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (!string.IsNullOrEmpty(segmentKey) && !string.Equals(segmentKey, _fileSegmentKey, StringComparison.Ordinal))
        {
            _fileSegmentKey = segmentKey;
            _fileHighWater = 0;
            force = true;
        }
        if (!force && percent < _fileHighWater && percent < 100)
            percent = _fileHighWater;
        if (percent >= _fileHighWater)
            _fileHighWater = percent;
        if (percent >= 100)
            _fileHighWater = 100;

        _filePct = percent;
        try
        {
            if (!FileBar.IsDisposed)
                FileBar.Value = _filePct;
            if (!FilePercentLabel.IsDisposed)
                FilePercentLabel.Text = _filePct > 0 && _filePct < 100 ? $"{_filePct}%" : "";
        }
        catch (ObjectDisposedException) { /* closing */ }
    }

    public void SetStats(string text) => StatsLabel.Text = text;

    public void SetDetail(string text) => DetailLabel.Text = text ?? "";

    public void SetCurrent(string text) => CurrentLabel.Text = string.IsNullOrWhiteSpace(text) ? "" : text;

    public void SetStatus(string text, bool isError = false)
    {
        StatusLabel.Text = text;
        StatusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
    }

    // --- Backward-compatible helpers ---
    public ProgressBar Bar => OverallBar;

    public void SetPercent(int percent) => SetOverallPercent(percent);

    public void SetQueue(string text) => SetStats(text);

    public void SetDone(string message = "Completado.") => EndSession(message, markComplete: true);

    public void SetOverallProgress(int completed, int total, int filePercent = 0)
    {
        if (total <= 0) { SetOverallPercent(filePercent); return; }
        var overall = (int)Math.Round((completed + filePercent / 100.0) * 100.0 / total);
        SetOverallPercent(overall);
        SetFilePercent(filePercent);
    }

    public void Apply(DownloadProgressUpdate u)
    {
        var segmentKey = u.ItemKey
            ?? (!string.IsNullOrWhiteSpace(u.CurrentTitle) ? u.CurrentTitle : null);

        // New item → reset dual-stream high-water
        if (!string.IsNullOrEmpty(segmentKey)
            && !string.Equals(segmentKey, _fileSegmentKey, StringComparison.Ordinal)
            && u.ItemState == QueueItemState.Downloading)
        {
            _fileSegmentKey = segmentKey;
            _fileHighWater = 0;
        }

        // --- bars ---
        if (u.Total > 0)
        {
            var finished = u.Done + u.Skipped; // toward completion of selection
            // Only recalculate when OverallPercent is null (not when explicitly 0)
            if (u.OverallPercent is { } explicitOverall)
            {
                SetOverallPercent(explicitOverall);
            }
            else
            {
                var fileFrac = (u.FilePercent ?? 0) / 100.0;
                var inProgress = finished < u.Total && u.FilePercent is > 0;
                var computed = (int)Math.Round((finished + (inProgress ? fileFrac : 0)) * 100.0 / u.Total);
                SetOverallPercent(computed);
            }

            if (u.FilePercent is { } fp)
            {
                var force = u.Phase is "merge" or "extract" || fp >= 100;
                SetFilePercent(fp, force: force, segmentKey: segmentKey);
            }

            var pending = Math.Max(0, u.Total - u.Done - u.Skipped - u.Failed);
            var parts = new List<string>();
            if (u.Done > 0) parts.Add($"{u.Done} ok");
            if (u.Skipped > 0) parts.Add($"{u.Skipped} omit");
            if (u.Failed > 0) parts.Add($"{u.Failed} err");
            if (pending > 0) parts.Add($"{pending} pend");
            if (parts.Count == 0) parts.Add($"0 / {u.Total}");

            var stats = string.Join(" · ", parts) + $" · total {u.Total}";

            // Selection position (not batch)
            var selPos = u.CurrentIndex;
            if (selPos <= 0 && finished < u.Total && (u.FilePercent is > 0 || !string.IsNullOrWhiteSpace(u.CurrentTitle)))
                selPos = Math.Min(finished + 1, u.Total);
            if (selPos > 0)
                stats += $" · item {selPos}/{u.Total}";

            // Optional batch subset (yt-dlp remaining playlist-items)
            if (u.BatchIndex > 0 && u.BatchTotal > 0
                && (u.BatchTotal != u.Total || u.BatchIndex != selPos))
                stats += $" · lote {u.BatchIndex}/{u.BatchTotal}";

            // Which full retry pass ("Barrido"/"Pasada") this is, when there's more than one.
            if (u.PassTotal > 1)
                stats += $" · pasada {Math.Max(1, u.PassIndex)}/{u.PassTotal}";

            // Whole-queue ETA: average wall-clock time per finished item (done+failed - skips
            // are near-instant and would skew the average) projected over what's left.
            if (_startedAt is { } startedAt && pending > 0)
            {
                var consumed = u.Done + u.Failed;
                if (consumed > 0)
                {
                    var perItem = (DateTime.Now - startedAt).TotalSeconds / consumed;
                    var etaSecs = perItem * pending;
                    if (etaSecs is > 0 and < 100_000)
                    {
                        var etaText = DownloadProgressUpdate.FormatEta(TimeSpan.FromSeconds(etaSecs));
                        if (!string.IsNullOrEmpty(etaText))
                            stats += $" · ETA total {etaText}";
                    }
                }
            }

            StatsLabel.Text = stats;
        }
        else
        {
            // Partial update: file % only — do NOT treat file% as overall
            if (u.FilePercent is { } fpOnly)
                SetFilePercent(fpOnly, force: fpOnly >= 100, segmentKey: segmentKey);
            if (u.OverallPercent is { } op)
                SetOverallPercent(op);
        }

        // --- detail: preserve last speed/ETA/size ---
        if (u.ClearDetail)
        {
            _lastSpeed = null;
            _lastEta = null;
            _lastSize = null;
            DetailLabel.Text = "";
        }
        else
        {
            if (u.SpeedBytesPerSec is > 0)
                _lastSpeed = u.SpeedBytesPerSec;
            if (u.Eta is { } etaVal && etaVal.TotalSeconds >= 0)
                _lastEta = etaVal;
            if (!string.IsNullOrWhiteSpace(u.SizeInfo))
                _lastSize = u.SizeInfo;

            var detailParts = new List<string>();
            var speed = DownloadProgressUpdate.FormatSpeed(_lastSpeed);
            if (!string.IsNullOrEmpty(speed)) detailParts.Add(speed);
            var eta = DownloadProgressUpdate.FormatEta(_lastEta);
            if (!string.IsNullOrEmpty(eta)) detailParts.Add($"ETA {eta}");
            if (!string.IsNullOrWhiteSpace(_lastSize)) detailParts.Add(_lastSize!);
            if (!string.IsNullOrWhiteSpace(u.Phase)
                && u.Phase is not ("download" or ""))
                detailParts.Add(u.Phase);
            if (detailParts.Count > 0)
                DetailLabel.Text = string.Join(" · ", detailParts);
        }

        if (!string.IsNullOrWhiteSpace(u.CurrentTitle))
            CurrentLabel.Text = u.CurrentTitle!;
        if (!string.IsNullOrWhiteSpace(u.Status))
            SetStatus(u.Status!, u.IsError);

        if (!string.IsNullOrEmpty(u.ItemKey) && u.ItemState is { } st)
            SetItemState(u.ItemKey, st, u.CurrentTitle,
                errorDetail: st == QueueItemState.Failed ? u.Status : null);
    }

    public void ClearQueue()
    {
        _items.Clear();
        _keyToIndex.Clear();
        QueueList.Items.Clear();
        _itemTip.Hide(QueueList);
        _lastTipIndex = -1;
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

    public void SetItemState(string key, QueueItemState state, string? title = null, string? errorDetail = null)
    {
        if (!_keyToIndex.TryGetValue(key, out var idx) || idx < 0 || idx >= _items.Count)
        {
            // Try bare id match against keys ending with same id
            if (string.IsNullOrEmpty(key)) return;
            idx = -1;
            for (var i = 0; i < _items.Count; i++)
            {
                var k = _items[i].Key;
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase)
                    || k.EndsWith(key, StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith(k, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return;
        }
        var it = _items[idx];
        // Don't overwrite terminal states with Downloading; don't demote Done/Failed to Skipped.
        if (state == QueueItemState.Downloading
            && it.State is QueueItemState.Done or QueueItemState.Skipped or QueueItemState.Failed)
            return;
        if (state == QueueItemState.Skipped
            && it.State is QueueItemState.Done or QueueItemState.Failed)
            return;
        if (state == QueueItemState.Done && it.State == QueueItemState.Skipped)
            return; // keep pre-skipped as ⏭
        it.State = state;
        if (!string.IsNullOrWhiteSpace(title)
            && !title.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
            && title.Length < 200)
            it.Title = title!;
        if (state == QueueItemState.Failed && !string.IsNullOrWhiteSpace(errorDetail))
            it.ErrorDetail = errorDetail;
        // Owner-draw ListBox redraws the row when its backing item is replaced.
        QueueList.Items[idx] = it.DisplayText;
        if (state == QueueItemState.Downloading)
        {
            try { QueueList.TopIndex = Math.Max(0, idx - 1); } catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(it.Title))
                CurrentLabel.Text = it.Title;
        }
    }

    public void SetItemStateByIndex(int index0, QueueItemState state)
    {
        if (index0 < 0 || index0 >= _items.Count) return;
        SetItemState(_items[index0].Key, state);
    }

    public IReadOnlyList<ProgressQueueItem> Items => _items;

    // Color-codes each queue row by state so a 20+ item playlist is scannable at a glance
    // instead of relying only on the ▶✓⏭✗ text glyph.
    private void QueueList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0 && e.Index < _items.Count)
        {
            var item = _items[e.Index];
            var color = item.State switch
            {
                QueueItemState.Done => Color.ForestGreen,
                QueueItemState.Failed => Color.Firebrick,
                QueueItemState.Skipped => SystemColors.GrayText,
                QueueItemState.Downloading => Color.RoyalBlue,
                _ => e.ForeColor,
            };
            var font = item.State == QueueItemState.Downloading ? _boldQueueFont : e.Font ?? QueueList.Font;
            TextRenderer.DrawText(e.Graphics, item.DisplayText, font, e.Bounds, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
        }
        e.DrawFocusRectangle();
    }

    private void QueueList_MouseMove(object? sender, MouseEventArgs e)
    {
        var idx = QueueList.IndexFromPoint(e.Location);
        if (idx == _lastTipIndex) return;
        _lastTipIndex = idx;
        if (idx < 0 || idx >= _items.Count)
        {
            _itemTip.Hide(QueueList);
            return;
        }
        var it = _items[idx];
        if (it.State == QueueItemState.Failed && !string.IsNullOrWhiteSpace(it.ErrorDetail))
            _itemTip.Show(it.ErrorDetail, QueueList, e.X + 12, e.Y + 12, 6000);
        else
            _itemTip.Hide(QueueList);
    }

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
