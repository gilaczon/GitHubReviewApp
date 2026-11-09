namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>A readable stream that hands out only a few bytes per read, as a slow connection does, so that a character of several bytes can arrive in pieces.</summary>
internal sealed class TricklingStream : Stream
{
    private readonly byte[] _bytes;
    private readonly int _bytesPerRead;
    private int _position;

    public TricklingStream(byte[] bytes, int bytesPerRead)
    {
        _bytes = bytes;
        _bytesPerRead = bytesPerRead;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        var count = Math.Min(Math.Min(buffer.Length, _bytesPerRead), _bytes.Length - _position);
        _bytes.AsSpan(_position, count).CopyTo(buffer);
        _position += count;

        return count;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
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
