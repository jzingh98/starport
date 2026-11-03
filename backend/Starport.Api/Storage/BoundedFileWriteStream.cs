namespace Starport.Api.Storage;

/// <summary>Prevents an unexpectedly large trusted-storage response from filling scratch space.</summary>
public sealed class BoundedFileWriteStream(FileStream file, long maxBytes) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => file.CanSeek;
    public override bool CanWrite => file.CanWrite;
    public override long Length => file.Length;
    public override long Position { get => file.Position; set => file.Position = value; }
    public override void Flush() => file.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        file.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);
    public override void SetLength(long value)
    {
        if (value > maxBytes) throw new InvalidDataException("The processor output is too large.");
        file.SetLength(value);
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(count);
        file.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(buffer.Length);
        file.Write(buffer);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        Check(count);
        return file.WriteAsync(buffer, offset, count, cancellationToken);
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Check(buffer.Length);
        return file.WriteAsync(buffer, cancellationToken);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) file.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await file.DisposeAsync();
        await base.DisposeAsync();
    }
    private void Check(int bytes)
    {
        if (file.Position > maxBytes - bytes)
            throw new InvalidDataException("The processor output is too large.");
    }
}
