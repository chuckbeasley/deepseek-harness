using System.Globalization;

namespace Harness.Cordis.Plugin.Include;

/// <summary>Error thrown when a config expression uses a construct outside the restricted language.</summary>
public sealed class ConfigExpressionException : Exception
{
    /// <summary>Create the error with a message naming the rejected construct.</summary>
    public ConfigExpressionException(string message) : base(message)
    {
    }
}

/// <summary>
/// A restricted declarative config expression — the ported form of a <c>!!js</c> config value.
/// The language supports literals, environment lookups, if/else ternaries, and/or/not, string
/// concatenation, list construction, numeric negation, and member access on dictionaries and
/// lists. Everything else (function calls, comparison operators, assignment, arbitrary code)
/// fails loud with <see cref="ConfigExpressionException"/>. Roslyn scripting is deliberately not
/// implemented; the plan records it as a later opt-in.
/// </summary>
public sealed class ConfigExpression
{
    /// <summary>The raw expression text.</summary>
    public string Source { get; }

    private readonly Node _root;

    /// <summary>Parse <paramref name="source"/>.</summary>
    public ConfigExpression(string source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _root = new Parser(source).Parse();
    }

    /// <summary>Evaluate the expression against the process environment.</summary>
    public object? Evaluate() => Evaluator.Evaluate(_root);

    /// <inheritdoc/>
    public override string ToString() => $"!!js {Source}";

    private abstract class Node
    {
    }

    private sealed class Literal(object? value) : Node
    {
        public object? Value { get; } = value;
    }

    private sealed class Env(string name) : Node
    {
        public string Name { get; } = name;
    }

    private sealed class ListNode(IReadOnlyList<Node> items) : Node
    {
        public IReadOnlyList<Node> Items { get; } = items;
    }

    private sealed class NotNode(Node inner) : Node
    {
        public Node Inner { get; } = inner;
    }

    private sealed class NegativeNode(Node inner) : Node
    {
        public Node Inner { get; } = inner;
    }

    private sealed class AndNode(Node left, Node right) : Node
    {
        public Node Left { get; } = left;
        public Node Right { get; } = right;
    }

    private sealed class OrNode(Node left, Node right) : Node
    {
        public Node Left { get; } = left;
        public Node Right { get; } = right;
    }

    private sealed class TernaryNode(Node condition, Node whenTrue, Node whenFalse) : Node
    {
        public Node Condition { get; } = condition;
        public Node WhenTrue { get; } = whenTrue;
        public Node WhenFalse { get; } = whenFalse;
    }

    private sealed class ConcatNode(Node left, Node right) : Node
    {
        public Node Left { get; } = left;
        public Node Right { get; } = right;
    }

    private sealed class MemberNode(Node target, string name) : Node
    {
        public Node Target { get; } = target;
        public string Name { get; } = name;
    }

    private sealed class IndexNode(Node target, Node index) : Node
    {
        public Node Target { get; } = target;
        public Node Index { get; } = index;
    }

    private sealed class Parser
    {
        private readonly string _source;
        private int _position;

        public Parser(string source)
        {
            _source = source;
        }

        public Node Parse()
        {
            var node = ParseTernary();
            SkipWhitespace();
            if (_position < _source.Length)
            {
                throw new ConfigExpressionException(
                    $"unsupported expression construct near offset {_position}: '{_source[_position..]}'");
            }
            return node;
        }

        private Node ParseTernary()
        {
            var condition = ParseOr();
            SkipWhitespace();
            if (TryConsume('?'))
            {
                var whenTrue = ParseTernary();
                SkipWhitespace();
                Expect(':');
                var whenFalse = ParseTernary();
                return new TernaryNode(condition, whenTrue, whenFalse);
            }
            return condition;
        }

        private Node ParseOr()
        {
            var node = ParseAnd();
            while (true)
            {
                SkipWhitespace();
                if (!TryConsume("||")) return node;
                node = new OrNode(node, ParseAnd());
            }
        }

        private Node ParseAnd()
        {
            var node = ParseAdd();
            while (true)
            {
                SkipWhitespace();
                if (!TryConsume("&&")) return node;
                node = new AndNode(node, ParseAdd());
            }
        }

