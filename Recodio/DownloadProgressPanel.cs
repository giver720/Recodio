namespace Recodio;

/// <summary>
/// Compact download progress UI: percent bar, queue line, current item, status.
/// Replaces the old full-height log TextBox on download forms.
/// </summary>
public sealed class DownloadProgressPanel
{
    public Panel Host { get; }
    public ProgressBar Bar { get; }
    public Label PercentLabel { get; }
    public Label QueueLabel { get; }
    public Label CurrentLabel { get; }
    public Label StatusLabel { get; }

    private int _percent;

    public DownloadProgressPanel(int x, int y, int width, Control parent)
    {
        Host = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(width, 96),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        parent.Controls.Add(Host);

        Bar = new ProgressBar
        {
            Location = new Point(0, 0),
            Size = new Size(width - 56, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous,
        };
        Host.Controls.Add(Bar);

        PercentLabel = new Label
        {
            Text = "0%",
            Location = new Point(width - 52, 2),
            Size = new Size(52, 22),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font(parent.Font.FontFamily, 11f, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        Host.Controls.Add(PercentLabel);

        QueueLabel = new Label
        {
            Text = "Cola: —",
            Location = new Point(0, 30),
            Size = new Size(width, 18),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font(parent.Font.FontFamily, 9f, FontStyle.Bold),
        };
        Host.Controls.Add(QueueLabel);

        CurrentLabel = new Label
        {
            Text = "",
            Location = new Point(0, 50),
            Size = new Size(width, 18),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Host.Controls.Add(CurrentLabel);

        StatusLabel = new Label
        {
            Text = "Listo.",
            Location = new Point(0, 70),
            Size = new Size(width, 20),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
        };
        Host.Controls.Add(StatusLabel);
    }

    public void Reset(string queueText = "Cola: —", string status = "Listo.")
    {
        SetPercent(0);
        QueueLabel.Text = queueText;
        CurrentLabel.Text = "";
        StatusLabel.Text = status;
        StatusLabel.ForeColor = SystemColors.GrayText;
    }

    public void SetPercent(int percent)
    {
        _percent = Math.Clamp(percent, 0, 100);
        Bar.Value = _percent;
        PercentLabel.Text = $"{_percent}%";
    }

    public void SetQueue(string text) => QueueLabel.Text = text;

    public void SetCurrent(string text) => CurrentLabel.Text = string.IsNullOrWhiteSpace(text) ? "" : text;

    public void SetStatus(string text, bool isError = false)
    {
        StatusLabel.Text = text;
        StatusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
    }

    /// <summary>Overall progress from completed items + current file fraction.</summary>
    public void SetOverallProgress(int completed, int total, int filePercent = 0)
    {
        if (total <= 0)
        {
            SetPercent(filePercent);
            return;
        }
        var overall = (int)Math.Round((completed + filePercent / 100.0) * 100.0 / total);
        SetPercent(overall);
    }

    public void SetDone(string message = "Completado.")
    {
        SetPercent(100);
        SetStatus(message);
    }
}
