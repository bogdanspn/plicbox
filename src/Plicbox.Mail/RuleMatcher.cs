using System.Globalization;
using Plicbox.Core;

namespace Plicbox.Mail;

/// <summary>Decides whether a mail rule applies to a message, and describes rules in plain words.</summary>
public static class RuleMatcher
{
    /// <summary>Yes/no conditions ("has attachments", "on a threat list"): no values to type, "is"/"isn't" as the test.</summary>
    public static bool IsYesNo(RuleField f) => f is RuleField.HasAttachment or RuleField.KnownThreat;

    /// <summary>Whether checking the rule needs the downloaded message (its text, or its links), fetched for new mail only when it does.</summary>
    public static bool NeedsBody(MailRule rule) => rule.Conditions.Any(c =>
        c.Field == RuleField.Body && c.Values.Any(v => v.Trim().Length > 0) || c.Field == RuleField.KnownThreat);

    /// <param name="body">The message's text, or null when it isn't available (body conditions then don't match).</param>
    /// <param name="links">The message's web links, or null when the message isn't downloaded (only the sender is checked then).</param>
    public static bool Matches(MailRule rule, MessageSummary m, string? body, IReadOnlyCollection<string>? links = null)
    {
        if (rule.AccountId is { } account && account != m.AccountId) return false;
        var conditions = rule.Conditions.Where(IsComplete).ToList();
        if (conditions.Count == 0) return true; // no conditions: every message (the editor says so)
        return rule.MatchAll ? conditions.All(c => Test(c, m, body, links)) : conditions.Any(c => Test(c, m, body, links));
    }

    private static bool IsComplete(RuleCondition c) => IsYesNo(c.Field) || c.Values.Any(v => v.Trim().Length > 0);

    private static bool Test(RuleCondition c, MessageSummary m, string? body, IReadOnlyCollection<string>? links)
    {
        if (c.Field == RuleField.HasAttachment)
            return c.Operator == RuleOperator.DoesNotContain ? !m.HasAttachments : m.HasAttachments;
        if (c.Field == RuleField.KnownThreat)
        {
            var listed = ThreatLists.Current.CheckMessage(m.FromAddress, links ?? []).Kind != ThreatKind.None;
            return c.Operator == RuleOperator.DoesNotContain ? !listed : listed;
        }

        IEnumerable<string?> texts = c.Field switch
        {
            RuleField.From => [m.FromAddress, m.FromName],
            RuleField.To => [m.To],
            RuleField.Subject => [m.Subject],
            RuleField.Body => [body],
            RuleField.FromOrSubject => [m.FromAddress, m.FromName, m.Subject],
            _ => [],
        };
        var present = texts.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
        if (c.Field == RuleField.Body && body is null) return false;

        var values = c.Values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        bool Hit(string text, string value) => c.Operator switch
        {
            RuleOperator.Is => text.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.StartsWith => text.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.EndsWith => text.EndsWith(value, StringComparison.OrdinalIgnoreCase),
            _ => text.Contains(value, StringComparison.OrdinalIgnoreCase),
        };
        var any = values.Any(v => present.Any(t => Hit(t, v)));
        return c.Operator == RuleOperator.DoesNotContain ? !any : any;
    }

    /// <summary>"Re: Fwd: Your order" → "Your order", for subject rules made from a message.</summary>
    public static string CleanSubject(string subject)
    {
        var s = subject.Trim();
        while (true)
        {
            var colon = s.IndexOf(':');
            if (colon is <= 0 or > 4) break;
            var prefix = s[..colon].Trim().ToLower(CultureInfo.InvariantCulture);
            if (prefix is not ("re" or "fw" or "fwd" or "aw" or "wg" or "tr" or "rv")) break;
            s = s[(colon + 1)..].TrimStart();
        }
        return s;
    }

    // ───────── plain words, for the rule list ─────────

    public static string FieldName(RuleField f) => f switch
    {
        RuleField.From => L.T("From"),
        RuleField.To => L.T("To or Cc"),
        RuleField.Subject => L.T("Subject"),
        RuleField.Body => L.T("Message text"),
        RuleField.FromOrSubject => L.T("From or subject"),
        RuleField.HasAttachment => L.T("Attachments"),
        RuleField.KnownThreat => L.T("Sender or links"),
        _ => f.ToString(),
    };

    public static string OperatorName(RuleOperator o, RuleField f = RuleField.From) => f switch
    {
        RuleField.HasAttachment => o == RuleOperator.DoesNotContain ? L.T("has none") : L.T("has some"),
        RuleField.KnownThreat => o == RuleOperator.DoesNotContain ? L.T("aren't on a phishing or malware list") : L.T("are on a phishing or malware list"),
        _ => o switch
        {
            RuleOperator.DoesNotContain => L.T("doesn't contain"),
            RuleOperator.Is => L.T("is"),
            RuleOperator.StartsWith => L.T("starts with"),
            RuleOperator.EndsWith => L.T("ends with"),
            _ => L.T("contains"),
        },
    };

    public static string ActionName(RuleActionKind k) => k switch
    {
        RuleActionKind.MoveTo => L.T("Move to"),
        RuleActionKind.CopyTo => L.T("Copy to"),
        RuleActionKind.MarkRead => L.T("Mark as read"),
        RuleActionKind.Flag => L.T("Flag"),
        RuleActionKind.Archive => L.T("Archive"),
        RuleActionKind.Delete => L.T("Delete (move to Trash)"),
        RuleActionKind.MoveToJunk => L.T("Move to Junk"),
        _ => k.ToString(),
    };

    /// <summary>"From contains ana@x.com → Move to Family, mark as read".</summary>
    public static string Summary(MailRule rule, Func<RuleAction, string?> folderName)
    {
        var conditions = rule.Conditions.Where(IsComplete).Select(c => c.Field switch
            {
                RuleField.HasAttachment => c.Operator == RuleOperator.DoesNotContain ? L.T("no attachments") : L.T("has attachments"),
                RuleField.KnownThreat => c.Operator == RuleOperator.DoesNotContain ? L.T("not on a phishing or malware list") : L.T("on a phishing or malware list"),
                _ => $"{InSentence(FieldName(c.Field))} {OperatorName(c.Operator)} {Quote(c.Values)}",
            })
            .ToList();
        var when = conditions.Count == 0 ? L.T("Every message") : string.Join(rule.MatchAll ? L.T(" and ") : L.T(" or "), conditions);
        var then = rule.Actions.Count == 0 ? L.T("nothing yet")
            : string.Join(", ", rule.Actions.Select(a => a.NeedsFolder ? $"{ActionName(a.Kind).ToLower(CultureInfo.CurrentCulture)} {folderName(a) ?? "…"}" : ActionName(a.Kind).ToLower(CultureInfo.CurrentCulture)));
        return $"{char.ToUpper(when[0], CultureInfo.CurrentCulture)}{when[1..]} → {then}";
    }

    private static string Quote(List<string> values)
    {
        var list = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        return list.Count switch
        {
            0 => "…",
            1 => $"“{list[0]}”",
            2 => L.F("“{0}” or “{1}”", list[0], list[1]),
            _ => L.F("“{0}” or {1} more", list[0], list.Count - 1),
        };
    }

    // Field names are nouns: German keeps them capitalised mid-sentence ("Betreff enthält …").
    private static string InSentence(string fieldName) => L.Language == "de" ? fieldName : fieldName.ToLower(CultureInfo.CurrentCulture);
}
