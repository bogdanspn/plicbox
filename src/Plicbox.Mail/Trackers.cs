using Plicbox.Core;
namespace Plicbox.Mail;

/// <summary>
/// Recognises "read receipt" pixels that newsletters and sales tools hide in mail: by the tracking service's
/// address, or by being an invisible 1x1-ish image. Named services let the reading pane say who was blocked.
/// </summary>
public static class Trackers
{
    private static readonly (string Pattern, string Name)[] Known =
    [
        ("sendgrid.net", "SendGrid"),
        ("list-manage.com", "Mailchimp"),
        ("mailchimp", "Mailchimp"),
        ("hubspotemail.net", "HubSpot"), ("hubspotlinks", "HubSpot"), ("track.hubspot.com", "HubSpot"), ("sidekickopen", "HubSpot"),
        ("exct.net", "Salesforce"), ("exacttarget.com", "Salesforce"), ("pardot.com", "Salesforce"), ("salesforce.com/track", "Salesforce"),
        ("awstrack.me", "Amazon SES"),
        ("mandrillapp.com", "Mailchimp Transactional"),
        ("mailgun", "Mailgun"),
        ("mjt.lu", "Mailjet"),
        ("createsend", "Campaign Monitor"),
        ("rs6.net", "Constant Contact"),
        ("klaviyo", "Klaviyo"),
        ("sendibt", "Brevo"), ("sendinblue", "Brevo"), ("brevo.com", "Brevo"),
        ("customeriomail", "Customer.io"),
        ("pstmrk.it", "Postmark"),
        ("sparkpostmail", "SparkPost"),
        ("iterable.com", "Iterable"),
        ("mktoweb", "Marketo"), ("marketo", "Marketo"),
        ("mixpanel.com", "Mixpanel"),
        ("google-analytics.com", "Google Analytics"),
        ("substack.com/o/", "Substack"),
        ("linkedin.com/emimp", "LinkedIn"),
        ("facebook.com/tr", "Facebook"),
        ("mailtrack.io", "Mailtrack"), ("mailsuite.com", "Mailtrack"),
        ("yesware.com", "Yesware"),
        ("streak.com", "Streak"),
        ("intercom-mail", "Intercom"),
    ];

    /// <summary>Name for a generic invisible pixel whose sender we can't identify.</summary>
    public const string Generic = "Email";

    /// <summary>
    /// The tracker an image is, or null for a normal picture. Only remote (http/https) images can track, and only
    /// ones nobody is meant to see; a picture that's part of the layout is never taken for one:
    /// <list type="bullet">
    /// <item>given a real size (more than a few pixels), it's meant to be seen, even on a tracking service's host
    /// (Mailchimp keeps newsletters' pictures there) or hidden for now (a responsive mail's mobile version, which a
    /// media query brings out);</item>
    /// <item>used more than once in the message (<paramref name="uses"/>), it's a spacer: a pixel that reports the
    /// opening carries an address of its own, once.</item>
    /// </list>
    /// </summary>
    public static string? Identify(string? src, string? width, string? height, string? style, int uses = 1)
    {
        if (src is null || !(src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (IsVisibleSize(width) || IsVisibleSize(height)) return null;

        var url = src.ToLowerInvariant();
        foreach (var (pattern, name) in Known)
            if (url.Contains(pattern, StringComparison.Ordinal)) return name;
        if (uses > 1) return null;

        // An image nobody can see exists only to report that the mail was opened.
        if (IsTiny(width) && IsTiny(height)) return Generic;
        var s = (style ?? "").Replace(" ", "").ToLowerInvariant();
        if (s.Contains("display:none") || s.Contains("visibility:hidden")
            || (s.Contains("width:1px") && s.Contains("height:1px")) || (s.Contains("width:0") && s.Contains("height:0")))
            return Generic;
        return null;
    }

    private static bool IsTiny(string? size) => Pixels(size) is <= 3;

    private static bool IsVisibleSize(string? size) => Pixels(size) is > 3;

    /// <summary>"40" or "40px" as pixels; null for anything else ("auto", "100%").</summary>
    private static int? Pixels(string? size) =>
        size is not null && int.TryParse(size.Trim().TrimEnd('x', 'p', 'X', 'P'), out var px) ? px : null;

    /// <summary>"SendGrid email tracking", "Email tracking", or "SendGrid and HubSpot email tracking".</summary>
    public static string Describe(IReadOnlyCollection<string> names)
    {
        var named = names.Where(n => n != Generic).Distinct().ToList();
        return named.Count switch
        {
            0 => L.T("Email tracking"),
            1 => L.F("{0} email tracking", named[0]),
            _ => L.F("{0} and {1} email tracking", string.Join(", ", named.Take(named.Count - 1)), named[^1]),
        };
    }
}
