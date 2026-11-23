namespace Plicbox.Mail;

public static class ThreadKeys
{
    /// <summary>
    /// A conversation is identified by its root Message-ID: the first entry of References, else In-Reply-To,
    /// else the message's own id. Replies from any mail client that sets References therefore group together.
    /// </summary>
    public static string? For(string? messageId, IEnumerable<string>? references, string? inReplyTo)
    {
        var root = references?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
                   ?? (string.IsNullOrWhiteSpace(inReplyTo) ? null : inReplyTo)
                   ?? messageId;
        return string.IsNullOrWhiteSpace(root) ? null : root.Trim().Trim('<', '>').ToLowerInvariant();
    }
}
