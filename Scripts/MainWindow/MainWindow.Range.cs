namespace WebVideoDownloader;

public partial class MainWindow
{
    private readonly CheckBox _rangeEnabled = new() { Text = "구간 저장", AutoSize = true };
    private readonly TextBox _rangeStart = new() { Text = "00:00:00", Width = 100, Enabled = false };
    private readonly TextBox _rangeEnd = new() { PlaceholderText = "끝까지", Width = 100, Enabled = false };

    private void InitializeRangeControls()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
        panel.Controls.AddRange(new Control[] {
            _rangeEnabled, new Label { Text = "시작", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _rangeStart,
            new Label { Text = "종료", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _rangeEnd,
            new Label { Text = "시:분:초 · 전체 수신 후 지정 구간을 MP4로 저장", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }
        });
        rootLayout.RowCount = 5;
        rootLayout.SetRow(logTextBox, 4);
        rootLayout.RowStyles.Insert(3, new RowStyle(SizeType.Absolute, 44));
        rootLayout.Controls.Add(panel, 0, 3);
        _rangeEnabled.CheckedChanged += (_, _) => _rangeStart.Enabled = _rangeEnd.Enabled = _rangeEnabled.Checked;
    }
}
