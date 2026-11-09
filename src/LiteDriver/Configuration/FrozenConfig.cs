using System.Text.Json;

namespace LiteDriver.Configuration;

/// <summary>按 kindId 校验该种类的私有参数。返回 <c>null</c> 表示通过。</summary>
internal delegate string? KindValidator(string kindId, ReadOnlyMemory<byte> settings);

/// <summary>冻结后的设备配置。所有集合只读，进入内核后不再变化。</summary>
internal sealed record FrozenDevice(
    string DeviceId,
    string KindId,
    FrozenEndpoint[] Endpoints,
    FrozenRetry Retry,
    FrozenHeartbeat Heartbeat,
    FrozenHandshake Handshake,
    JsonElement? KindSettings)
{
    public string BindingKey { get; } = DeviceId + "\u0000" + KindId + "\u0000" +
        string.Join("\u0001", Array.ConvertAll(Endpoints, static e => e.Host + ":" + e.Port));

    /// <summary>无需重建 socket 的“软”参数是否等价：端点不变，仅超时/心跳/退避变化。</summary>
    public bool SameTransport(FrozenDevice other) =>
        KindId == other.KindId &&
        Endpoints.Length == other.Endpoints.Length &&
        BindingKey == other.BindingKey &&
        string.Equals(KindSettingsText, other.KindSettingsText, StringComparison.Ordinal);

    public string KindSettingsText { get; } = KindSettings?.GetRawText() ?? "";
}

internal sealed record FrozenEndpoint(string EndpointId, string Host, int Port, TimeSpan Settle);

internal sealed record FrozenRetry(TimeSpan Initial, TimeSpan Max, double Jitter, TimeSpan ConnectTimeout, TimeSpan EndpointCooldown)
{
    public TimeSpan Backoff(int attempt)
    {
        var exponent = Math.Min(attempt, 24);
        var baseMs = Initial.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(baseMs, Max.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }
}

internal sealed record FrozenHeartbeat(bool Enabled, TimeSpan Interval, int Misses, TimeSpan Silent, TimeSpan ResponseTimeout);

internal sealed record FrozenHandshake(TimeSpan Timeout);

/// <summary>冻结后的监听配置。</summary>
internal sealed record FrozenListener(
    string ListenerId,
    string KindId,
    string Host,
    int Port,
    int Backlog,
    int MaxSessions,
    FrozenHandshake Handshake,
    FrozenHeartbeat Heartbeat,
    JsonElement? KindSettings);

/// <summary>
/// 一次配置校验的全部结果。<see cref="Devices"/> 与 <see cref="Listeners"/> 只在 <see cref="Issues"/> 为空时可用。
/// </summary>
internal sealed class FrozenConfig
{
    private FrozenConfig(
        long revision,
        FrozenDevice[] devices,
        FrozenListener[] listeners,
        IReadOnlyDictionary<string, FrozenDevice> deviceMap,
        IReadOnlyDictionary<string, FrozenListener> listenerMap,
        IReadOnlyList<ApplyIssue> issues)
    {
        Revision = revision;
        Devices = devices;
        Listeners = listeners;
        DeviceMap = deviceMap;
        ListenerMap = listenerMap;
        Issues = issues;
    }

    public long Revision { get; }

    public FrozenDevice[] Devices { get; }

    public FrozenListener[] Listeners { get; }

    public IReadOnlyDictionary<string, FrozenDevice> DeviceMap { get; }

    public IReadOnlyDictionary<string, FrozenListener> ListenerMap { get; }

    public IReadOnlyList<ApplyIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;

    public static FrozenConfig Empty { get; } = new(
        0, [], [], new Dictionary<string, FrozenDevice>(0), new Dictionary<string, FrozenListener>(0), []);

