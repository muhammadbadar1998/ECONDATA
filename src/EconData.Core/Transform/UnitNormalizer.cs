using System.Globalization;

namespace EconData.Core.Transform;

/// <summary>Raised when a unit-normalization expression is unsafe or cannot be evaluated.</summary>
public sealed class UnitNormalizationException : Exception
{
    public UnitNormalizationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Applies a safe arithmetic unit-normalization expression over the symbol <c>x</c>
/// (the raw value). Empty means identity. Replaces the Python <c>eval</c> with a
/// recursive-descent parser restricted to <c>x 0-9 . + - * / ( )</c> and spaces.
/// </summary>
public static class UnitNormalizer
{
    private const string AllowedChars = "x0123456789.+-*/() ";

    public static double Normalize(double rawValue, string unitNormalization)
    {
        if (string.IsNullOrEmpty(unitNormalization))
        {
            return rawValue;
        }

        foreach (var ch in unitNormalization)
        {
            if (!AllowedChars.Contains(ch, StringComparison.Ordinal))
            {
                throw new UnitNormalizationException($"Unsafe unit_normalization expression: '{unitNormalization}'");
            }
        }

        try
        {
            return ExpressionEvaluator.Evaluate(unitNormalization, rawValue);
        }
        catch (FormatException ex)
        {
            throw new UnitNormalizationException(
                $"Failed to apply unit_normalization '{unitNormalization}' to value {rawValue}: {ex.Message}");
        }
    }

    private sealed class ExpressionEvaluator
    {
        private readonly string _text;
        private readonly double _x;
        private int _position;

        private ExpressionEvaluator(string text, double x)
        {
            _text = text;
            _x = x;
        }

        public static double Evaluate(string expression, double x) => new ExpressionEvaluator(expression, x).Parse();

        private double Parse()
        {
            var value = ParseExpression();
            SkipWhitespace();
            if (_position != _text.Length)
            {
                throw new FormatException($"Unexpected token at position {_position}");
            }

            return value;
        }

        private double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (Match('+'))
                {
                    value += ParseTerm();
                }
                else if (Match('-'))
                {
                    value -= ParseTerm();
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (Match('*'))
                {
                    value *= ParseFactor();
                }
                else if (Match('/'))
                {
                    var divisor = ParseFactor();
                    if (divisor == 0)
                    {
                        throw new FormatException("Division by zero");
                    }

                    value /= divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            if (Match('-'))
            {
                return -ParseFactor();
            }

            if (Match('+'))
            {
                return ParseFactor();
            }

            if (Match('('))
            {
                var value = ParseExpression();
                SkipWhitespace();
                if (!Match(')'))
                {
                    throw new FormatException("Missing closing parenthesis");
                }

                return value;
            }

            if (_position < _text.Length && _text[_position] == 'x')
            {
                _position++;
                return _x;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            var start = _position;
            while (_position < _text.Length && (char.IsAsciiDigit(_text[_position]) || _text[_position] == '.'))
            {
                _position++;
            }

            if (start == _position)
            {
                throw new FormatException($"Expected number at position {_position}");
            }

            return double.Parse(_text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && _text[_position] == ' ')
            {
                _position++;
            }
        }

        private bool Match(char expected)
        {
            if (_position < _text.Length && _text[_position] == expected)
            {
                _position++;
                return true;
            }

            return false;
        }
    }
}
