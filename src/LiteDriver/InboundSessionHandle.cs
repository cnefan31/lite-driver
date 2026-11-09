using System.Threading.Channels;
using LiteDriver.Sessions;

namespace LiteDriver;

/// <summary>从入站会话收到的一帧。缓冲区从内存池租用，<see cref="Dispose"/> 后归还。</summary>
public sealed class PushFrame : IDisposable
{
    private readonly InboundFrame _inner;

    internal PushFrame(InboundFrame inner) => _inner = inner;

    /// <summary>帧载荷（含该协议的帧头，由协议栈定义）。</summary>
    public ReadOnlyMemory<byte> Payload => _inner.Memory;

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// 一个入站会话（设备主动连入本端）。用于读取设备上报的帧，并向该会话回写。
/// 会话结束后句柄失效。
/// </summary>
public readonly struct InboundSessionHandle
{
    private readonly SessionRunner? _runner;

    internal InboundSessionHandle(SessionRunner runner) => _runner = runner;

    public bool IsValid => _runner is not null;

    public ConnectionId ConnectionId => _runner?.Context.ConnectionId ?? default;

    public long Epoch => _runner?.Context.Epoch ?? 0;

    public string? ListenerId => _runner?.Context.ListenerId;

    /// <summary>读取下一帧上报数据；会话结束时返回 <c>null</c>。</summary>
    public async ValueTask<PushFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_runner is null)
        {
            return null;
        }

        try
        {
            var frame = await _runner.PushReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new PushFrame(frame);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>向该入站会话写一帧。</summary>
    public ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (_runner is null)
        {
            throw new InvalidOperationException("入站会话句柄已失效");
        }

        if (!_runner.TryEnqueue(payload.Span, out var sent))
        {
            throw new InvalidOperationException("入站会话写队列已满或已关闭");
        }

        return sent is null
            ? ValueTask.CompletedTask
            : new ValueTask(sent.Task.WaitAsync(cancellationToken));
    }

    public override string ToString() => $"inbound {ListenerId} conn={ConnectionId} epoch={Epoch}";
}
