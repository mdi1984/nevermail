namespace Nevermail.Providers.Smtp;

internal readonly record struct SmtpLine(byte[] Bytes, bool TooLong);

/// <summary>Reads CRLF (or bare LF) terminated lines from a network stream.</summary>
internal sealed class SmtpLineReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    /// <summary>
    /// Returns the next line without its line ending, or null when the client closed the connection.
    /// Lines longer than <paramref name="maxLength"/> are consumed entirely but returned empty with <see cref="SmtpLine.TooLong"/> set.
    /// </summary>
    public async Task<SmtpLine?> ReadLineAsync(int maxLength, CancellationToken ct)
    {
        using var line = new MemoryStream();
        var tooLong = false;
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var chunkEnd = newline >= 0 ? newline : _end;

            if (!tooLong)
            {
                line.Write(_buffer, _start, chunkEnd - _start);
                if (line.Length > maxLength + 1)
                {
                    tooLong = true;
                    line.SetLength(0);
                }
            }

            if (newline >= 0)
            {
                _start = newline + 1;
                if (tooLong)
                {
                    return new SmtpLine([], TooLong: true);
                }
                var bytes = line.ToArray();
                return new SmtpLine(bytes.Length > 0 && bytes[^1] == '\r' ? bytes[..^1] : bytes, TooLong: false);
            }

            _start = _end = 0;
            var read = await stream.ReadAsync(_buffer, ct);
            if (read == 0)
            {
                return null;
            }
            _end = read;
        }
    }
}