    /// <summary>
    /// 校验并冻结配置。所有失败在占用端口、拨号之前暴露，不在首次收包时才失败。
    /// <paramref name="knownKinds"/> 为启动期冻结的注册表；未知 kindId 一律拒绝。
    /// <paramref name="kindValidator"/> 让设备种类校验自己的固定扩展键。
    /// </summary>
    public static FrozenConfig Create(
        DriverConfig source,
        long revision,
        IReadOnlyCollection<string> knownKinds,
        DriverLimits limits,
        KindValidator kindValidator)
    {
        var issues = new List<ApplyIssue>();
        var devices = new List<FrozenDevice>(source.Devices.Length);
        var listeners = new List<FrozenListener>(source.Listeners.Length);
        var deviceMap = new Dictionary<string, FrozenDevice>(StringComparer.Ordinal);
        var listenerMap = new Dictionary<string, FrozenListener>(StringComparer.Ordinal);
        var consumedPorts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var d in source.Devices)
        {
            var id = (d.DeviceId ?? "").Trim();
            if (id.Length == 0)
            {
                issues.Add(new ApplyIssue("device_id_empty", "设备缺少 DeviceId"));
                continue;
            }

            if (!deviceMap.TryAdd(id, null!))
            {
                issues.Add(new ApplyIssue("device_duplicate", $"设备 '{id}' 重复定义", id));
                continue;
            }

            deviceMap.Remove(id);

            var kind = (d.KindId ?? "").Trim();
            if (kind.Length == 0)
            {
                issues.Add(new ApplyIssue("kind_id_empty", $"设备 '{id}' 缺少 KindId", id));
                continue;
            }

            if (!knownKinds.Contains(kind))
            {
                issues.Add(new ApplyIssue("kind_unknown", $"设备 '{id}' 的 KindId '{kind}' 未注册", id));
                continue;
            }

            if (d.Endpoints is null || d.Endpoints.Length == 0)
            {
                issues.Add(new ApplyIssue("endpoints_empty", $"设备 '{id}' 没有端点", id));
                continue;
            }

            var endpoints = new List<FrozenEndpoint>(d.Endpoints.Length);
            var endpointIds = new HashSet<string>(StringComparer.Ordinal);
            var bad = false;
            for (var i = 0; i < d.Endpoints.Length; i++)
            {
                var e = d.Endpoints[i];
                var host = (e.Host ?? "").Trim();
                if (host.Length == 0)
                {
                    issues.Add(new ApplyIssue("endpoint_host_empty", $"设备 '{id}' 第 {i + 1} 个端点缺少 Host", id));
                    bad = true;
                    continue;
                }

                if (e.Port is < 1 or > 65535)
                {
                    issues.Add(new ApplyIssue("endpoint_port_range", $"设备 '{id}' 端点 '{host}' 端口 {e.Port} 越界", id));
                    bad = true;
                    continue;
                }

                if (e.SettleMs is < 0 or > 60_000)
                {
                    issues.Add(new ApplyIssue("endpoint_settle_range", $"设备 '{id}' 端点 '{host}' SettleMs 越界", id));
                    bad = true;
                    continue;
                }

                var endpointId = string.IsNullOrWhiteSpace(e.EndpointId) ? $"ep{i}" : e.EndpointId!.Trim();
                if (!endpointIds.Add(endpointId))
                {
                    issues.Add(new ApplyIssue("endpoint_id_duplicate", $"设备 '{id}' 端点标识 '{endpointId}' 重复", id));
                    bad = true;
                    continue;
                }

                endpoints.Add(new FrozenEndpoint(endpointId, host, e.Port, TimeSpan.FromMilliseconds(e.SettleMs)));
            }

            var retry = FreezeRetry(d.Retry, id, issues, out var retryOk);
            var heartbeat = FreezeHeartbeat(d.Heartbeat, id, issues, out var hbOk);
            var handshake = FreezeHandshake(d.Handshake, id, issues, out var hsOk);
            if (bad || !retryOk || !hbOk || !hsOk)
            {
                continue;
            }

            var settingsBytes = d.KindSettings is { } je
                ? JsonSerializer.SerializeToUtf8Bytes(je, LiteDriverJsonContext.Default.JsonElement)
                : [];

            var validationError = kindValidator(kind, settingsBytes);
            if (validationError is not null)
            {
                issues.Add(new ApplyIssue("kind_settings_invalid", $"设备 '{id}' 的 KindSettings 非法：{validationError}", id));
                continue;
            }

            var frozen = new FrozenDevice(
                id, kind, [.. endpoints], retry, heartbeat, handshake, CloneElement(d.KindSettings));
            devices.Add(frozen);
            deviceMap[id] = frozen;
        }

