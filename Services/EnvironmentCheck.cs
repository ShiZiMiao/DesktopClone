using System.Text;
using DesktopClone.Interop;

namespace DesktopClone.Services;

/// <summary>一条环境预检结论。</summary>
internal sealed record EnvironmentIssue(string Title, string Message);

/// <summary>
/// 启动前的只读环境预检。
/// 桌面分身完全依赖 Windows 自带的子会话与本机 RDP 能力,本工具只负责唤起;
/// 环境不满足时 RDP 只会报告含糊的「内部错误」,因此在这里提前给出可读结论。
/// 所有检查只读,不修改系统设置。
/// </summary>
internal static class EnvironmentCheck
{
    public static IReadOnlyList<EnvironmentIssue> Collect()
    {
        var issues = new List<EnvironmentIssue>();
        CollectRemoteDesktopHost(issues);
        CollectHelloOnly(issues);
        CollectRdpWrapper(issues);
        return issues;
    }

    public static string FormatSummary(IReadOnlyList<EnvironmentIssue> issues)
    {
        if (issues.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var issue in issues)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append('【').Append(issue.Title).Append('】').AppendLine();
            builder.Append(issue.Message);
        }

        return builder.ToString();
    }

    private static void CollectRemoteDesktopHost(List<EnvironmentIssue> issues)
    {
        if (!ChildSessionApi.IsRemoteDesktopDisabled(out var fromPolicy))
        {
            return;
        }

        // 组策略值会覆盖本机设置,来源不同,修复路径也不同。
        var suggestion = fromPolicy
            ? "该值由组策略下发,改本机注册表无效。请在 gpedit.msc 的「计算机配置 - 管理模板 - "
              + "Windows 组件 - 远程桌面服务 - 远程桌面会话主机 - 连接」中启用"
              + "「允许用户通过使用远程桌面服务进行远程连接」,或联系域管理员。"
            : "可尝试在管理员 PowerShell 中执行:\n"
              + "Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' "
              + "-Name fDenyTSConnections -Value 0 -Type DWord\n"
              + "Restart-Service TermService -Force";

        issues.Add(new EnvironmentIssue(
            "远程桌面主机已关闭",
            $"fDenyTSConnections = 1({(fromPolicy ? "组策略" : "本机设置")}),"
            + "本机 RDP 监听器不会启动,桌面分身无法建立会话。\n" + suggestion));
    }

    private static void CollectHelloOnly(List<EnvironmentIssue> issues)
    {
        if (!ChildSessionApi.IsHelloOnlySignIn())
        {
            return;
        }

        issues.Add(new EnvironmentIssue(
            "仅允许 Windows Hello 登录",
            "系统已开启「为了提高安全性,仅允许对此设备上的 Microsoft 帐户使用 Windows Hello 登录」。"
            + "此时账户没有可用的密码凭据,分身登录会一直提示凭据无效,输入任何密码都会失败。\n"
            + "可在「设置 - 帐户 - 登录选项」中关闭该选项,然后注销并重新登录(部分情况需重启)。"));
    }

    private static void CollectRdpWrapper(List<EnvironmentIssue> issues)
    {
        if (!ChildSessionApi.IsRdpWrapperInstalled())
        {
            return;
        }

        issues.Add(new EnvironmentIssue(
            "检测到 RDP Wrapper",
            "TermService 的 ServiceDll 已被 RDP Wrapper(rdpwrap.dll)接管。"
            + "该工具与本工具依赖的子会话机制互斥,同时启用会导致连接失败。\n"
            + "请先卸载 RDP Wrapper 还原 TermService(执行其安装目录下的 uninstall 脚本,或还原 ServiceDll 注册表值并重启 TermService)。"));
    }
}
