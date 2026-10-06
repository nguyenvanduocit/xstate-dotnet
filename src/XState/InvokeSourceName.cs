using System.Text.RegularExpressions;
namespace XState;

internal static partial class InvokeSourceName
{
    [GeneratedRegex(@"^xstate\.invoke\.([0-9]+)\.(.*)", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    internal static Match Match(string name) => Pattern().Match(name);
}
