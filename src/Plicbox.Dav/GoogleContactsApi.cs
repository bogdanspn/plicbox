using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plicbox.Auth;
using Plicbox.Core;

namespace Plicbox.Dav;

/// <summary>Google stopped accepting the sync token (they last a week): start over with a full read.</summary>
public sealed class SyncTokenExpiredException() : Exception("The contacts sync token expired.");

/// <summary>One page-through of a contact list: what changed, what was deleted, and the token for next time.</summary>
public sealed record ContactChanges(IReadOnlyList<Contact> Changed, IReadOnlyList<string> Deleted, string? NextSyncToken, bool IsFull);

/// <summary>Google People API v1: your contacts, and the "other contacts" Gmail saves when you write to someone.</summary>
public sealed class GoogleContactsApi(HttpClient http, GoogleAuth google)
{
    private const string Base = "https://people.googleapis.com/v1";
    private const string PersonFields = "names,nicknames,emailAddresses,phoneNumbers,organizations,birthdays,photos,biographies,urls,addresses,memberships,metadata";
    private const string UpdateFields = "names,nicknames,emailAddresses,phoneNumbers,organizations,birthdays,biographies,urls,addresses";

    /// <summary>Your contacts: everything (no token) or what changed since <paramref name="syncToken"/>.</summary>
    public Task<ContactChanges> ListContactsAsync(Account account, string? syncToken, CancellationToken ct) =>
        ListAsync(account, $"{Base}/people/me/connections?personFields={PersonFields}&pageSize=1000&requestSyncToken=true", "connections",
            ContactSource.Google, syncToken, ct);

    /// <summary>Gmail's automatically saved addresses ("Other contacts"): read-only, but they can be saved to your contacts.</summary>
    public Task<ContactChanges> ListOtherContactsAsync(Account account, string? syncToken, CancellationToken ct) =>
        ListAsync(account, $"{Base}/otherContacts?readMask=names,emailAddresses,phoneNumbers,photos,metadata&pageSize=1000&requestSyncToken=true",
            "otherContacts", ContactSource.GoogleOther, syncToken, ct);