        foreach (var l in source.Listeners)
        {
            var id = (l.ListenerId ?? "").Trim();
            if (id.Length == 0)
            {
                issues.Add(new ApplyIssue("listener_id_empty", "监听端点缺少 ListenerId"));
                continue;
            }

            if (listenerMap.ContainsKey(id))
            {
                issues.Add(new ApplyIssue("listener_duplicate", $"监听端点 '{id}' 重复定义", id));
                continue;
            }

            var kind = (l.KindId ?? "").Trim();
            if (kind.Length == 0)
            {
                issues.Add(new ApplyIssue("kind_id_empty", $"监听端点 '{id}' 缺少 KindId", id));
                continue;
            }

            if (!knownKinds.Contains(kind))
            {
                issues.Add(new ApplyIssue("kind_unknown", $"监听端点 '{id}' 的 KindId '{kind}' 未注册", id));
                continue;
            }

            var host = (l.Host ?? "").Trim();
            if (host.Length == 0)
            {
                issues.Add(new ApplyIssue("listener_host_empty", $"监听端点 '{id}' 缺少 Host", id));
                continue;
            }

            // 0 表示由系统分配端口，便于测试与容器环境。
            if (l.Port is < 0 or > 65535)
            {
                issues.Add(new ApplyIssue("listener_port_range", $"监听端点 '{id}' 端口 {l.Port} 越界", id));
                continue;
            }

            if (l.Backlog is < 1 or > 4096)
            {
                issues.Add(new ApplyIssue("listener_backlog_range", $"监听端点 '{id}' Backlog 越界", id));
                continue;
            }

            if (l.MaxSessions is < 1 || l.MaxSessions > limits.MaxSessionsPerListener)
            {
                issues.Add(new ApplyIssue(
                    "listener_max_sessions_range",
                    $"监听端点 '{id}' MaxSessions 必须在 1..{limits.MaxSessionsPerListener}", id));
                continue;
            }

            // 端口 0 由系统分配，彼此不会冲突，无需参与冲突检查。
            if (l.Port != 0)
            {
                var portKey = host + ":" + l.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (consumedPorts.TryGetValue(portKey, out var owner))
                {
                    issues.Add(new ApplyIssue("listener_port_conflict", $"监听端点 '{id}' 与 '{owner}' 抢占同一地址 {portKey}", id));
                    continue;
                }

                consumedPorts[portKey] = id;
            }

            var handshake = FreezeHandshake(l.Handshake, id, issues, out var hsOk);
            var heartbeat = FreezeHeartbeat(l.Heartbeat, id, issues, out var hbOk);
            if (!hsOk || !hbOk)
            {
                continue;
            }

            var settingsBytes = l.KindSettings is { } je
                ? JsonSerializer.SerializeToUtf8Bytes(je, LiteDriverJsonContext.Default.JsonElement)
                : [];
            var validationError = kindValidator(kind, settingsBytes);
            if (validationError is not null)
            {
                issues.Add(new ApplyIssue("kind_settings_invalid", $"监听端点 '{id}' 的 KindSettings 非法：{validationError}", id));
                continue;
            }

            var frozen = new FrozenListener(
                id, kind, host, l.Port, l.Backlog, l.MaxSessions, handshake, heartbeat, CloneElement(l.KindSettings));
            listeners.Add(frozen);
            listenerMap[id] = frozen;
        }

        return new FrozenConfig(revision, [.. devices], [.. listeners], deviceMap, listenerMap, issues);
    }