        private Node ParseAdd()
        {
            var node = ParseUnary();
            while (true)
            {
                SkipWhitespace();
                if (!TryConsume('+')) return node;
                node = new ConcatNode(node, ParseUnary());
            }
        }

        private Node ParseUnary()
        {
            SkipWhitespace();
            if (TryConsume('!')) return new NotNode(ParseUnary());
            if (TryConsume('-')) return new NegativeNode(ParseUnary());
            return ParsePostfix();
        }

        private Node ParsePostfix()
        {
            var node = ParsePrimary();
            while (true)
            {
                SkipWhitespace();
                if (TryConsume('.'))
                {
                    node = new MemberNode(node, ParseIdentifier());
                    continue;
                }
                if (TryConsume('['))
                {
                    var index = ParseTernary();
                    SkipWhitespace();
                    Expect(']');
                    node = new IndexNode(node, index);
                    continue;
                }
                return node;
            }
        }

        private Node ParsePrimary()
        {
            SkipWhitespace();
            if (TryConsume('('))
            {
                var inner = ParseTernary();
                SkipWhitespace();
                Expect(')');
                return inner;
            }
            if (TryConsume('['))
            {
                var items = new List<Node>();
                SkipWhitespace();
                if (!TryConsume(']'))
                {
                    while (true)
                    {
                        items.Add(ParseTernary());
                        SkipWhitespace();
                        if (TryConsume(']')) break;
                        Expect(',');
                    }
                }
                return new ListNode(items);
            }
            if (TryConsume('$')) return new Env(ParseEnvName());
            if (TryParseString(out var str)) return new Literal(str);
            if (TryParseNumber(out var num)) return new Literal(num);
            var start = _position;
            var ident = ParseIdentifier();
            switch (ident)
            {
                case "true":
                    return new Literal(true);
                case "false":
                    return new Literal(false);
                case "null":
                    return new Literal(null);
                default:
                    throw new ConfigExpressionException(
                        $"unsupported identifier '{ident}' at offset {start}: only literals, $env lookups, and the " +
                        "restricted operators are allowed");
            }
        }

        private string ParseEnvName()
        {
            var name = ParseIdentifier();
            while (TryConsume('.'))
            {
                name += "." + ParseIdentifier();
            }
            if (name.Length == 0) throw new ConfigExpressionException("expected an environment variable name after '$'");
            if (name.StartsWith("env.", StringComparison.Ordinal)) name = name[4..];
            return name;
        }

        private string ParseIdentifier()
        {
            SkipWhitespace();
            var start = _position;
            while (_position < _source.Length &&
                   (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
            {
                _position++;
            }
            if (_position == start)
            {
                throw new ConfigExpressionException($"expected an identifier at offset {start}");
            }
            return _source[start.._position];
        }

        private bool TryParseString(out string value)
        {
            value = "";
            SkipWhitespace();
            if (_position >= _source.Length || (_source[_position] != '"' && _source[_position] != '\''))
            {
                return false;
            }
            var quote = _source[_position++];
            var builder = new System.Text.StringBuilder();
            while (_position < _source.Length && _source[_position] != quote)
            {
                var ch = _source[_position++];
                if (ch == '\\' && quote == '"')
                {
                    if (_position >= _source.Length)
                    {
                        throw new ConfigExpressionException("unterminated escape in string literal");
                    }
                    var escaped = _source[_position++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => escaped,
                    });
                }
                else
                {
                    builder.Append(ch);
                }
            }
            if (_position >= _source.Length)
            {
                throw new ConfigExpressionException("unterminated string literal");
            }
            _position++;
            value = builder.ToString();
            return true;
        }

        private bool TryParseNumber(out object value)
        {
            value = 0;
            SkipWhitespace();
            var start = _position;
            while (_position < _source.Length && char.IsDigit(_source[_position]))
            {
                _position++;
            }
            if (_position < _source.Length && _source[_position] == '.')
            {
                _position++;
                while (_position < _source.Length && char.IsDigit(_source[_position]))
                {
                    _position++;
                }
            }
            if (_position == start) return false;
            var text = _source[start.._position];
            if (!text.Contains('.') && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                value = integer;
                return true;
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                value = number;
                return true;
            }
            throw new ConfigExpressionException($"invalid number literal '{text}'");
        }

        private void SkipWhitespace()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
            {
                _position++;
            }
        }

