using System.Text.Json;
using LiteDriver.Configuration;
using LiteDriver.Protocol;

namespace LiteDriver;

/// <summary>
/// 设备种类私有参数校验委托。返回 <c>null</c> 表示通过，否则返回给用户看的错误说明。
/// 用自定义委托而不是 <c>Func&lt;ReadOnlySpan&lt;byte&gt;, string?&gt;</c>，因为 span 不能作泛型实参。
/// </summary>
public delegate string? KindSettingsValidator(ReadOnlySpan<byte> settings);

/// <summary>设备种类注册表。键是稳定字符串，<b>不是 CLR 类型名</b>；启动后冻结，不可再注册。</summary>
internal sealed class KindRegistry
{
    private readonly Dictionary<string, Registration> _map = new(StringComparer.Ordinal);
    private bool _frozen;

    internal sealed record Registration(
        Func<ProtocolContext, IDeviceKind> Factory,
        KindSettingsValidator? SettingsValidator);

    internal void Register(
        string kindId,
        Func<ProtocolContext, IDeviceKind> factory,
        KindSettingsValidator? settingsValidator)
    {
        if (_frozen)
        {
            throw new InvalidOperationException("注册表已冻结：Builder.Build() 之后不允许再注册设备种类。");
        }

        if (string.IsNullOrWhiteSpace(kindId))
        {
            throw new ArgumentException("kindId 不能为空", nameof(kindId));
        }

        ArgumentNullException.ThrowIfNull(factory);
        if (!_map.TryAdd(kindId, new Registration(factory, settingsValidator)))
        {
            throw new ArgumentException($"kindId '{kindId}' 已注册", nameof(kindId));
        }
    }

    internal void Freeze() => _frozen = true;

    internal bool Contains(string kindId) => _map.ContainsKey(kindId);

    internal IReadOnlyCollection<string> Keys => _map.Keys;

    /// <summary>
    /// 为一条新物理连接创建协议栈。每次调用都走工厂委托，返回全新实例；
    /// 绝不缓存 kind 实例或跨连接复用有状态对象。
    /// </summary>
    internal ISessionProtocol CreateProtocol(string kindId, in ProtocolContext context)
    {
        if (!_map.TryGetValue(kindId, out var registration))
        {
            throw new InvalidOperationException($"kindId '{kindId}' 未注册");
        }

        var kind = registration.Factory(context);
        return kind.CreateProtocol(context);
    }

    /// <summary>让种类实现校验自己的固定扩展键。返回 <c>null</c> 表示通过。</summary>
    internal string? ValidateSettings(string kindId, ReadOnlyMemory<byte> settings)
    {
        if (!_map.TryGetValue(kindId, out var registration) || registration.SettingsValidator is null)
        {
            return null;
        }

        return registration.SettingsValidator(settings.Span);
    }
}

/// <summary>读取 <c>kindSettings</c> 固定键的小工具，供种类实现校验与解析参数。</summary>
public static class KindSettingsReader
{
    /// <summary>从 <c>kindSettings</c> 中读取一个必填的正整数。</summary>
    public static string? RequirePositiveInt(ReadOnlySpan<byte> settings, string property, out int value)
    {
        value = 0;
        if (settings.IsEmpty)
        {
            return $"缺少 kindSettings.{property}";
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settings.ToArray());
        }
        catch (JsonException ex)
        {
            return "kindSettings 不是合法 JSON：" + ex.Message;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty(property, out var element))
            {
                return $"缺少 kindSettings.{property}";
            }

            if (!element.TryGetInt32(out value) || value <= 0)
            {
                return $"kindSettings.{property} 必须是正整数";
            }

            return null;
        }
    }

    /// <summary>从 <c>kindSettings</c> 中读取一个可选整数。</summary>
    public static int OptionalInt(ReadOnlySpan<byte> settings, string property, int fallback)
    {
        if (settings.IsEmpty)
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(settings.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(property, out var element) &&
                element.TryGetInt32(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            return fallback;
        }

        return fallback;
    }
}
