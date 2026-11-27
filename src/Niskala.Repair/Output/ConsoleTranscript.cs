namespace Niskala.Repair.Output;

public enum TranscriptAction
{
    None,
    Append,
    Replace,
}

public readonly record struct TranscriptLine(RepairStream Stream, string Text, bool IsOpen, bool IsCommand = false);

public readonly record struct TranscriptChange(TranscriptAction Action, int Index, TranscriptLine Line, int Dropped = -1);

public sealed class ConsoleTranscript
{
    public const int DefaultCapacity = 5000;

    private const int Streams = 2;

    private readonly List<TranscriptLine> _lines = [];
    private readonly int[] _open = new int[Streams];
    private readonly int[] _progress = new int[Streams];
    private readonly string?[] _shape = new string?[Streams];
    private readonly int _capacity;

    public ConsoleTranscript(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _capacity = capacity;
        Forget();
    }

    public IReadOnlyList<TranscriptLine> Lines => _lines;

    public void Clear()
    {
        _lines.Clear();
        Forget();
    }

    public TranscriptChange Echo(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Append(new TranscriptLine(RepairStream.Output, command, IsOpen: false, IsCommand: true), -1, null);
    }

    public TranscriptChange Apply(RepairUpdate update)
    {
        int stream = update.Stream == RepairStream.Error ? 1 : 0;
        string text = update.Text ?? string.Empty;
        if (!update.IsFinal)
        {
            if (_open[stream] >= 0)
            {
                return Replace(_open[stream], new TranscriptLine(update.Stream, text, IsOpen: true));
            }

            if (text.Length == 0)
            {
                return default;
            }

            TranscriptChange opened = Append(new TranscriptLine(update.Stream, text, IsOpen: true), stream, null);
            _open[stream] = opened.Index;
            return opened;
        }

        string? shape = ProgressShape(text);
        var line = new TranscriptLine(update.Stream, text, IsOpen: false);
        if (_open[stream] >= 0)
        {
            int at = _open[stream];
            _open[stream] = -1;
            Remember(stream, shape, at);
            return Replace(at, line);
        }

        if (shape is not null && _shape[stream] == shape && _progress[stream] == _lines.Count - 1)
        {
            return Replace(_progress[stream], line);
        }

        if (text.Length == 0 && _lines.Count > 0 && _lines[^1].Text.Length == 0)
        {
            return default;
        }

        return Append(line, stream, shape);
    }

    public static string? ProgressShape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (RepairProgressParser.Scan(text).Percent is null)
        {
            return null;
        }

        return string.Concat(text.Where(c => !char.IsDigit(c) && !char.IsWhiteSpace(c) && c is not ('.' or ',' or '=' or '-' or '#')));
    }

    private TranscriptChange Append(TranscriptLine line, int stream, string? shape)
    {
        int dropped = -1;
        if (_lines.Count >= _capacity)
        {
            dropped = _lines.Count > 1 && _lines[0].IsCommand ? 1 : 0;
            _lines.RemoveAt(dropped);
            for (int i = 0; i < Streams; i++)
            {
                _open[i] = Shift(_open[i], dropped);
                _progress[i] = Shift(_progress[i], dropped);
                if (_progress[i] < 0)
                {
                    _shape[i] = null;
                }
            }
        }

        _lines.Add(line);
        int index = _lines.Count - 1;
        if (stream >= 0 && !line.IsOpen)
        {
            Remember(stream, shape, index);
        }

        return new TranscriptChange(TranscriptAction.Append, index, line, dropped);
    }

    private TranscriptChange Replace(int index, TranscriptLine line)
    {
        _lines[index] = line;
        return new TranscriptChange(TranscriptAction.Replace, index, line);
    }

    private void Remember(int stream, string? shape, int index)
    {
        _shape[stream] = shape;
        _progress[stream] = shape is null ? -1 : index;
    }

    private void Forget()
    {
        for (int i = 0; i < Streams; i++)
        {
            _open[i] = -1;
            _progress[i] = -1;
            _shape[i] = null;
        }
    }

    private static int Shift(int index, int dropped) => index < 0 || index == dropped ? -1 : index > dropped ? index - 1 : index;
}
