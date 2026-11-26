using System.Text;

namespace Niskala.Repair.Output;

public readonly record struct ConsoleLine(string Text, bool IsFinal);

internal sealed class ConsoleLineAssembler
{
    private readonly StringBuilder _line = new();
    private bool _returned;
    private bool _changed;

    public void Append(ReadOnlySpan<char> text, List<ConsoleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\n':
                    lines.Add(new ConsoleLine(_line.ToString(), IsFinal: true));
                    _line.Clear();
                    _returned = false;
                    _changed = false;
                    break;
                case '\r':
                    _returned = true;
                    break;
                case '\0':
                    break;
                default:
                    if (_returned)
                    {
                        if (_changed)
                        {
                            lines.Add(new ConsoleLine(_line.ToString(), IsFinal: false));
                        }

                        _line.Clear();
                        _returned = false;
                    }

                    _line.Append(c);
                    _changed = true;
                    break;
            }
        }

        if (_changed)
        {
            lines.Add(new ConsoleLine(_line.ToString(), IsFinal: false));
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
        _line.Clear();
        _returned = false;
        _changed = false;
        return last;
    }
}
