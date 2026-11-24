using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Plicbox.Auth;
using Plicbox.Core;

namespace Plicbox.Mail;

/// <summary>
/// Reads Gmail's "Send mail as" addresses (Settings → Accounts) through the Gmail API, using the token the account
/// already has (the https://mail.google.com/ scope covers it). Only verified addresses are returned.
/// </summary>
public sealed class GmailAliases(HttpClient http, GoogleAuth google)
{
    public async Task<IReadOnlyList<MailAlias>> GetAsync(Account account, CancellationToken ct = default) =>
        await GetAsync(await google.GetAccessTokenAsync(account.Id, account.Email, ct), ct);

    /// <summary>With a token straight from signing in, before the account is saved.</summary>
    public async Task<IReadOnlyList<MailAlias>> GetAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/settings/sendAs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(L.F("Gmail didn't return the aliases ({0}). Is the Gmail API enabled in your Google Cloud project?", (int)response.StatusCode));

        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static IReadOnlyList<MailAlias> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("sendAs", out var list)) return [];
        return list.EnumerateArray()
            .Where(e => !(e.TryGetProperty("isPrimary", out var p) && p.GetBoolean()))
            .Where(e => !e.TryGetProperty("verificationStatus", out var v) || v.GetString() == "accepted")
            .Select(e => new MailAlias(
                e.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
                e.GetProperty("sendAsEmail").GetString() ?? "",
                e.TryGetProperty("isDefault", out var d) && d.GetBoolean()))
            .Where(a => a.Address.Contains('@'))
            .ToList();
    }
}
