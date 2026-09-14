using System;
using System.IO;
using System.Threading;

namespace TgVfsPlugin;

/// <summary>
/// Обертка над FileStream, блокирующая запись при установленной паузе.
/// Когда поток скачивания Telegram пытается записать очередной блок данных,
/// он засыпает на pauseGate. При этом TCP-буферы сокета заполняются,
/// размер окна TCP схлопывается до 0 и передача данных по сети физически прекращается.
/// </summary>
public class PausableStream : Stream
{
    private readonly Stream _baseStream;
    private readonly ManualResetEventSlim _pauseGate;
    private readonly CancellationToken _cancellationToken;

    public PausableStream(Stream baseStream, ManualResetEventSlim pauseGate, CancellationToken cancellationToken = default)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _pauseGate = pauseGate ?? throw new ArgumentNullException(nameof(pauseGate));
        _cancellationToken = cancellationToken;
    }

    private void WaitIfNotPaused()
    {
        if (!_pauseGate.IsSet)
        {
            Logger.Log("PausableStream: Stream operation paused by gate. Waiting for unpause...");
            _pauseGate.Wait(_cancellationToken);
            Logger.Log("PausableStream: Stream operation unpaused. Continuing data transfer.");
        }
    }

    public override bool CanRead => _baseStream.CanRead;
    public override bool CanSeek => _baseStream.CanSeek;
    public override bool CanWrite => _baseStream.CanWrite;
    public override long Length => _baseStream.Length;

    public override long Position
    {
        get => _baseStream.Position;
        set => _baseStream.Position = value;
    }

    public override void Flush()
    {
        WaitIfNotPaused();
        _baseStream.Flush();
    }

    public override System.Threading.Tasks.Task FlushAsync(CancellationToken cancellationToken)
    {
        WaitIfNotPaused();
        return _baseStream.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        WaitIfNotPaused();
        return _baseStream.Read(buffer, offset, count);
    }

    public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        WaitIfNotPaused();
        return _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WaitIfNotPaused();
        return _baseStream.ReadAsync(buffer, cancellationToken);
    }

    public override int Read(Span<byte> buffer)
    {
        WaitIfNotPaused();
        return _baseStream.Read(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _baseStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        WaitIfNotPaused();
        _baseStream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WaitIfNotPaused();
        _baseStream.Write(buffer, offset, count);
    }

    public override System.Threading.Tasks.Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        WaitIfNotPaused();
        return _baseStream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override System.Threading.Tasks.ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WaitIfNotPaused();
        return _baseStream.WriteAsync(buffer, cancellationToken);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WaitIfNotPaused();
        _baseStream.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        WaitIfNotPaused();
        _baseStream.WriteByte(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _baseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
