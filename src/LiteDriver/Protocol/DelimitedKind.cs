using System.Text.Json;

namespace LiteDriver.Protocol;

/// <summary>
/// 开箱可用的「起始/结束标记」设备种类。通过 <c>kindSettings</c> 配置标记：
/// <code>
/// "kindSettings": { "start": "02", "end": "03" }
/// "kindSettings": { "end": "ffffffff" }        // 只配结束标记
/// "kindSettings": { "start": "0x02" }          // 只配起始标记
/// </code>
/// 也可以在注册时用 <see cref="CreateFactory"/> 固定标记，忽略配置文件。
/// </summary>
public sealed class DelimitedKind : IDeviceKind
{
    /// <summary>配置键：起始标记（十六进制）。</summary>
    public const string StartKey = "start";

    /// <summary>配置键：结束标记（十六进制）。</summary>
    public const string EndKey = "end";

    private readonly int _maxFrameSize;
    private readonly byte[] _fixedStart;
    private readonly byte[] _fixedEnd;
    private readonly bool _useFixed;

    /// <summary>从配置读取标记。</summary>
    public DelimitedKind(in ProtocolContext context)
    {
        _maxFrameSize = context.Limits.MaxFrameSize;
        _useFixed = false;
        _fixedStart = [];
        _fixedEnd = [];
        ReadMarkers(context.KindSettings, out var start, out var end);
        MarkerStart = start;
        MarkerEnd = end;
    }

    private DelimitedKind(int maxFrameSize, byte[] start, byte[] end)
    {
        _maxFrameSize = maxFrameSize;
        _useFixed = true;
        _fixedStart = start;
        _fixedEnd = end;
        MarkerStart = start;
        MarkerEnd = end;
    }

    /// <summary>实际生效的起始标记。</summary>
    public byte[] MarkerStart { get; }

    /// <summary>实际生效的结束标记。</summary>
    public byte[] MarkerEnd { get; }

    /// <summary>
    /// 注册时固定标记的工厂：不依赖配置文件，直接在这里写死十六进制。
    /// <code>
    /// builder.RegisterKind("stx-etx", DelimitedKind.CreateFactory("02", "03"));
    /// </code>
    /// </summary>
    public static Func<ProtocolContext, IDeviceKind> CreateFactory(string? startHex, string? endHex)
    {
        if (!HexMarker.TryParse(startHex, out var start, out var err1))
        {
            throw new ArgumentException($"起始标记非法：{err1}", nameof(startHex));
        }

        if (!HexMarker.TryParse(endHex, out var end, out var err2))
        {
            throw new ArgumentException($"结束标记非法：{err2}", nameof(endHex));
        }

        if (start.Length == 0 && end.Length == 0)
        {
            throw new ArgumentException("起始标记与结束标记至少要配置一个");
        }

        return ctx => new DelimitedKind(ctx.Limits.MaxFrameSize, start, end);
    }

    public ISessionProtocol CreateProtocol(in ProtocolContext context)
    {
        var start = _useFixed ? _fixedStart : MarkerStart;
        var end = _useFixed ? _fixedEnd : MarkerEnd;
        return new DelimitedProtocol(_maxFrameSize, start, end);
    }

    private void ReadMarkers(JsonElement? settings, out byte[] start, out byte[] end)
    {
        start = [];
        end = [];

        if (settings is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty(StartKey, out var s) && s.ValueKind == JsonValueKind.String)
        {
            HexMarker.TryParse(s.GetString(), out start, out _);
        }

        if (element.TryGetProperty(EndKey, out var e) && e.ValueKind == JsonValueKind.String)
        {
            HexMarker.TryParse(e.GetString(), out end, out _);
        }
    }

    /// <summary>
    /// 供 <c>RegisterKind</c> 使用的配置校验器：把标记错误提前到配置校验阶段。
    /// </summary>
    public static string? ValidateSettings(ReadOnlySpan<byte> settings)
    {
        if (settings.IsEmpty)
        {
            return "必须提供 kindSettings.start 或 kindSettings.end（至少一个）";
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
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "kindSettings 必须是 JSON 对象";
            }

            var hasStart = false;
            var hasEnd = false;

            if (root.TryGetProperty(StartKey, out var s))
            {
                if (s.ValueKind != JsonValueKind.String)
                {
                    return "kindSettings.start 必须是字符串（十六进制）";
                }

                if (!HexMarker.TryParse(s.GetString(), out var start, out var err))
                {
                    return "kindSettings.start 非法：" + err;
                }

                hasStart = start.Length > 0;
            }

            if (root.TryGetProperty(EndKey, out var e))
            {
                if (e.ValueKind != JsonValueKind.String)
                {
                    return "kindSettings.end 必须是字符串（十六进制）";
                }

                if (!HexMarker.TryParse(e.GetString(), out var end, out var err))
                {
                    return "kindSettings.end 非法：" + err;
                }

                hasEnd = end.Length > 0;
            }

            if (!hasStart && !hasEnd)
            {
                return "起始标记与结束标记至少要配置一个非空值";
            }

            return null;
        }
    }
}

/// <summary>起始/结束标记协议栈。</summary>
public sealed class DelimitedProtocol : ISessionProtocol
{
    public DelimitedProtocol(int maxFrameSize, ReadOnlySpan<byte> start, ReadOnlySpan<byte> end)
    {
        Decoder = new DelimitedFrameDecoder(start, end, maxFrameSize);
        Encoder = new DelimitedFrameEncoder(start, end);
    }

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder { get; }

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}
