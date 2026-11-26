using System.Text;

namespace Niskala.Scanner.Steam;

public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _children = new(StringComparer.OrdinalIgnoreCase);

    private VdfNode(string? value) => Value = value;

    public string? Value { get; }

    public IReadOnlyDictionary<string, VdfNode> Children => _children;

    public VdfNode? this[string key] => _children.GetValueOrDefault(key);

    public string? Text(string key) => this[key]?.Value;

    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(text);
        VdfNode root = new(null);
        ReadBlock(reader, root, nested: false);
        return root;
    }

    private static void ReadBlock(Reader reader, VdfNode block, bool nested)
    {
        while (reader.Next() is { } key)
        {
            if (key.Kind == TokenKind.Close)
            {
                if (!nested)
                {
                    throw new FormatException("Unexpected '}'.");
                }

                return;
            }

            if (key.Kind != TokenKind.Text)
            {
                throw new FormatException("Expected a key.");
            }

            Token value = reader.NextValue(key.Text);
            VdfNode child = new(value.Kind == TokenKind.Text ? value.Text : null);
            if (value.Kind == TokenKind.Open)
            {
                ReadBlock(reader, child, nested: true);
            }
            else if (value.Kind != TokenKind.Text)
            {
                throw new FormatException($"Unexpected token after \"{key.Text}\".");
            }

            block._children.TryAdd(key.Text, child);
            reader.SkipCondition();
        }

        if (nested)
        {
            throw new FormatException("Missing '}'.");
        }
    }

    private enum TokenKind
    {
        Text,
        Open,
        Close,
        Condition,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed class Reader(string text)
    {
        private int _position;

        public Token NextValue(string key)
        {
            Token value = Next() ?? throw new FormatException($"No value for \"{key}\".");
            return value.Kind == TokenKind.Condition ? Next() ?? throw new FormatException($"No value for \"{key}\".") : value;
        }

        public void SkipCondition()
        {
            SkipSpace();
            if (_position < text.Length && text[_position] == '[')
            {
                Next();
            }
        }

        public Token? Next()
        {
            SkipSpace();
            if (_position >= text.Length)
            {
                return null;
            }

            switch (text[_position])
            {
                case '{':
                    _position++;
                    return new Token(TokenKind.Open, "{");
                case '}':
                    _position++;
                    return new Token(TokenKind.Close, "}");
                case '[':
                    int end = text.IndexOf(']', _position);
                    if (end < 0)
                    {
                        throw new FormatException("Missing ']'.");
                    }

                    string condition = text[_position..(end + 1)];
                    _position = end + 1;
                    return new Token(TokenKind.Condition, condition);
                case '"':
                    return new Token(TokenKind.Text, Quoted());
                default:
                    int start = _position;
                    while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not ('{' or '}' or '"'))
                    {
                        _position++;
                    }

                    return new Token(TokenKind.Text, text[start.._position]);
            }
        }

        private string Quoted()
        {
            var value = new StringBuilder();
            _position++;
            while (_position < text.Length)
            {
                char c = text[_position++];
                if (c == '"')
                {
                    return value.ToString();
                }

                if (c == '\\' && _position < text.Length)
                {
                    char escaped = text[_position++];
                    value.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => escaped,
                    });
                    continue;
                }

                value.Append(c);
            }

            throw new FormatException("Missing closing quote.");
        }

        private void SkipSpace()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                }
                else if (text[_position] == '/' && _position + 1 < text.Length && text[_position + 1] == '/')
                {
                    int newline = text.IndexOf('\n', _position);
                    _position = newline < 0 ? text.Length : newline + 1;
                }
                else
                {
                    return;
                }
            }
        }
    }
}
