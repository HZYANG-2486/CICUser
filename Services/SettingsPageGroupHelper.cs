using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace CICUser.Services;

/// <summary>
/// 设置页分组注册辅助。
/// </summary>
/// <remarks>
/// 插件编译期引用的 ClassIsland.Core（SDK 1.7.106.2-dev-v2）尚未包含
/// <c>AddSettingsPageGroup</c> 与 <c>GroupAttribute.GroupId</c>，而运行时宿主
/// （2.1.1.1）已经提供。为了让插件既能编译通过、又能在新宿主上形成
/// 左侧导航的两级分组树，这里用反射调用宿主的 API：
/// <list type="bullet">
/// <item>宿主支持分组 → 注册分组并把页面归入其中，左侧导航显示为可展开的树</item>
/// <item>宿主不支持 → 静默跳过，页面以平铺方式显示，不影响功能</item>
/// </list>
/// </remarks>
internal static class SettingsPageGroupHelper
{
    /// <summary>
    /// 尝试注册设置页分组。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="groupId">分组 ID。</param>
    /// <param name="iconGlyph">分组图标 Glyph。</param>
    /// <param name="name">分组显示名。</param>
    /// <returns>宿主是否支持并成功注册分组。</returns>
    public static bool TryAddGroup(IServiceCollection services, string groupId,
        string iconGlyph, string name)
    {
        try
        {
            // 定位 AddSettingsPageGroup(string, string, string) 扩展方法
            var method = FindAddGroupMethod();
            if (method == null)
            {
                return false;
            }

            method.Invoke(null, new object?[] { services, groupId, iconGlyph, name });
            Logger.Info($"已注册设置页导航分组：{groupId}");
            return true;
        }
        catch (Exception ex)
        {
            // 分组失败不应阻断插件初始化，退化为平铺显示即可
            Logger.Info($"设置页分组不可用（{ex.GetType().Name}），导航将平铺显示");
            return false;
        }
    }

    /// <summary>
    /// 尝试把设置页归入指定分组。
    /// </summary>
    /// <param name="pageType">设置页类型。</param>
    /// <param name="groupId">分组 ID。</param>
    /// <returns>宿主是否支持并成功归组。</returns>
    public static bool TryAssignGroup(Type pageType, string groupId)
    {
        try
        {
            // GroupId 在宿主中是 internal set，需要通过注册表服务写入
            var registryType = FindType("ClassIsland.Core.Services.Registry.SettingsWindowRegistryService");
            if (registryType == null)
            {
                return false;
            }

            var registered = registryType
                .GetProperty("Registered", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as System.Collections.IEnumerable;

            if (registered == null)
            {
                return false;
            }

            // 通过页面类上的 SettingsPageInfo 取得页 ID。
            // 注意：CLR 对特性的 GetCustomAttributes 每次可能返回内容相同但
            // 引用不同的实例，因此不能用 ReferenceEquals 匹配注册表项，
            // 必须改用稳定的 Id 字段比对。
            var pageId = pageType.GetCustomAttributes(false)
                .OfType<SettingsPageInfo>()
                .FirstOrDefault()?.Id;

            if (string.IsNullOrEmpty(pageId))
            {
                return false;
            }

            // GroupId 在宿主中是 internal set，公开反射拿不到 setter，
            // 必须显式取非公开 setter 才能写入。
            var groupIdProp = typeof(SettingsPageInfo).GetProperty(
                "GroupId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            var setter = groupIdProp?.GetSetMethod(nonPublic: true);

            if (setter == null)
            {
                return false;
            }

            foreach (var item in registered)
            {
                if (item is not SettingsPageInfo info || info.Id != pageId)
                {
                    continue;
                }

                setter.Invoke(info, new object?[] { groupId });
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Info($"设置页归组失败（{ex.GetType().Name}），导航将平铺显示");
            return false;
        }
    }

    /// <summary>在已加载程序集中查找注册分组用的扩展方法。</summary>
    private static MethodInfo? FindAddGroupMethod()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? extensions;
            try
            {
                extensions = assembly.GetType(
                    "ClassIsland.Core.Extensions.Registry.SettingsWindowRegistryExtensions", false);
            }
            catch
            {
                continue;
            }

            if (extensions == null)
            {
                continue;
            }

            var method = extensions
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "AddSettingsPageGroup" &&
                    m.GetParameters() is { Length: 4 } p &&
                    p[1].ParameterType == typeof(string) &&
                    p[2].ParameterType == typeof(string) &&
                    p[3].ParameterType == typeof(string));

            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    /// <summary>在所有已加载程序集中按全名查找类型。</summary>
    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
                // 个别程序集加载异常时跳过
            }
        }

        return null;
    }
}
