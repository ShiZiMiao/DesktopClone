namespace DesktopClone;

/// <summary>自定义分身桌面分辨率的小对话框。</summary>
internal sealed class ResolutionDialog : Form
{
    private const int MinWidth = 640;
    private const int MinHeight = 480;
    private const int MaxSize = 8192;

    private readonly NumericUpDown _widthBox = new()
    {
        Minimum = MinWidth,
        Maximum = MaxSize,
        Increment = 20,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 4, 0, 4)
    };

    private readonly NumericUpDown _heightBox = new()
    {
        Minimum = MinHeight,
        Maximum = MaxSize,
        Increment = 20,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 4, 0, 4)
    };

    public ResolutionDialog(Size initialSize)
    {
        Text = "自定义分辨率";
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _widthBox.Value = Math.Clamp(initialSize.Width, MinWidth, MaxSize);
        _heightBox.Value = Math.Clamp(initialSize.Height, MinHeight, MaxSize);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(14, 14, 14, 10),
            Dock = DockStyle.Fill
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        for (var row = 0; row < 4; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.Controls.Add(CreateFieldLabel("宽度(像素)"), 0, 0);
        layout.Controls.Add(_widthBox, 1, 0);
        layout.Controls.Add(CreateFieldLabel("高度(像素)"), 0, 1);
        layout.Controls.Add(_heightBox, 1, 1);

        var note = new Label
        {
            Text = "允许范围 640×480 – 8192×8192。\n"
                   + "改动会在重新连接后生效,连接中的分身会自动重连。",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 10, 0, 0)
        };
        layout.Controls.Add(note, 0, 2);
        layout.SetColumnSpan(note, 2);

        var okButton = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 30),
            Margin = new Padding(8, 0, 0, 0)
        };
        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 30),
            Margin = new Padding(8, 0, 0, 0)
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0)
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public Size SelectedSize => new((int)_widthBox.Value, (int)_heightBox.Value);

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 8, 12, 8)
    };
}
