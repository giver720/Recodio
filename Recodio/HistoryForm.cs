namespace Recodio;

public class HistoryForm : Form
{
    private readonly ListView _listView;

    public HistoryForm(IEnumerable<HistoryEntry> history)
    {
        Text = "Historial de conversiones";
        Size = new Size(560, 400);
        StartPosition = FormStartPosition.CenterScreen;

        _listView = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Location = new Point(10, 10),
            Size = new Size(525, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        _listView.Columns.Add("Nombre", 280);
        _listView.Columns.Add("Fecha", 130);
        _listView.Columns.Add("Tamano (KB)", 90);

        foreach (var item in history.Reverse())
        {
            var li = new ListViewItem(item.Name) { Tag = item.Path };
            li.SubItems.Add(item.Date);
            li.SubItems.Add(item.SizeKB.ToString());
            _listView.Items.Add(li);
        }
        Controls.Add(_listView);

        var btnOpen = new Button { Text = "Abrir carpeta", Location = new Point(10, 320), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        btnOpen.Click += (_, _) =>
        {
            if (_listView.SelectedItems.Count == 0) return;
            var p = (string)_listView.SelectedItems[0].Tag!;
            if (File.Exists(p)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
        };
        Controls.Add(btnOpen);

        var btnClose = new Button { Text = "Cerrar", Location = new Point(455, 320), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
        Controls.Add(btnClose);
        CancelButton = btnClose;
    }
}
