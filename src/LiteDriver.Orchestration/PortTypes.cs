namespace LiteDriver.Orchestration;

/// <summary>
/// 端口数据类型。连线两端类型必须相同才允许连接，
/// 这是「客户端不能直连处理器」这类约束的唯一判定依据。
/// </summary>
public static class PortTypes
{
    /// <summary>原始字节流：连接层拿到的裸帧（含协议头）。</summary>
    public const string Frame = "IFrame";

    /// <summary>解码后的业务消息。</summary>
    public const string Message = "IMessage";

    /// <summary>
    /// 待发送的业务消息。与 <see cref="Message"/> 的值相同，但语义是「要写入设备」。
    /// 单独命名是为了让端口标签与校验提示能区分收发方向。
    /// </summary>
    public const string Outbound = "IMessage";

    /// <summary>任意类型：仅用于能接受一切的终端节点，连线时视为通配。</summary>
    public const string Any = "Any";
}

/// <summary>端口方向。</summary>
public enum PortDirection
{
    /// <summary>输入（连线的终点）。</summary>
    Input,

    /// <summary>输出（连线的起点）。</summary>
    Output
}

/// <summary>
/// 端口声明。节点通过它表达「我能接受什么、我能产出什么」。
/// </summary>
public sealed class PortSpec
{
    /// <summary>构造端口声明。</summary>
    public PortSpec(string id, string label, PortDirection direction, string dataType, bool required = false)
    {
        Id = id;
        Label = label;
        Direction = direction;
        DataType = dataType;
        Required = required;
    }

    /// <summary>端口标识，节点内唯一。</summary>
    public string Id { get; }

    /// <summary>界面显示名。</summary>
    public string Label { get; }

    public PortDirection Direction { get; }

    /// <summary>数据类型，取值见 <see cref="PortTypes"/>。</summary>
    public string DataType { get; }

    /// <summary>
    /// 是否必连。必连端口没有连线时，链路完整性校验会报错。
    /// 仅对客户端/服务端这类「链路端点」有意义。
    /// </summary>
    public bool Required { get; }

    /// <summary>两个端口类型是否兼容。任一端为 <see cref="PortTypes.Any"/> 即视为兼容。</summary>
    public static bool TypesCompatible(string from, string to) =>
        string.Equals(from, to, StringComparison.Ordinal) ||
        string.Equals(from, PortTypes.Any, StringComparison.Ordinal) ||
        string.Equals(to, PortTypes.Any, StringComparison.Ordinal);
}
