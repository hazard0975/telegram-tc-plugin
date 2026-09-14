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
        _pauseGate.Wait(_cancellationToken);
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        WaitIfNotPaused();
        return _baseStream.Read(buffer, offset, count);
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
