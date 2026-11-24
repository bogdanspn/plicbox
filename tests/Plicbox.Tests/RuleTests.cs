using Plicbox.Core;
using Plicbox.Mail;

namespace Plicbox.Tests;

public sealed class RuleTests
{
    private static readonly MessageSummary Mail = new()
    {
        AccountId = 1, FromName = "Ana Popescu", FromAddress = "ana@family.example", To = "you@example.com",
        Subject = "Re: Weekend plans", HasAttachments = true,
    };

    private static MailRule Rule(params RuleCondition[] conditions) => new() { Conditions = [.. conditions] };
    private static RuleCondition C(RuleField f, RuleOperator o, params string[] values) => new() { Field = f, Operator = o, Values = [.. values] };

    [Fact]
    public void Any_of_several_values_matches_and_case_is_ignored()
    {
        Assert.True(RuleMatcher.Matches(Rule(C(RuleField.From, RuleOperator.Contains, "bob@x.com", "@FAMILY.example")), Mail, null));
        Assert.True(RuleMatcher.Matches(Rule(C(RuleField.From, RuleOperator.Is, "ana@family.example")), Mail, null));
        Assert.True(RuleMatcher.Matches(Rule(C(RuleField.From, RuleOperator.Is, "Ana Popescu")), Mail, null)); // the name counts too
        Assert.False(RuleMatcher.Matches(Rule(C(RuleField.From, RuleOperator.DoesNotContain, "bob", "ana")), Mail, null));
    }

    [Fact]
    public void All_or_any_conditions()
    {
        var subject = C(RuleField.Subject, RuleOperator.Contains, "invoice");
        var from = C(RuleField.From, RuleOperator.EndsWith, "family.example");
        Assert.False(RuleMatcher.Matches(Rule(subject, from), Mail, null));
        Assert.True(RuleMatcher.Matches(Rule(subject, from) with { MatchAll = false }, Mail, null));
    }

    [Fact]
    public void Account_scope_attachments_and_body()
    {
        Assert.False(RuleMatcher.Matches(Rule(C(RuleField.Subject, RuleOperator.Contains, "weekend")) with { AccountId = 2 }, Mail, null));
        Assert.True(RuleMatcher.Matches(Rule(new RuleCondition { Field = RuleField.HasAttachment }), Mail, null));
        Assert.False(RuleMatcher.Matches(Rule(new RuleCondition { Field = RuleField.HasAttachment, Operator = RuleOperator.DoesNotContain }), Mail, null));

        var body = Rule(C(RuleField.Body, RuleOperator.Contains, "unsubscribe"));
        Assert.True(RuleMatcher.NeedsBody(body));
        Assert.False(RuleMatcher.Matches(body, Mail, null));           // body unknown: no match
        Assert.True(RuleMatcher.Matches(body, Mail, "… click to UNSUBSCRIBE"));
    }

    [Fact]
    public void Empty_conditions_are_ignored_and_no_conditions_match_everything()
    {
        Assert.True(RuleMatcher.Matches(Rule(C(RuleField.Subject, RuleOperator.Contains, "  ")), Mail, null));
        Assert.True(RuleMatcher.Matches(Rule(), Mail, null));
    }

    [Fact]
    public void Summary_reads_like_a_sentence()
    {
        var rule = Rule(C(RuleField.From, RuleOperator.Is, "ana@family.example")) with
        {
            Actions = [new RuleAction { Kind = RuleActionKind.MoveTo, FolderAccountId = 1, FolderPath = "Family" }, new RuleAction { Kind = RuleActionKind.MarkRead }],
        };
        Assert.Equal("From is “ana@family.example” > move to Family, mark as read", RuleMatcher.Summary(rule, a => a.FolderPath));
    }

    [Theory]
    [InlineData("Re: Fwd: Your order", "Your order")]
    [InlineData("AW: Rechnung", "Rechnung")]
    [InlineData("Meeting: Tuesday", "Meeting: Tuesday")]
    public void Subjects_lose_reply_prefixes(string subject, string expected) => Assert.Equal(expected, RuleMatcher.CleanSubject(subject));
}
