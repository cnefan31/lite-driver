using LiteDriver.Transport;

namespace LiteDriver;

/// <summary>
/// 单个逻辑设备的操作门面。设备被删除后该句柄失效；每次物理连接使用新的 <see cref="ConnectionId"/>。
/// </summary>
public readonly struct DeviceHandle
{
    private readonly OutboundSupervisor? _supervisor;

    internal DeviceHandle(OutboundSupervisor supervisor) => _supervisor = supervisor;

    public bool IsValid => _supervisor is not null;

    public string DeviceId => _supervisor?.DeviceId ?? "";

    public LinkState State => _supervisor?.State ?? LinkState.Stopped;

    public long Generation => _supervisor?.Generation ?? 0;

    public ConnectionId CurrentConnection => _supervisor?.Runner?.Context.ConnectionId ?? default;

    public long Epoch => _supervisor?.Runner?.Context.Epoch ?? 0;

    /// <summary>
    /// 发送一个请求并等待响应。协议帧不含 requestId 时本库强制单在途串行。
    /// 返回的 <see cref="CommandResult"/> 需 <c>Dispose</c> 以归还响应缓冲。
    /// </summary>
    public ValueTask<CommandResult> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_supervisor is null)
        {
            return ValueTask.FromResult(CommandResult.None(CommandDisposition.NotSent, "handle-invalid", default, 0));
        }

        return _supervisor.ExchangeAsync(request, timeout, cancellationToken);
    }

    /// <summary>单向发送一帧，等待其写入 socket。</summary>
    public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (_supervisor is null)
        {
            throw new InvalidOperationException("设备句柄已失效");
        }

        return _supervisor.SendAsync(frame, cancellationToken);
    }

    public override string ToString() => $"{DeviceId} ({State}) epoch={Epoch}";
}
