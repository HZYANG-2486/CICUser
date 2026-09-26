using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace CICUser;

/// <summary>
/// 设备ID 的生成方式。用户可在高级设置中选择，界面会显示对应说明。
/// </summary>
public enum DeviceIdSource
{
    /// <summary>硬件机器码哈希（Linux /etc/machine-id、Windows MachineGuid）。最稳定，推荐。</summary>
    MachineId,

    /// <summary>物理网卡 MAC 地址哈希。</summary>
    MacAddress,

    /// <summary>主机名哈希。同一网络内同名机器会冲突。</summary>
    MachineName,

    /// <summary>由「班级名称 + 设备名称」派生，可读性最好，适合多教室部署。</summary>
    ClassNameDerived,

    /// <summary>随机短码，绝对唯一但不可读，重装后无法复现。</summary>
    RandomGuid,

    /// <summary>用户手动修改。不属于自动生成方式。</summary>
    Manual
}

/// <summary>
/// 设备ID 生成器。从设备上提取唯一标识符并以 SHA256 浓缩成固定长度的短码，
/// 避免设备之间重复。
/// </summary>
/// <remarks>
/// 设计要点：生成结果一旦写入配置就不再重新计算。这样即使硬件标识
/// 发生变化（如更换网卡、容器重建），设备在服务端的身份也保持稳定。
/// </remarks>
public static class DeviceIdGenerator
{
    /// <summary>生成的设备ID 长度（十六进制字符数）。</summary>
    private const int IdLength = 16;

    /// <summary>可供用户选择的生成方式（不含 Manual）。</summary>
    public static readonly DeviceIdSource[] SelectableSources =
    {
        DeviceIdSource.MachineId,
        DeviceIdSource.ClassNameDerived,
        DeviceIdSource.MachineName,
        DeviceIdSource.MacAddress,
        DeviceIdSource.RandomGuid
    };

    /// <summary>生成方式的中文显示名。</summary>
    public static string GetSourceDisplayName(DeviceIdSource source) => source switch
    {
        DeviceIdSource.MachineId => "硬件机器码哈希（推荐）",
        DeviceIdSource.MacAddress => "网卡 MAC 地址哈希",
        DeviceIdSource.MachineName => "主机名哈希",
        DeviceIdSource.ClassNameDerived => "班级名称 + 设备名称派生",
        DeviceIdSource.RandomGuid => "随机短码",
        DeviceIdSource.Manual => "手动指定",
        _ => source.ToString()
    };

    /// <summary>生成方式的一句话说明，展示在界面上帮助用户理解差异。</summary>
    public static string GetSourceDescription(DeviceIdSource source) => source switch
    {
        DeviceIdSource.MachineId => "取自操作系统机器码，同一台设备重装插件后仍得到相同结果，最稳定。",
        DeviceIdSource.MacAddress => "取自第一块物理网卡。更换网卡或使用虚拟机时结果可能变化。",
        DeviceIdSource.MachineName => "取自主机名，简单直观。同一网络内存在同名机器时会冲突。",
        DeviceIdSource.ClassNameDerived => "由班级名称与设备名称拼接派生，人可读性最好，适合多教室批量部署。",
        DeviceIdSource.RandomGuid => "完全随机，保证唯一，但重装后无法复现，只能重新授权。",
        DeviceIdSource.Manual => "由用户手动填写，可能与其它设备重复，请谨慎使用。",
        _ => ""
    };

    /// <summary>
    /// 按指定方式生成设备ID。
    /// </summary>
    /// <param name="source">生成方式。</param>
    /// <param name="className">班级名称，仅「班级+设备名派生」方式使用。</param>
    /// <param name="deviceName">设备名称，仅「班级+设备名派生」方式使用。</param>
    /// <returns>生成的设备ID。</returns>
    public static string Generate(DeviceIdSource source, string className = "", string deviceName = "")
        => source switch
        {
            DeviceIdSource.MachineId => Build(ReadMachineId() ?? ReadMacAddress() ?? Environment.MachineName),
            DeviceIdSource.MacAddress => Build(ReadMacAddress() ?? Environment.MachineName),
            DeviceIdSource.MachineName => Build(Environment.MachineName),
            DeviceIdSource.ClassNameDerived => BuildClassNameDerived(className, deviceName),
            DeviceIdSource.RandomGuid => Build(Guid.NewGuid().ToString("N")),
            _ => Build(Guid.NewGuid().ToString("N"))
        };

