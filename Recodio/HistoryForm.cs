namespace Recodio;

public class HistoryForm : Form
{
    private readonly ListView _listView;
    private readonly ComboBox _cmbFilter;
    private readonly List<HistoryEntry> _history;
    private readonly Action _onSave;

    public HistoryForm(List<HistoryEntry> history, Action onSave)
    {
        _history = history;
        _onSave = onSave;

        Text = "Historial";
        Size = new Size(720, 460);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 360);

        var lblFilter = new Label { Text = "Filtrar:", Location = new Point(10, 12), AutoSize = true };
        Controls.Add(lblFilter);

        _cmbFilter = new ComboBox
        {
            Location = new Point(60, 9),
            Size = new Size(160, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbFilter.Items.AddRange(["Todo", "Convertir", "Video (yt-dlp)", "Spotify (spotDL)", "Solo errores"]);
        _cmbFilter.SelectedIndex = 0;
        _cmbFilter.SelectedIndexChanged += (_, _) => Reload();
        Controls.Add(_cmbFilter);

        _listView = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Location = new Point(10, 40),
            Size = new Size(685, 330),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            MultiSelect = false,
        };
        _listView.Columns.Add("Tipo", 80);
        _listView.Columns.Add("Estado", 70);
        _listView.Columns.Add("Nombre", 240);
        _listView.Columns.Add("Fecha", 130);
        _listView.Columns.Add("Detalle", 140);
        _listView.DoubleClick += (_, _) => OpenSelectedFolder();
        Controls.Add(_listView);

        var btnOpen = new Button { Text = "Abrir carpeta", Location = new Point(10, 380), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        btnOpen.Click += (_, _) => OpenSelectedFolder();
        Controls.Add(btnOpen);

        var btnCopy = new Button { Text = "Copiar detalle", Location = new Point(110, 380), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        btnCopy.Click += (_, _) =>
        {
            if (_listView.SelectedItems.Count == 0) return;
            if (_listView.SelectedItems[0].Tag is HistoryEntry e)
            {
                var text = string.IsNullOrWhiteSpace(e.Detail) ? e.Path : e.Detail;
                if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
            }
        };
        Controls.Add(btnCopy);

        var btnClear = new Button { Text = "Limpiar historial", Location = new Point(230, 380), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        btnClear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "¿Borrar todo el historial?", "Recodio",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _history.Clear();
            _onSave();
            Reload();
        };
        Controls.Add(btnClear);

        var btnClose = new Button { Text = "Cerrar", Location = new Point(610, 380), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
        Controls.Add(btnClose);
        CancelButton = btnClose;

        Reload();
    }

    private void Reload()
    {
        _listView.Items.Clear();
        IEnumerable<HistoryEntry> q = _history;
        q = _cmbFilter.SelectedIndex switch
        {
            1 => q.Where(e => e.Kind is "convert" or "" or null),
            2 => q.Where(e => e.Kind == "ytdlp"),
            3 => q.Where(e => e.Kind == "spotdl"),
            4 => q.Where(e => e.Status is "fail" or "partial"),
            _ => q,
        };

        foreach (var item in q.Reverse())
        {
            var kind = string.IsNullOrEmpty(item.Kind) ? "convert" : item.Kind;
            var status = string.IsNullOrEmpty(item.Status) ? "ok" : item.Status;
            var li = new ListViewItem(HistoryEntry.KindLabel(kind)) { Tag = item };
            li.SubItems.Add(HistoryEntry.StatusLabel(status));
            li.SubItems.Add(item.Name);
            li.SubItems.Add(item.Date);
            var detail = string.IsNullOrWhiteSpace(item.Detail) ? item.Path : item.Detail;
            if (detail.Length > 80) detail = detail[..77] + "...";
            li.SubItems.Add(detail);
            if (status == "fail") li.ForeColor = Color.IndianRed;
            else if (status == "partial") li.ForeColor = Color.DarkOrange;
            _listView.Items.Add(li);
        }
    }

    private void OpenSelectedFolder()
    {
        if (_listView.SelectedItems.Count == 0) return;
        if (_listView.SelectedItems[0].Tag is not HistoryEntry e) return;
        var p = e.Path;
        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
            return;
        }
        if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
        }
    }
}
