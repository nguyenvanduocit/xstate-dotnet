using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
namespace XState;

/// <summary>JavaScript numeric coercion and formatting for after-config keys.</summary>
internal static partial class DelayKey
{
    [GeneratedRegex(@"^[+-]?(?:(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?|Infinity)$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalNumber();
    internal static bool TryNumber(string key, out double value)
    {
        var start = 0;
        var end = key.Length;
        while (start < end && IsSpace(key[start])) start++;
        while (end > start && IsSpace(key[end - 1])) end--;
        var text = key[start..end];
        if (text.Length == 0) { value = 0; return true; }
        if (text.Length > 2 && text[0] == '0')
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'o' => 8, 'b' => 2, _ => 0 };
            if (radix != 0)
            {
                var integer = BigInteger.Zero;
                foreach (var ch in text.AsSpan(2))
                {
                    var digit = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
                    if (digit < 0 || digit >= radix) { value = double.NaN; return false; }
                    integer = integer * radix + digit;
                }
                value = (double)integer;
                return true;
            }
        }
        if (!DecimalNumber().IsMatch(text)) { value = double.NaN; return false; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    private static bool IsSpace(char ch) => ch == '\uFEFF' || ch != '\u0085' && char.IsWhiteSpace(ch);
    internal static string Format(double value)
    {
        if (value == 0) return "0";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        var prefix = value < 0 ? "-" : "";
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = text.IndexOf('E');
        var exponent = e < 0 ? 0 : int.Parse(text.AsSpan(e + 1), CultureInfo.InvariantCulture);
        var coefficient = e < 0 ? text : text[..e];
        var dot = coefficient.IndexOf('.');
        if (dot < 0) dot = coefficient.Length;
        var digits = coefficient.Replace(".", "", StringComparison.Ordinal);
        var significant = digits.TrimStart('0');
        var power = exponent + dot - (digits.Length - significant.Length) - 1;
        significant = significant.TrimEnd('0');
        if (power >= 21 || power < -6)
            return prefix + significant[0] + (significant.Length > 1 ? "." + significant[1..] : "") + "e" +
                (power >= 0 ? "+" : "") + power.ToString(CultureInfo.InvariantCulture);
        var position = power + 1;
        if (position <= 0) return prefix + "0." + new string('0', -position) + significant;
        if (position >= significant.Length) return prefix + significant + new string('0', position - significant.Length);
        return prefix + significant[..position] + "." + significant[position..];
    }
}

