namespace Plicbox.Core;

/// <summary>
/// What part of a message a rule condition looks at. <see cref="KnownThreat"/>: the sender's domain or a link is on
/// the public lists of reported phishing and malware sites. Settings store these as numbers: add new ones at the end.
/// </summary>
public enum RuleField { From, To, Subject, Body, FromOrSubject, HasAttachment, KnownThreat }

public enum RuleOperator { Contains, DoesNotContain, Is, StartsWith, EndsWith }

/// <summary>
/// One test, e.g. From contains "ana@example.com" or "@shop.example". Several values mean "any of these"
/// (for the negative operator: "none of these"), so one row covers a whole list of senders.
/// </summary>
public sealed record RuleCondition
{
    public RuleField Field { get; init; } = RuleField.From;
    public RuleOperator Operator { get; init; } = RuleOperator.Contains;
    public List<string> Values { get; init; } = [];
}

public enum RuleActionKind { MoveTo, CopyTo, MarkRead, Flag, Archive, Delete, MoveToJunk }

/// <summary>What happens to a matching message. Folder actions name the folder by account and server path.</summary>
public sealed record RuleAction
{
    public RuleActionKind Kind { get; init; } = RuleActionKind.MoveTo;
    public long? FolderAccountId { get; init; }
    public string? FolderPath { get; init; }

    public bool NeedsFolder => Kind is RuleActionKind.MoveTo or RuleActionKind.CopyTo;
}

/// <summary>A rule run on new mail arriving in an inbox (and on demand on a folder's existing mail).</summary>
public sealed record MailRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = L.T("New rule");
    public bool Enabled { get; init; } = true;
    /// <summary>Only mail arriving in this account; null for every account.</summary>
    public long? AccountId { get; init; }
    /// <summary>True: every condition must match. False: any one is enough.</summary>
    public bool MatchAll { get; init; } = true;
    public List<RuleCondition> Conditions { get; init; } = [];
    public List<RuleAction> Actions { get; init; } = [];
    /// <summary>Later rules don't run on a message this rule matched.</summary>
    public bool StopAfter { get; init; }
}
