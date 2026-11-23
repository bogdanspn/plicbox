using System.Text.RegularExpressions;

namespace Plicbox.Core;

public static partial class Subjects
{
    /// <summary>"Re: Fwd: Invoice" → "Invoice", for sorting by subject (English, German and Nordic prefixes).</summary>
    public static string WithoutReplyPrefix(string subject) => ReplyPrefix().Replace(subject, "");

    [GeneratedRegex(@"^\s*(?:(?:re|fwd?|aw|wg|sv|vs)\s*(?:\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();
}
