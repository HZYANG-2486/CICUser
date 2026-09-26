using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace CICUser.Actions;

/// <summary>
/// 自动化行动：立即上报一次课堂状态与当日课表。
/// 用户可在 ClassIsland 的自动化流程中编排触发时机
/// （如上课铃响、手动点击按钮、特定时间点等）。
/// </summary>
[ActionInfo("cicuser.action.report", "上报课堂状态与课表", "\ue7ba",
    defaultGroupToMenu: "CICUser")]
public class ReportStatusAction : ActionBase
{
    /// <summary>
    /// 行动触发入口。复用插件的手动上报逻辑，
    /// 与设置页「立即上报」按钮行为完全一致。
    /// </summary>
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        Plugin.TriggerSend();
    }
}
