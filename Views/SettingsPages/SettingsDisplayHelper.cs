using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Shared;
using ClassIsland.Core.Abstractions.Services;

namespace CICUser.Views.SettingsPages;

/// <summary>
/// 设置页共用的界面辅助方法。主设置页与高级设置页共用同一套
/// 提示条、授权回填与导航逻辑，避免两处实现产生分歧。
/// </summary>
internal static class SettingsDisplayHelper
{
    /// <summary>提示条自动隐藏的延时。</summary>
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 在底部提示条显示一条消息，并在一段时间后自动隐藏。
    /// </summary>
    public static void ShowMessage(Border border, TextBlock text, string message, bool isError)
    {
        border.IsVisible = true;
        text.Text = message;

        var color = isError ? "#7F1D1D" : "#14532D";
        var background = isError ? "#FEE2E2" : "#DCFCE7";
        text.Foreground = new SolidColorBrush(Color.Parse(color));
        border.Background = new SolidColorBrush(Color.Parse(background));

        var timer = new DispatcherTimer { Interval = MessageTimeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            border.IsVisible = false;
        };
        timer.Start();
    }

    /// <summary>
    /// 把授权接口的返回结果回填到配置，并在界面上给出提示。
    /// </summary>
    public static void ApplyAuthResponse(PluginSettings settings, ApiResponse response,
        Action<string, bool> notify)
    {
        if (response.Code == 0)
        {
            settings.AuthStatus = string.IsNullOrWhiteSpace(response.Status)
                ? "authorized"
                : response.Status;
            settings.AuthMessage = response.Message;
            settings.Save();
            Plugin.ReloadSettings();

            if (settings.AuthStatus == "authorized")
            {
                notify("授权成功，已开始上报数据。", false);
                Plugin.TriggerSend();
            }
            else
            {
                notify($"服务端返回状态：{settings.AuthStatus}。{response.Message}", false);
            }
        }
        else if (response.Code == 403)
        {
            settings.AuthStatus = "rejected";
            settings.AuthMessage = response.Message;
            settings.Save();
            notify($"授权被拒绝：{response.Message}", true);
        }
        else
        {
            notify($"授权请求失败：{response.Message}", true);
        }
    }

    /// <summary>
    /// 通过 Uri 导航跳转到同一插件的另一个设置页。
    /// </summary>
    public static void NavigateToPage(string pageId, Action<string, bool> notify)
    {
        try
        {
            var service = IAppHost.TryGetService<IUriNavigationService>();
            if (service == null)
            {
                notify("当前环境不支持页面跳转，请在左侧导航中打开目标页面。", true);
                return;
            }

            service.NavigateWrapped(new Uri($"classisland://app/settings/{pageId}"));
        }
        catch (Exception ex)
        {
            notify($"无法跳转：{ex.Message}", true);
        }
    }

    /// <summary>
    /// 用系统默认程序打开一个文件或目录。
    /// </summary>
    public static void OpenPath(string path, string missingMessage, Action<string, bool> notify)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                notify(missingMessage, true);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            notify($"无法打开：{ex.Message}", true);
        }
    }

    /// <summary>
    /// 在文件管理器中定位到指定文件所在目录。
    /// </summary>
    public static void OpenContainingFolder(string filePath, Action<string, bool> notify)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir))
            {
                notify("无法确定配置目录位置。", true);
                return;
            }

            Directory.CreateDirectory(dir);
            OpenPath(dir, "配置目录不存在。", notify);
        }
        catch (Exception ex)
        {
            notify($"无法打开配置目录：{ex.Message}", true);
        }
    }

    /// <summary>
    /// 把文本写入系统剪贴板。使用 Avalonia 自带的剪贴板接口，
    /// 不依赖宿主是否提供相关服务。
    /// </summary>
    /// <param name="owner">用于获取 TopLevel 的控件。</param>
    /// <param name="text">要写入的文本。</param>
    /// <returns>写入成功返回 true。</returns>
    public static bool TrySetClipboardText(Visual owner, string text)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(owner);
            if (topLevel?.Clipboard == null)
            {
                return false;
            }

            // 剪贴板写入是异步的，这里只关心是否成功发起，不阻塞 UI 线程
            _ = topLevel.Clipboard.SetTextAsync(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把配置中保存的生成方式字符串解析为枚举，非法时回落为机器码哈希。
    /// </summary>
    public static DeviceIdSource ParseSource(string? raw)
        => Enum.TryParse<DeviceIdSource>(raw, out var parsed)
            ? parsed
            : DeviceIdSource.MachineId;
}