        private bool TryConsume(char ch)
        {
            if (_position < _source.Length && _source[_position] == ch)
            {
                _position++;
                return true;
            }
            return false;
        }

        private bool TryConsume(string text)
        {
            if (_position + text.Length <= _source.Length &&
                _source.AsSpan(_position, text.Length).SequenceEqual(text))
            {
                _position += text.Length;
                return true;
            }
            return false;
        }

        private void Expect(char ch)
        {
            if (!TryConsume(ch))
            {
                throw new ConfigExpressionException($"expected '{ch}' at offset {_position}");
            }
        }
    }

    private static class Evaluator
    {
        public static object? Evaluate(Node node) => node switch
        {
            Literal literal => literal.Value,
            Env env => Environment.GetEnvironmentVariable(env.Name),
            ListNode list => list.Items.Select(Evaluate).ToList(),
            NotNode not => Truthy(Evaluate(not.Inner)) ? (object?)false : true,
            NegativeNode negative => -ToNumber(Evaluate(negative.Inner)),
            AndNode and => Truthy(Evaluate(and.Left)) ? Evaluate(and.Right) : false,
            OrNode or => Truthy(Evaluate(or.Left)) ? Evaluate(or.Left) : Evaluate(or.Right),
            TernaryNode ternary => Truthy(Evaluate(ternary.Condition))
                ? Evaluate(ternary.WhenTrue)
                : Evaluate(ternary.WhenFalse),
            ConcatNode concat => Concat(Evaluate(concat.Left), Evaluate(concat.Right)),
            MemberNode member => Access(Evaluate(member.Target), member.Name),
            IndexNode index => AccessIndex(Evaluate(index.Target), Evaluate(index.Index)),
            _ => throw new ConfigExpressionException("unknown expression node"),
        };

        private static bool Truthy(object? value) => value switch
        {
            null => false,
            bool boolean => boolean,
            string text => text.Length > 0,
            int integer => integer != 0,
            long integer => integer != 0,
            double number => number != 0,
            _ => true,
        };

        private static object? Concat(object? left, object? right)
        {
            if (left is string or char || right is string or char) return $"{left}{right}";
            if (left is null) return right;
            if (right is null) return left;
            if (left is double leftDouble && right is double rightDouble) return leftDouble + rightDouble;
            if (left is int leftInt && right is int rightInt) return leftInt + rightInt;
            if (left is long leftLong && right is long rightLong) return leftLong + rightLong;
            if (left is int or long or double && right is int or long or double)
            {
                return ToNumber(left) + ToNumber(right);
            }
            return $"{left}{right}";
        }

        private static double ToNumber(object? value) => value switch
        {
            int integer => integer,
            long integer => integer,
            double number => number,
            null => 0,
            _ => throw new ConfigExpressionException($"value '{value}' is not a number"),
        };

        private static object? AccessIndex(object? target, object? index)
        {
            if (target is IList<object?> list && index is long integer && integer >= 0 && integer < list.Count)
            {
                return list[(int)integer];
            }
            if (target is IReadOnlyDictionary<string, object?> readOnly && index is string key &&
                readOnly.TryGetValue(key, out var value))
            {
                return value;
            }
            throw new ConfigExpressionException($"cannot index value [{target ?? "null"}] with [{index ?? "null"}]");
        }

        private static object? Access(object? target, string name)
        {
            if (target is IReadOnlyDictionary<string, object?> readOnly && readOnly.TryGetValue(name, out var value))
            {
                return value;
            }
            if (target is Dictionary<string, object?> dictionary && dictionary.TryGetValue(name, out value))
            {
                return value;
            }
            if (target is IList<object?> list &&
                int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && index < list.Count)
            {
                return list[index];
            }
            throw new ConfigExpressionException($"cannot access member '{name}' on value '{target ?? "null"}'");
        }
    }
}