    private async Task<ContactChanges> ListAsync(Account account, string url, string itemsProperty, ContactSource source, string? syncToken, CancellationToken ct)
    {
        var changed = new List<Contact>();
        var deleted = new List<string>();
        string? page = null, next = null;
        do
        {
            var pageUrl = url + (syncToken is null ? "" : $"&syncToken={Uri.EscapeDataString(syncToken)}")
                              + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}");
            using var doc = await SendAsync(HttpMethod.Get, pageUrl, account, null, ct);
            if (doc.RootElement.TryGetProperty(itemsProperty, out var items))
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("metadata", out var meta) && Bool(meta, "deleted"))
                    {
                        if (Str(item, "resourceName") is { } gone) deleted.Add(gone);
                    }
                    else if (Parse(item, source) is { } contact) changed.Add(contact);
                }
            page = Str(doc.RootElement, "nextPageToken");
            next = Str(doc.RootElement, "nextSyncToken") ?? next;
        } while (page is not null);
        return new ContactChanges(changed, deleted, next, syncToken is null);
    }

    public async Task<Contact> CreateAsync(Account account, Contact contact, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{Base}/people:createContact?personFields={PersonFields}", account, ToJson(contact), ct);
        return Parse(doc.RootElement, ContactSource.Google)!;
    }

    public async Task<Contact> UpdateAsync(Account account, Contact contact, CancellationToken ct)
    {
        var body = ToJson(contact);
        body["etag"] = contact.Etag;
        using var doc = await SendAsync(HttpMethod.Patch,
            $"{Base}/{contact.RemoteId}:updateContact?updatePersonFields={UpdateFields}&personFields={PersonFields}", account, body, ct);
        return Parse(doc.RootElement, ContactSource.Google)!;
    }

    public async Task DeleteAsync(Account account, string resourceName, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"{Base}/{resourceName}:deleteContact", account, null, ct);
    }

    /// <summary>"Save" on an automatically saved address: Google copies it into your contacts.</summary>
    public async Task<Contact> SaveOtherContactAsync(Account account, string resourceName, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{Base}/{resourceName}:copyOtherContactToMyContactsGroup", account,
            new JsonObject { ["copyMask"] = "names,emailAddresses,phoneNumbers" }, ct);
        return Parse(doc.RootElement, ContactSource.Google)!;
    }

    /// <summary>Google's "starred" group is what Favorites means.</summary>
    public async Task SetStarredAsync(Account account, string resourceName, bool starred, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"{Base}/contactGroups/starred/members:modify", account,
            new JsonObject { [starred ? "resourceNamesToAdd" : "resourceNamesToRemove"] = new JsonArray(resourceName) }, ct);
    }

    // ───────── JSON ↔ model ─────────

    internal static Contact? Parse(JsonElement p, ContactSource source)
    {
        if (Str(p, "resourceName") is not { } resource) return null;
        var name = Primary(p, "names");
        var org = Primary(p, "organizations");
        var emails = Values(p, "emailAddresses", "value");
        var display = name is { } n ? Str(n, "displayName") : null;
        display ??= org is { } o ? Str(o, "name") : null;
        display ??= emails.FirstOrDefault()?.Value ?? "";

        DateOnly? birthday = null;
        if (Primary(p, "birthdays") is { } b && b.TryGetProperty("date", out var date) && Int(date, "month") is { } month && Int(date, "day") is { } day)
        {
            try { birthday = new DateOnly(Int(date, "year") ?? Contact.NoYear, month, day); }
            catch (ArgumentOutOfRangeException) { }
        }

        var photo = p.TryGetProperty("photos", out var photos)
            ? photos.EnumerateArray().Where(ph => !Bool(ph, "default")).Select(ph => Str(ph, "url")).FirstOrDefault(u => u is not null)
            : null;
        var starred = p.TryGetProperty("memberships", out var ms) && ms.EnumerateArray().Any(m =>
            m.TryGetProperty("contactGroupMembership", out var g) && Str(g, "contactGroupResourceName") == "contactGroups/starred");
        var updated = p.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("sources", out var sources)
            ? sources.EnumerateArray().Select(s => Str(s, "updateTime")).Where(t => t is not null)
                .Select(t => DateTimeOffset.Parse(t!, CultureInfo.InvariantCulture)).DefaultIfEmpty(DateTimeOffset.UtcNow).Max()
            : DateTimeOffset.UtcNow;

        var addresses = p.TryGetProperty("addresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array
            ? addrs.EnumerateArray().Select(a => new PostalAddress(Str(a, "streetAddress"), Str(a, "city"), Str(a, "postalCode"), Str(a, "region"),
                    Str(a, "country"), Str(a, "type")) is { IsEmpty: false } parts ? parts
                    : Str(a, "formattedValue") is { } text ? new PostalAddress(text, null, null, null, null, Str(a, "type")) : null)
                .OfType<PostalAddress>().ToList()
            : [];

        return new Contact
        {
            RemoteId = resource, Etag = Str(p, "etag"), Source = source, DisplayName = display,
            Nickname = Primary(p, "nicknames") is { } nick ? Str(nick, "value") : null,
            Department = org is { } o4 ? Str(o4, "department") : null,
            Addresses = addresses,
            GivenName = name is { } gn ? Str(gn, "givenName") : null, FamilyName = name is { } fn ? Str(fn, "familyName") : null,
            Organization = org is { } o2 ? Str(o2, "name") : null, JobTitle = org is { } o3 ? Str(o3, "title") : null,
            Emails = emails, Phones = Values(p, "phoneNumbers", "value"), Websites = Values(p, "urls", "value"),
            Birthday = birthday, Notes = Primary(p, "biographies") is { } bio ? Str(bio, "value") : null,
            PhotoUrl = photo, Address = Primary(p, "addresses") is { } a ? Str(a, "formattedValue") : null,
            IsFavorite = starred, Updated = updated,
        };
    }

    internal static JsonObject ToJson(Contact c)
    {
        var body = new JsonObject
        {
            ["names"] = new JsonArray(new JsonObject
            {
                ["givenName"] = c.GivenName ?? (c.FamilyName is null ? c.DisplayName : null),
                ["familyName"] = c.FamilyName,
            }),
            ["emailAddresses"] = Labeled(c.Emails),
            ["phoneNumbers"] = Labeled(c.Phones),
            ["urls"] = Labeled(c.Websites),
            ["nicknames"] = string.IsNullOrWhiteSpace(c.Nickname) ? new JsonArray() : new JsonArray(new JsonObject { ["value"] = c.Nickname }),
            ["organizations"] = c.Organization is null && c.JobTitle is null && c.Department is null
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["name"] = c.Organization, ["title"] = c.JobTitle, ["department"] = c.Department }),
            ["biographies"] = string.IsNullOrWhiteSpace(c.Notes)
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["value"] = c.Notes, ["contentType"] = "TEXT_PLAIN" }),
            // In parts, so Google (and phones) can show and map them properly.
            ["addresses"] = new JsonArray(c.Addresses.Where(a => !a.IsEmpty).Select(a => (JsonNode)new JsonObject
            {
                ["streetAddress"] = a.Street, ["city"] = a.City, ["postalCode"] = a.PostalCode, ["region"] = a.Region, ["country"] = a.Country,
                ["type"] = a.Label,
            }).ToArray()),
        };
        if (c.Birthday is { } b)
        {
            var date = new JsonObject { ["month"] = b.Month, ["day"] = b.Day };
            if (c.BirthdayHasYear) date["year"] = b.Year;
            body["birthdays"] = new JsonArray(new JsonObject { ["date"] = date });
        }
        else
        {
            body["birthdays"] = new JsonArray();
        }
        return body;
    }

    private static JsonArray Labeled(IEnumerable<LabeledValue> values) =>
        new(values.Where(v => v.Value.Trim().Length > 0)
            .Select(v => (JsonNode)new JsonObject { ["value"] = v.Value.Trim(), ["type"] = v.Label }).ToArray());

    private static JsonElement? Primary(JsonElement p, string property)
    {
        if (!p.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
        foreach (var item in arr.EnumerateArray())
            if (item.TryGetProperty("metadata", out var m) && Bool(m, "primary")) return item;
        return arr[0];
    }

    private static IReadOnlyList<LabeledValue> Values(JsonElement p, string property, string field) =>
        p.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => Str(e, field) is { } v ? new LabeledValue(v, Str(e, "formattedType") ?? Str(e, "type")) : null)
                .OfType<LabeledValue>().ToList()
            : [];

    // ───────── HTTP ─────────

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, Account account, JsonNode? body, CancellationToken ct)
    {
        var token = await google.GetAccessTokenAsync(account.Id, account.Email, ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);

        var reason = ErrorReason(text);
        throw response.StatusCode switch
        {
            HttpStatusCode.Gone => new SyncTokenExpiredException(),
            HttpStatusCode.BadRequest when reason == "EXPIRED_SYNC_TOKEN" || text.Contains("EXPIRED_SYNC_TOKEN", StringComparison.Ordinal) => new SyncTokenExpiredException(),
            HttpStatusCode.Unauthorized => new GoogleSignInRequiredException(L.F("Google sign-in for {0} has expired. Right-click the account and choose \"Sign in again\".", account.Email)),
            HttpStatusCode.Forbidden when reason is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT" or "PERMISSION_DENIED" && !text.Contains("SERVICE_DISABLED", StringComparison.Ordinal) =>
                new CalendarPermissionException(L.F("{0} hasn't given Plicbox access to contacts yet.", account.Email)),
            HttpStatusCode.Forbidden when reason is "accessNotConfigured" or "SERVICE_DISABLED" || text.Contains("SERVICE_DISABLED", StringComparison.Ordinal) =>
                new CalendarApiDisabledException(L.T("The Google People API is switched off in your Google Cloud project. Enable it under APIs & Services > Library > Google People API.")),
            _ => new HttpRequestException(L.F("Google Contacts answered {0}: {1}", (int)response.StatusCode, ErrorMessage(text) ?? response.ReasonPhrase), null, response.StatusCode),
        };
    }

    private static string? ErrorReason(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var err = doc.RootElement.GetProperty("error");
            if (err.TryGetProperty("details", out var details))
                foreach (var d in details.EnumerateArray())
                    if (Str(d, "reason") is { } r) return r;
            if (err.TryGetProperty("errors", out var errors))
                foreach (var e in errors.EnumerateArray())
                    if (Str(e, "reason") is { } r) return r;
            return Str(err, "status");
        }
        catch (Exception) { return null; }
    }

    private static string? ErrorMessage(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement.GetProperty("error"), "message"); }
        catch (Exception) { return null; }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static string? Str(JsonElement? e, string name) => e is { } x ? Str(x, name) : null;
}
