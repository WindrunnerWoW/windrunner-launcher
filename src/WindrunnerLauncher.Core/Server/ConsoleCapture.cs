using System.Text;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Retains up to <see cref="MaxChars"/> characters of console output with normalized line endings.
/// </summary>
public sealed class ConsoleCapture
{
    public const int MaxChars = 256 * 1024;

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();

    public string Text
    {
        get
        {
            lock (_gate)
                return _buffer.ToString();
        }
    }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        lock (_gate)
        {
            _buffer.Append(normalized);
            TrimUnlocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
            _buffer.Clear();
    }

    public bool Contains(string fragment, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        lock (_gate)
            return _buffer.ToString().Contains(fragment, comparison);
    }

    private void TrimUnlocked()
    {
        if (_buffer.Length <= MaxChars)
            return;
        var extra = _buffer.Length - MaxChars;
        var start = extra;
        for (var i = extra; i < _buffer.Length; i++)
        {
            if (_buffer[i] == '\n')
            {
                start = i + 1;
                break;
            }
        }

        _buffer.Remove(0, Math.Min(start, _buffer.Length));
    }
}
