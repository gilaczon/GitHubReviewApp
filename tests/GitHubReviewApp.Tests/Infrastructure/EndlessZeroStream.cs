namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// A readable stream of zero bytes of a stated length that allocates nothing, and counts how many bytes were actually read. It lets a test
/// present a 26 MB body and prove the code under test stopped reading long before the end.
/// </summary>
internal sealed class EndlessZeroStream : Stream
{
    private readonly long _length;
    private long _position;

    public EndlessZeroStream(long length)
    {
        _length = length;
    }

    public long BytesRead => _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _length - _position);
        buffer[..count].Clear();
        _position += count;

        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
