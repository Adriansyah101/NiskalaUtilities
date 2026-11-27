using System.Text;

namespace Niskala.Repair.Output;

public readonly record struct ConsoleLine(string Text, bool IsFinal, bool IsOpen = false);

internal sealed class ConsoleLineAssembler
{
    private readonly StringBuilder _line = new();
    private bool _returned;
    private bool _changed;
    private bool _open;

    public void Append(ReadOnlySpan<char> text, List<ConsoleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\n':
                    lines.Add(new ConsoleLine(_line.ToString(), IsFinal: true));
                    Reset();
                    break;
                case '\r':
                    _returned = true;
                    break;
                case '\0':
                    break;
                default:
                    if (_returned)
                    {
                        if (_changed || _open)
                        {
                            lines.Add(new ConsoleLine(_line.ToString(), IsFinal: false));
                        }

                        Reset();
                    }

                    _line.Append(c);
                    _changed = true;
                    break;
            }
        }

        if (_changed || (_open && _returned))
        {
            lines.Add(new ConsoleLine(_line.ToString(), IsFinal: false, IsOpen: !_returned));
            _open = !_returned;
            _changed = false;
        }
    }

    public ConsoleLine? Complete()
    {
        if (_line.Length == 0)
        {
            return null;
        }

        var last = new ConsoleLine(_line.ToString(), IsFinal: true);
        Reset();
        return last;
    }

    private void Reset()
    {
        _line.Clear();
        _returned = false;
        _changed = false;
        _open = false;
    }
}
