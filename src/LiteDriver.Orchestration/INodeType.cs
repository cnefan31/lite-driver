namespace LiteDriver.Orchestration;

/// <summary>
/// 节点类型描述：声明该种类的端口、可配置字段与运行语义。
/// <para>
/// 编排的「可连接性」完全由端口类型决定，而不是在编译器里写 <c>if kind == Client</c>。
/// 新增一种节点只需实现它并注册，编译器与前端自动适配。
/// </para>
/// </summary>
public interface INodeType
{
    /// <summary>节点类型键，配置里的 <c>nodeType</c>；稳定字符串，不是 CLR 类型名。</summary>
    string TypeId { get; }

    /// <summary>界面显示名。</summary>
    string DisplayName { get; }

    /// <summary>一句话说明用途。</summary>
    string Description { get; }

    /// <summary>是否可作为链路起点（出站侧）。</summary>
    bool IsSource { get; }

    /// <summary>
    /// 是否为纯布局节点：没有端口、不参与编译与运行，仅用于撑开走线空间。
    /// 默认 <c>false</c>。
    /// </summary>
    bool IsLayoutOnly => false;

    /// <summary>该类型声明的端口。</summary>
    IReadOnlyList<PortSpec> Ports { get; }

    /// <summary>该类型可编辑的字段。</summary>
    IReadOnlyList<NodeField> Fields { get; }

    /// <summary>
    /// 校验该节点自身的配置。返回 <c>null</c> 表示通过。
    /// 链路级校验（端口必连、环路、孤立）由编译器统一处理。
    /// </summary>
    string? Validate(GraphNode node, INodeTypeCatalogView catalog);
}

/// <summary>节点可编辑字段的类型。</summary>
public enum NodeFieldKind
{
    Text,
    Number,
    Bool,
    /// <summary>下拉，候选来自 <see cref="NodeField.Options"/>。</summary>
    Select,
    /// <summary>多行文本，每行一项（如端点列表）。</summary>
    Lines
}

/// <summary>
/// 节点字段声明，驱动前端属性面板。
/// <para>
/// 当 <see cref="OptionsSource"/> 非空时，候选值来自后端注册表（如 kind/codec/handler），
/// 前端据此渲染下拉框，避免用户手填出错误的键名。
/// </para>
/// </summary>
public sealed record NodeField(
    string Key,
    string Label,
    NodeFieldKind Kind,
    string? DefaultValue = null,
    string Help = "",
    IReadOnlyList<NodeFieldOption>? Options = null,
    string? OptionsSource = null);

/// <summary>下拉字段的一个候选。</summary>
public sealed record NodeFieldOption(string Value, string Label);

/// <summary>节点类型目录。宿主注册哪些类型，编排页面就出现哪些。</summary>
public sealed class NodeTypeCatalog
{
    private readonly Dictionary<string, INodeType> _types = new(StringComparer.Ordinal);

    /// <summary>已注册的类型。</summary>
    public IReadOnlyCollection<INodeType> Types => _types.Values;

    /// <summary>注册一种节点类型。</summary>
    public NodeTypeCatalog Register(INodeType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (string.IsNullOrWhiteSpace(type.TypeId))
        {
            throw new ArgumentException("TypeId 不能为空", nameof(type));
        }

        if (!_types.TryAdd(type.TypeId, type))
        {
            throw new ArgumentException($"节点类型 '{type.TypeId}' 已注册");
        }

        return this;
    }

    /// <summary>按 TypeId 查找。</summary>
    public INodeType? Find(string typeId) =>
        _types.TryGetValue(typeId, out var t) ? t : null;

    /// <summary>判断某类型是否存在。</summary>
    public bool Contains(string typeId) => _types.ContainsKey(typeId);
}
