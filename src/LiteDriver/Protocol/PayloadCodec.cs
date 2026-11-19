using System.Buffers;
using System.Text.Json;

namespace LiteDriver.Protocol;

/// <summary>
/// 一次成功解码后的业务消息。
/// <para>
/// 刻意不做成泛型：<c>IPayloadCodec&lt;TMessage&gt;</c> 会在 AOT 下引入
/// 泛型实例化与虚方法分发问题，且配置驱动的编解码器选择本身就是运行期按字符串键的。
/// 需要强类型的实现在自己的 codec 内部完成，对外只暴露本结构。
/// </para>
/// </summary>
public readonly struct PayloadMessage
{
    private readonly object? _value;

    private PayloadMessage(object? value, string messageType) => (_value, MessageType) = (value, messageType);

    /// <summary>业务消息类型名，由 codec 自行定义，仅用于观测与路由判断。</summary>
    public string MessageType { get; }

    /// <summary>是否有实际消息对象。</summary>
    public bool HasValue => _value is not null;

    /// <summary>
    /// 取出强类型消息。调用方必须确认 <see cref="MessageType"/> 与自己的 codec 约定一致；
    /// 类型不符时抛 <see cref="InvalidCastException"/>，不做静默兜底。
    /// </summary>
    public T As<T>() where T : class =>
        _value as T ?? throw new InvalidCastException(
            $"消息类型 '{MessageType}' 不能转换为 {typeof(T).Name}");

    /// <summary>构造带消息对象的实例，供 codec 实现使用。</summary>
    public static PayloadMessage From<T>(T value, string? messageType = null) where T : class =>
        new(value, messageType ?? typeof(T).Name);

    /// <summary>空消息：表示 codec 认为该帧不需要进入业务层。</summary>
    public static PayloadMessage Empty(string messageType) => new(null, messageType);

    public override string ToString() => HasValue ? $"{MessageType}" : $"{MessageType} (empty)";
}

/// <summary>
/// 载荷编解码器：把「已分帧的完整帧」翻译成业务消息，以及反向。
/// <para>
/// 与 <see cref="IFrameDecoder"/> 的分工：分帧器只负责切出完整帧，
/// 本接口负责帧内载荷（可能含协议头）到业务消息的语义转换。
/// </para>
/// <para>
/// 实现约束：必须每条物理连接新建（可在内部保存跨帧状态，如流序号）；
/// 必须是纯 CPU 操作，不得阻塞等待 I/O。
/// </para>
/// </summary>
public interface IPayloadCodec
{
    /// <summary>编解码器标识，与配置中的 <c>codecId</c> 对应。</summary>
    string CodecId { get; }

    /// <summary>
    /// 解码一帧。<paramref name="frame"/> 是完整帧（含协议头，内容由分帧器定义），
    /// 仅在本次调用期间有效；需要跨调用保存必须复制。
    /// </summary>
    /// <returns>
    /// 解码成功返回消息；返回 <see cref="PayloadMessage.Empty"/> 表示该帧不应进入业务层
    /// （例如 codec 内部已处理的确认帧），但仍算成功。
    /// </returns>
    /// <exception cref="PayloadDecodeException">帧格式非法，无法解码。</exception>
    PayloadMessage Decode(ReadOnlySpan<byte> frame);

    /// <summary>把业务消息编码成帧载荷。写入的字节会成为 <see cref="IFrameEncoder"/> 的输入。</summary>
    void Encode(PayloadMessage message, IBufferWriter<byte> writer);
}

/// <summary>载荷解码失败。抛出即视为协议错误，当前链路会被拆除。</summary>
public sealed class PayloadDecodeException : Exception
{
    /// <summary>用说明信息构造。</summary>
    public PayloadDecodeException(string message) : base(message)
    {
    }

    /// <summary>用说明信息与内部异常构造。</summary>
    public PayloadDecodeException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// 编解码器注册表：把配置里的 <c>codecId</c> 映射到已编译的工厂委托。
/// <para>
/// <b>AOT 前提</b>：NativeAOT 禁止按类型名动态加载程序集或反射创建类型，
/// 所以「配置驱动」只能表现为「配置里填注册表键」，实现必须在启动期静态注册。
/// </para>
/// </summary>
public sealed class PayloadCodecRegistry
{
    private readonly Dictionary<string, Func<PayloadCodecContext, IPayloadCodec>> _factories =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, PayloadCodecSettingsValidator> _validators =
        new(StringComparer.Ordinal);

    private bool _frozen;

    /// <summary>已注册的 codecId。</summary>
    public IReadOnlyCollection<string> Keys => _factories.Keys;

    /// <summary>
    /// 注册编解码器工厂。<paramref name="codecId"/> 是配置里的稳定字符串键，
    /// <b>不是 CLR 类型名</b>；工厂必须静态可分析。
    /// </summary>
    public PayloadCodecRegistry Register(
        string codecId,
        Func<PayloadCodecContext, IPayloadCodec> factory,
        PayloadCodecSettingsValidator? settingsValidator = null)
    {
        if (_frozen)
        {
            throw new InvalidOperationException("编解码器注册表已冻结，不允许再注册。");
        }

        if (string.IsNullOrWhiteSpace(codecId))
        {
            throw new ArgumentException("codecId 不能为空", nameof(codecId));
        }

        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryAdd(codecId, factory))
        {
            throw new ArgumentException($"codecId '{codecId}' 已注册", nameof(codecId));
        }

        if (settingsValidator is not null)
        {
            _validators[codecId] = settingsValidator;
        }

        return this;
    }

    /// <summary>冻结注册表。由 <c>LiteDriverBuilder.Build()</c> 调用。</summary>
    internal void Freeze() => _frozen = true;

    internal bool Contains(string codecId) => _factories.ContainsKey(codecId);

    /// <summary>
    /// 按 codecId 创建编解码器实例。返回 <c>null</c> 表示 codecId 未注册
    /// （调用方应已在配置校验阶段拦下）。
    /// </summary>
    internal IPayloadCodec? Create(string codecId, in PayloadCodecContext context) =>
        _factories.TryGetValue(codecId, out var factory) ? factory(context) : null;

    /// <summary>让 codec 校验自己的私有参数。</summary>
    internal string? ValidateSettings(string codecId, ReadOnlyMemory<byte> settings)
    {
        var validator = _validators.TryGetValue(codecId, out var v) ? v : null;
        return validator?.Invoke(settings.Span);
    }
}

/// <summary>编解码器私有参数校验委托（span 不能作泛型实参，故用自定义委托）。</summary>
public delegate string? PayloadCodecSettingsValidator(ReadOnlySpan<byte> settings);

/// <summary>创建 payload codec 时的上下文。</summary>
public readonly struct PayloadCodecContext
{
    internal PayloadCodecContext(
        string codecId,
        string targetId,
        ConnectionDirection direction,
        DriverLimits limits,
        JsonElement? codecSettings)
    {
        CodecId = codecId;
        TargetId = targetId;
        Direction = direction;
        Limits = limits;
        CodecSettings = codecSettings;
    }

    /// <summary>codec 自己的标识。</summary>
    public string CodecId { get; }

    /// <summary>出站时是 DeviceId，入站时是 ListenerId。</summary>
    public string TargetId { get; }

    public ConnectionDirection Direction { get; }

    /// <summary>已冻结的资源上限。</summary>
    public DriverLimits Limits { get; }

    /// <summary>codec 私有参数，已 Clone，可安全长期持有。</summary>
    public JsonElement? CodecSettings { get; }
}
