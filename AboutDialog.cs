using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopClone;

/// <summary>「关于」对话框:版本号与软件基本信息。</summary>
internal sealed class AboutDialog : Form
{
    private const string ProjectUrl = "https://github.com/ShiZiMiao/DesktopClone";

    private readonly Icon _appIcon = (Icon)SystemIcons.Application.Clone();
    private readonly Bitmap _logo;

    public AboutDialog()
    {
        using (var largeIcon = new Icon(_appIcon, 48, 48))
        {
            _logo = largeIcon.ToBitmap();
        }

        Text = "关于 桌面分身";
        Font = new Font("Microsoft YaHei UI", 9f);
        Icon = _appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 12),
            Dock = DockStyle.Fill
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var logo = new PictureBox
        {
            Image = _logo,
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 14, 0)
        };
        layout.Controls.Add(logo, 0, 0);
        layout.SetRowSpan(logo, 2);

        var title = new Label
        {
            Text = "桌面分身 DesktopClone",
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2)
        };
        layout.Controls.Add(title, 1, 0);

        var versionLabel = new Label
        {
            Text = $"版本 v{GetDisplayVersion()}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 0)
        };
        layout.Controls.Add(versionLabel, 1, 1);

        var description = new Label
        {
            Text = "利用 Windows 子会话(Child Session)与本机 RDP 打造的独立桌面分身:"
                   + "挂机的程序运行在分身会话中,主桌面完全不受影响。",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 14, 0, 12)
        };
        layout.Controls.Add(description, 0, 2);
        layout.SetColumnSpan(description, 2);

        var separator = new Panel
        {
            Height = 1,
            BackColor = SystemColors.ControlLight,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 10)
        };
        layout.Controls.Add(separator, 0, 3);
        layout.SetColumnSpan(separator, 2);

        var info = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddInfoRow(info, "版本", new Label
        {
            Text = $"v{GetDisplayVersion()}",
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 3)
        });
        AddInfoRow(info, "运行环境", new Label
        {
            Text = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription}",
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 3, 0, 3)
        });

        var projectLink = new LinkLabel
        {
            Text = ProjectUrl,
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 3)
        };
        projectLink.LinkClicked += OnProjectLinkClicked;
        AddInfoRow(info, "项目主页", projectLink);

        AddInfoRow(info, "开源许可", new Label
        {
            Text = "MIT",
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 3)
        });

        layout.Controls.Add(info, 0, 4);
        layout.SetColumnSpan(info, 2);

        var acknowledgment = new Label
        {
            Text = "机制与交互设计参考 BetterGI(GPL-3.0);本项目为全新实现,以 MIT 许可开源。",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 12, 0, 0)
        };
        layout.Controls.Add(acknowledgment, 0, 5);
        layout.SetColumnSpan(acknowledgment, 2);

        var okButton = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 30),
            Margin = new Padding(0, 14, 0, 0)
        };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        buttons.Controls.Add(okButton);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = okButton;

        FormClosed += (_, _) =>
        {
            _logo.Dispose();
            _appIcon.Dispose();
        };
    }

    /// <summary>程序集版本,去掉 SourceLink 可能附加的 +提交号后缀。</summary>
    private static string GetDisplayVersion()
    {
        var assembly = typeof(AboutDialog).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "未知";
        var metadataIndex = version.IndexOf('+');
        return (metadataIndex > 0 ? version[..metadataIndex] : version).Trim();
    }

    private static void AddInfoRow(TableLayoutPanel panel, string name, Control value)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(
            new Label
            {
                Text = name,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 3, 16, 3)
            },
            0,
            row);
        panel.Controls.Add(value, 1, row);
    }

    private void OnProjectLinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(
                this,
                $"无法打开链接:{ProjectUrl}",
                "关于",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
