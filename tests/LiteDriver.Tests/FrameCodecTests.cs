using System.Buffers;
using System.Text;
using LiteDriver.Protocol;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>帧编解码边界：粘包、半包、超限、终止符。</summary>
public sealed class FrameCodecTests
{
    [Fact]
    public void LengthPrefix_ShouldSplitCoalescedFrames()
    {
        var decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, 1024);
        var encoder = new LengthPrefixFrameEncoder(2, bigEndian: true, lengthIncludesHeader: false);

        var buffer = new ArrayBufferWriter<byte>();
        encoder.Encode("AAA"u8, buffer);
        encoder.Encode("BB"u8, buffer);
        var stream = buffer.WrittenSpan.ToArray();

        var consumed = 0;
        var frames = new List<string>();
        var offset = 0;
        while (offset < stream.Length)
        {
            var result = decoder.Decode(stream.AsSpan(offset), out var used, out var frame);
            if (result != FrameDecodeResult.Frame)
            {
                break;
            }

            // 跳过 2 字节长度前缀。
            frames.Add(Encoding.ASCII.GetString(frame[2..]));
            offset += used;
            consumed += used;
        }

        Assert.Equal(stream.Length, consumed);
        Assert.Equal(["AAA", "BB"], frames);
    }

    [Fact]
    public void LengthPrefix_ShouldWaitForHalfFrame()
    {
        var decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, 1024);

        // 声明 5 字节长度，但只来 3 字节载荷。
        var partial = new byte[] { 0, 5, (byte)'a', (byte)'b', (byte)'c' };
        var result = decoder.Decode(partial, out var consumed, out _);

        Assert.Equal(FrameDecodeResult.NeedMore, result);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void LengthPrefix_ShouldBeFatalWhenDeclaredLengthExceedsLimit()
    {
        var decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, 64);
        var oversized = new byte[] { 0xFF, 0xFF, 0x00 };

        var result = decoder.Decode(oversized, out _, out _);

        Assert.Equal(FrameDecodeResult.Fatal, result);
    }

    [Fact]
    public void Terminator_ShouldNotSwallowTerminatorWhenExcluded()
    {
        var decoder = new TerminatorFrameDecoder("\r\n"u8, includeTerminator: false, 1024);
        var input = "HELLO\r\nNEXT"u8.ToArray();

        var result = decoder.Decode(input, out var consumed, out var frame);

        Assert.Equal(FrameDecodeResult.Frame, result);
        Assert.Equal(7, consumed);
        Assert.Equal("HELLO", Encoding.ASCII.GetString(frame));
    }

    [Fact]
    public void Terminator_ShouldBeFatalWhenBufferExceedsMaxSize()
    {
        var decoder = new TerminatorFrameDecoder("\n"u8, includeTerminator: false, 8);
        var input = new byte[32];

        var result = decoder.Decode(input, out _, out _);

        Assert.Equal(FrameDecodeResult.Fatal, result);
    }

    [Fact]
    public void FixedLength_ShouldSliceExactly()
    {
        var decoder = new FixedLengthFrameDecoder(4, 64);
        var input = "AAAABBBB"u8.ToArray();

        Assert.Equal(FrameDecodeResult.Frame, decoder.Decode(input, out var c1, out var f1));
        Assert.Equal(4, c1);
        Assert.Equal("AAAA", Encoding.ASCII.GetString(f1));

        Assert.Equal(FrameDecodeResult.Frame, decoder.Decode(input.AsSpan(4), out var c2, out var f2));
        Assert.Equal(4, c2);
        Assert.Equal("BBBB", Encoding.ASCII.GetString(f2));
    }

    [Fact]
    public void DecoderFactory_ShouldRejectFrameLongerThanLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixedLengthFrameDecoder(2048, 1024));
    }
}
