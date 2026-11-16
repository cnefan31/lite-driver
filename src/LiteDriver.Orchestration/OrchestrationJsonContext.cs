using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDriver.Orchestration;

/// <summary>编排库的源生成 JSON 契约。禁用反射式序列化，保证 AOT 可用。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OrchestrationGraph))]
[JsonSerializable(typeof(GraphNode))]
[JsonSerializable(typeof(GraphEdge))]
[JsonSerializable(typeof(List<GraphNode>))]
[JsonSerializable(typeof(List<GraphEdge>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ProtocolOption))]
[JsonSerializable(typeof(ProtocolOption[]))]
[JsonSerializable(typeof(ProtocolField))]
[JsonSerializable(typeof(ProtocolField[]))]
[JsonSerializable(typeof(CompileResult))]
[JsonSerializable(typeof(CompileIssue))]
[JsonSerializable(typeof(CompileIssue[]))]
[JsonSerializable(typeof(ApplyReport))]
[JsonSerializable(typeof(OrchestrationStatus))]
[JsonSerializable(typeof(NodeStatus))]
[JsonSerializable(typeof(NodeStatus[]))]
[JsonSerializable(typeof(EventEntry))]
[JsonSerializable(typeof(EventEntry[]))]
[JsonSerializable(typeof(ApiEnvelope<OrchestrationStatus>))]
[JsonSerializable(typeof(ApiEnvelope<ApplyReport>))]
[JsonSerializable(typeof(ApiEnvelope<OrchestrationGraph>))]
[JsonSerializable(typeof(ApiEnvelope<ProtocolOption[]>))]
[JsonSerializable(typeof(ApiEnvelope<ValidateEnvelope>))]
[JsonSerializable(typeof(ApiEnvelope<HealthEnvelope>))]
[JsonSerializable(typeof(ApiEnvelope))]
[JsonSerializable(typeof(ValidateEnvelope))]
[JsonSerializable(typeof(HealthEnvelope))]
[JsonSerializable(typeof(NodeKind))]
public partial class OrchestrationJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 带宽松转义的实例：中文按原字符输出，而不是 \uXXXX。
    /// 序列化统一走这个实例，页面与 API 输出才可读。
    /// </summary>
    public static OrchestrationJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = Default
    });
}

/// <summary>
/// 统一 API 响应外壳。用泛型而非 <c>object</c>，让源生成器能静态确定类型，
/// 避免 <c>object</c> 字段在 AOT 下回落到反射序列化。
/// </summary>
public sealed class ApiEnvelope<T>
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public T? Data { get; set; }

    public static ApiEnvelope<T> Success(T data) => new() { Ok = true, Data = data };
}

/// <summary>无数据响应（仅表示失败或成功）。</summary>
public sealed class ApiEnvelope
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public static ApiEnvelope Failure(string error) => new() { Ok = false, Error = error };
}

/// <summary>校验接口的响应体。</summary>
public sealed class ValidateEnvelope
{
    public bool Ok { get; set; }

    public IReadOnlyList<CompileIssue> Issues { get; set; } = [];
}

/// <summary>健康检查响应体。</summary>
public sealed class HealthEnvelope
{
    public string Status { get; set; } = "ok";

    public long Revision { get; set; }
}