    private static FrozenRetry FreezeRetry(RetryOptions? o, string target, List<ApplyIssue> issues, out bool ok)
    {
        o ??= new RetryOptions();
        ok = true;
        if (o.InitialMs is < 1 or > 600_000)
        {
            issues.Add(new ApplyIssue("retry_initial_range", $"'{target}' Retry.InitialMs 越界", target));
            ok = false;
        }

        if (o.MaxMs is < 1 or > 3_600_000 || o.MaxMs < o.InitialMs)
        {
            issues.Add(new ApplyIssue("retry_max_range", $"'{target}' Retry.MaxMs 越界或小于 InitialMs", target));
            ok = false;
        }

        if (double.IsNaN(o.Jitter) || o.Jitter is < 0 or > 1)
        {
            issues.Add(new ApplyIssue("retry_jitter_range", $"'{target}' Retry.Jitter 必须在 0..1", target));
            ok = false;
        }

        if (o.ConnectTimeoutMs is < 100 or > 600_000)
        {
            issues.Add(new ApplyIssue("retry_connect_timeout_range", $"'{target}' Retry.ConnectTimeoutMs 越界", target));
            ok = false;
        }

        if (o.EndpointCooldownMs is < 0 or > 3_600_000)
        {
            issues.Add(new ApplyIssue("retry_cooldown_range", $"'{target}' Retry.EndpointCooldownMs 越界", target));
            ok = false;
        }

        return new FrozenRetry(
            TimeSpan.FromMilliseconds(o.InitialMs),
            TimeSpan.FromMilliseconds(o.MaxMs),
            o.Jitter,
            TimeSpan.FromMilliseconds(o.ConnectTimeoutMs),
            TimeSpan.FromMilliseconds(o.EndpointCooldownMs));
    }

    private static FrozenHeartbeat FreezeHeartbeat(HeartbeatOptions? o, string target, List<ApplyIssue> issues, out bool ok)
    {
        o ??= new HeartbeatOptions();
        ok = true;
        if (o.IntervalMs is < 100 or > 3_600_000)
        {
            issues.Add(new ApplyIssue("heartbeat_interval_range", $"'{target}' Heartbeat.IntervalMs 越界", target));
            ok = false;
        }

        if (o.Misses is < 1 or > 100)
        {
            issues.Add(new ApplyIssue("heartbeat_misses_range", $"'{target}' Heartbeat.Misses 必须在 1..100", target));
            ok = false;
        }

        if (o.SilentMs is < 0 or > 86_400_000)
        {
            issues.Add(new ApplyIssue("heartbeat_silent_range", $"'{target}' Heartbeat.SilentMs 越界", target));
            ok = false;
        }

        if (o.ResponseTimeoutMs is < 50 or > 600_000)
        {
            issues.Add(new ApplyIssue("heartbeat_response_timeout_range", $"'{target}' Heartbeat.ResponseTimeoutMs 越界", target));
            ok = false;
        }

        return new FrozenHeartbeat(
            o.Enabled,
            TimeSpan.FromMilliseconds(o.IntervalMs),
            o.Misses,
            TimeSpan.FromMilliseconds(o.SilentMs),
            TimeSpan.FromMilliseconds(o.ResponseTimeoutMs));
    }

    private static FrozenHandshake FreezeHandshake(HandshakeOptions? o, string target, List<ApplyIssue> issues, out bool ok)
    {
        o ??= new HandshakeOptions();
        ok = true;
        if (o.TimeoutMs is < 50 or > 600_000)
        {
            issues.Add(new ApplyIssue("handshake_timeout_range", $"'{target}' Handshake.TimeoutMs 越界", target));
            ok = false;
        }

        return new FrozenHandshake(TimeSpan.FromMilliseconds(o.TimeoutMs));
    }

    /// <summary>克隆 JsonElement，避免调用方持有原文档后修改或释放。</summary>
    private static JsonElement? CloneElement(JsonElement? source)
    {
        if (source is not { } element || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return element.Clone();
    }
}
