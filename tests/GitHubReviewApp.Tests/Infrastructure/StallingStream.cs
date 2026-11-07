namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>A readable stream that returns some bytes and then waits, forever or until the reader's token is cancelled, like a connection that has gone quiet part way through a reply.</summary>
internal sealed class StallingStream : Stream
{
    private readonly byte[] _firstBytes;
    private readonly TaskCompletionSource? _stalled;
    private int _position;

    /// <param name="firstBytes">What the stream returns before it goes quiet.</param>
    /// <param name="stalled">Completed when the reader has taken the first bytes and is waiting for more, so a test can act once the stall has begun.</param>
    public StallingStream(byte[] firstBytes, TaskCompletionSource? stalled = null)
    {
        _firstBytes = firstBytes;
        _stalled = stalled;
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

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _firstBytes.Length)
        {
            var count = Math.Min(buffer.Length, _firstBytes.Length - _position);
            _firstBytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;

            return count;
        }

        _stalled?.TrySetResult();
        await Stall.UntilCancelledAsync(cancellationToken);

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
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