    /// <summary>
    /// 「班级名称 + 设备名称派生」方式：生成带可读前缀的组合ID，
    /// 例如班级「709」+ 设备「709CI」→「709-709CI-A3F8C21B」。
    /// 前缀经过清洗，确保只含可安全用于标识的字符。
    /// </summary>
    private static string BuildClassNameDerived(string className, string deviceName)
    {
        var cls = Sanitize(className);
        var dev = Sanitize(deviceName);

        // 设备名常已包含班级名，重复出现时只保留一次，避免「709-709CI」
        if (!string.IsNullOrEmpty(dev) && dev.StartsWith(cls, StringComparison.OrdinalIgnoreCase))
        {
            dev = dev[cls.Length..].Trim('-', '_');
        }

        var prefix = string.Join("-", new[] { cls, dev }.Where(x => !string.IsNullOrEmpty(x)));
        var digest = Build($"{className}|{deviceName}|{ReadMachineId() ?? Environment.MachineName}");

        // 完全没有可读信息时退回纯哈希
        if (string.IsNullOrEmpty(prefix))
        {
            return digest;
        }

        // 前缀过长会拖累报文可读性，截断到 20 字符
        if (prefix.Length > 20)
        {
            prefix = prefix[..20];
        }

        return $"{prefix}-{digest[..8]}";
    }

    /// <summary>清洗字符串，移除不适合作为标识前缀的字符。</summary>
    private static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把输入以 SHA256 浓缩为固定长度的十六进制短码。
    /// 输入为空时改用随机值兜底，保证永远返回非空结果。
    /// </summary>
    private static string Build(string? raw)
    {
        var seed = string.IsNullOrWhiteSpace(raw) ? Guid.NewGuid().ToString("N") : raw.Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes)[..IdLength];
    }

    /// <summary>
    /// 读取操作系统机器码。
    /// Linux 取 /etc/machine-id，Windows 取注册表 MachineGuid。
    /// 读取失败返回 null，由调用方回退到下一个来源。
    /// </summary>
    private static string? ReadMachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var value = key?.GetValue("MachineGuid")?.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                if (File.Exists(path))
                {
                    var content = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content;
                    }
                }
            }
        }
        catch
        {
            // 读取失败交由调用方回退
        }

        return null;
    }

    /// <summary>
    /// 读取第一块物理网卡的 MAC 地址。
    /// 过滤回环、隧道以及常见的虚拟网卡，避免容器环境下取到不稳定的标识。
    /// </summary>
    private static string? ReadMacAddress()
    {
        try
        {
            var candidates = new List<(int Priority, string Mac)>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var mac = nic.GetPhysicalAddress()?.ToString();
                if (string.IsNullOrWhiteSpace(mac) || mac == "000000000000")
                {
                    continue;
                }

                // 虚拟网卡优先级最低，物理网卡优先
                var isVirtual = IsVirtualInterface(nic);
                candidates.Add((isVirtual ? 1 : 0, mac));
            }

            return candidates
                .OrderBy(x => x.Priority)
                .Select(x => x.Mac)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>判断网卡是否为虚拟网卡（依据名称与描述特征）。</summary>
    private static bool IsVirtualInterface(NetworkInterface nic)
    {
        string[] markers = { "virtual", "vethernet", "vmware", "vbox", "hyper-v", "docker", "br-", "veth", "tun", "tap" };
        var text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        return markers.Any(m => text.Contains(m, StringComparison.Ordinal));
    }
}

/// <summary>
/// 设备ID 生成方式的下拉选项包装。ToString 返回中文名，
/// 使 ComboBox 无需额外模板即可正确显示。
/// </summary>
public sealed class DeviceIdSourceOption
{
    /// <summary>对应的生成方式。</summary>
    public DeviceIdSource Value { get; init; }

    /// <summary>中文显示名。</summary>
    public string DisplayName => DeviceIdGenerator.GetSourceDisplayName(Value);

    /// <summary>供下拉框直接显示的文本。</summary>
    public override string ToString() => DisplayName;

    /// <summary>由枚举值构造选项。</summary>
    public static DeviceIdSourceOption From(DeviceIdSource value) => new() { Value = value };
}

/// <summary>
/// 校验用户手动填写的设备ID 是否可用。
/// 返回 null 表示通过，否则返回错误描述。
/// </summary>
public static class DeviceIdValidator
{
    public static string? Validate(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return "设备ID 不能为空。";
        }

        var trimmed = deviceId.Trim();
        if (trimmed.Length < 4)
        {
            return "设备ID 至少需要 4 个字符，过短容易与其它设备重复。";
        }

        if (trimmed.Length > 64)
        {
            return "设备ID 最多 64 个字符。";
        }

        if (!trimmed.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return "设备ID 只能包含字母、数字、连字符、下划线和点。";
        }

        return null;
    }
}
