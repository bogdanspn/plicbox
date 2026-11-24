using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using DnsClient;
using DnsClient.Protocol;
using Plicbox.Auth;
using System.Xml.Linq;
using Plicbox.Core;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace Plicbox.Mail;

/// <param name="Warning">Something to tell the person before they try (e.g. the provider needs a sign-in Plicbox lacks).</param>
public sealed record DiscoveredSettings(
    ProviderKind Provider, string ImapHost, int ImapPort, string SmtpHost, int SmtpPort, string Username, string Source,
    TlsMode ImapSecurity = TlsMode.Auto, TlsMode SmtpSecurity = TlsMode.Auto, string? Warning = null);

/// <param name="AppPassword">Mail apps sign in with an app password made on the provider's website, not the normal one.</param>
/// <param name="Steps">Where to do it, in a sentence or two.</param>
/// <param name="Url">The provider's page for it, if it has a stable one.</param>
public sealed record ProviderHelp(string Name, bool AppPassword, string Steps, string? Url);

/// <summary>
/// Finds IMAP/SMTP settings for an address: built-in provider table, the provider behind the domain's MX records,
/// the domain's SRV records (RFC 6186), its autoconfig file, Mozilla's ISPDB (for the domain and for its mail host),
/// then probing the usual host names and ports.
/// </summary>
public sealed class AccountDiscovery(HttpClient http, ILogger<AccountDiscovery> log)
{
    /// <summary>Outlook.com, Hotmail and Microsoft 365: only Microsoft sign-in works there, passwords are refused.</summary>
    public static readonly DiscoveredSettings Microsoft = new(ProviderKind.Microsoft, "outlook.office365.com", 993, "smtp.office365.com", 587,
        "%EMAILADDRESS%", "Microsoft", TlsMode.Tls, TlsMode.StartTls);

    /// <summary>
    /// A personal Microsoft account (Outlook.com, Hotmail, Live): the same incoming server, but it sends through
    /// smtp-mail.outlook.com, as Microsoft's own settings page for Outlook.com lists.
    /// </summary>
    public static readonly DiscoveredSettings MicrosoftPersonal = Microsoft with { SmtpHost = "smtp-mail.outlook.com" };

    private static readonly Dictionary<string, DiscoveredSettings> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = new(ProviderKind.Gmail, "imap.gmail.com", 993, "smtp.gmail.com", 465, "%EMAILADDRESS%", "built-in"),
        ["googlemail.com"] = new(ProviderKind.Gmail, "imap.gmail.com", 993, "smtp.gmail.com", 465, "%EMAILADDRESS%", "built-in"),
        ["icloud.com"] = new(ProviderKind.ICloud, "imap.mail.me.com", 993, "smtp.mail.me.com", 587, "%EMAILADDRESS%", "built-in"),
        ["me.com"] = new(ProviderKind.ICloud, "imap.mail.me.com", 993, "smtp.mail.me.com", 587, "%EMAILADDRESS%", "built-in"),
        ["mac.com"] = new(ProviderKind.ICloud, "imap.mail.me.com", 993, "smtp.mail.me.com", 587, "%EMAILADDRESS%", "built-in"),
        ["yahoo.com"] = new(ProviderKind.Yahoo, "imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, "%EMAILADDRESS%", "built-in"),
        ["ymail.com"] = new(ProviderKind.Yahoo, "imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, "%EMAILADDRESS%", "built-in"),
        ["outlook.com"] = Microsoft,
        ["hotmail.com"] = Microsoft,
        ["live.com"] = Microsoft,
        ["msn.com"] = Microsoft,
    };

    public async Task<DiscoveredSettings?> DiscoverAsync(string email, CancellationToken ct)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();

        if (Known.TryGetValue(domain, out var known)) return Fill(known, email);

        // Custom domains hosted by a big provider (Google Workspace, iCloud+, Zoho, …) are recognisable by their MX records.
        var mx = await MxHostsAsync(domain, ct);
        if (FromMx(mx) is { } hosted)
        {
            log.LogInformation("Mail for {Domain} is hosted by {Provider} (MX)", domain, hosted.Source);
            return Fill(hosted, email);
        }

        // The domain's own word on it: SRV records, then its autoconfig file, then Mozilla's database.
        if (await FromSrvAsync(domain, ct) is { } srv) return Fill(srv, email);
        List<string> urls =
        [
            $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}",
            $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml",
            $"https://autoconfig.thunderbird.net/v1.1/{domain}",
        ];
        // Mozilla's database also knows hosting companies by the domain their mail servers are in (e.g. mx00.ionos.de -> ionos.de).
        foreach (var mailDomain in mx.Select(BaseDomain).OfType<string>().Distinct().Where(d => d != domain).Take(2))
            urls.Add($"https://autoconfig.thunderbird.net/v1.1/{mailDomain}");
        foreach (var url in urls)
        {
            if (await TryAutoconfigAsync(url, ct) is { } found) return Fill(found, email);
        }

        return await ProbeAsync(domain, email, ct);
    }

    private async Task<IReadOnlyList<string>> MxHostsAsync(string domain, CancellationToken ct)
    {
        try
        {
            var result = await new LookupClient().QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            return result.Answers.OfType<MxRecord>().OrderBy(r => r.Preference)
                .Select(r => r.Exchange.Value.TrimEnd('.').ToLowerInvariant()).ToList();
        }
        catch (Exception ex) when (ex is DnsResponseException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return [];
        }
    }

    /// <summary>Settings for a domain whose mail a known provider receives.</summary>
    public static DiscoveredSettings? FromMx(IReadOnlyList<string> mxHosts)
    {
        switch (MxProvider(mxHosts))
        {
            case ProviderKind.Gmail: return Known["gmail.com"] with { Source = "Google Workspace" };
            case ProviderKind.ICloud: return Known["icloud.com"] with { Source = "iCloud+" };
            case ProviderKind.Yahoo: return Known["yahoo.com"] with { Source = "Yahoo" };
        }
        foreach (var h in mxHosts)
        {
            if (h.EndsWith(".mail.protection.outlook.com", StringComparison.Ordinal) || h.EndsWith(".olc.protection.outlook.com", StringComparison.Ordinal))
                return Microsoft with { Source = "Microsoft 365" };
            if (h.EndsWith(".messagingengine.com", StringComparison.Ordinal))
                return new(ProviderKind.Imap, "imap.fastmail.com", 993, "smtp.fastmail.com", 465, "%EMAILADDRESS%", "Fastmail", TlsMode.Tls, TlsMode.Tls);
            // Zoho runs a region per country: mx.zoho.eu -> imap.zoho.eu.
            if (System.Text.RegularExpressions.Regex.Match(h, @"(?:^|\.)(zoho(?:\.com(?:\.au)?|\.eu|\.in|\.jp|\.sa|\.uk))$") is { Success: true } zoho)
            {
                var region = zoho.Groups[1].Value;
                return new(ProviderKind.Imap, $"imap.{region}", 993, $"smtp.{region}", 465, "%EMAILADDRESS%", "Zoho Mail", TlsMode.Tls, TlsMode.Tls);
            }
        }
        return null;
    }

    public static ProviderKind? MxProvider(IEnumerable<string> mxHosts)
    {
        foreach (var h in mxHosts)
        {
            if (h.EndsWith("google.com", StringComparison.Ordinal) || h.EndsWith("googlemail.com", StringComparison.Ordinal)) return ProviderKind.Gmail;
            if (h.EndsWith("mail.icloud.com", StringComparison.Ordinal)) return ProviderKind.ICloud;
            if (h.EndsWith("yahoodns.net", StringComparison.Ordinal)) return ProviderKind.Yahoo;
        }
        return null;
    }

    /// <summary>"mx00.ionos.de" -> "ionos.de", "mx1.host.co.uk" -> "host.co.uk".</summary>
    public static string? BaseDomain(string host)
    {
        var parts = host.TrimEnd('.').Split('.');
        if (parts.Length < 2) return null;
        var take = parts.Length >= 3 && parts[^1].Length == 2 && parts[^2] is "co" or "com" or "net" or "org" or "ac" or "gov" or "ne" or "or" ? 3 : 2;
        return string.Join('.', parts[^take..]);
    }

    /// <summary>RFC 6186/8314 SRV records (_imaps/_imap, _submissions/_submission), when the domain publishes them.</summary>
    private async Task<DiscoveredSettings?> FromSrvAsync(string domain, CancellationToken ct)
    {
        var lookup = new LookupClient();
        async Task<(string Host, int Port, TlsMode Security)?> Find(string service, TlsMode security)
        {
            try
            {
                var result = await lookup.QueryAsync($"{service}._tcp.{domain}", QueryType.SRV, cancellationToken: ct);
                var best = result.Answers.OfType<SrvRecord>().OrderBy(r => r.Priority).ThenByDescending(r => r.Weight).FirstOrDefault();
                var host = best?.Target.Value.TrimEnd('.');
                // A target of "." means the service is deliberately not offered.
                return string.IsNullOrEmpty(host) || best!.Port == 0 ? null : (host, best.Port, security);
            }
            catch (Exception ex) when (ex is DnsResponseException or System.Net.Sockets.SocketException or TimeoutException)
            {
                return null;
            }
        }
        var lookups = await Task.WhenAll(Find("_imaps", TlsMode.Tls), Find("_imap", TlsMode.StartTls),
            Find("_submissions", TlsMode.Tls), Find("_submission", TlsMode.StartTls));
        var imap = lookups[0] ?? lookups[1];
        var smtp = lookups[2] ?? lookups[3];
        if (imap is not { } i || smtp is not { } s) return null;
        log.LogInformation("Mail settings for {Domain} found in its SRV records", domain);
        return new DiscoveredSettings(ProviderFor(i.Host), i.Host, i.Port, s.Host, s.Port, "%EMAILADDRESS%", L.T("DNS records"), i.Security, s.Security);
    }

    private async Task<DiscoveredSettings?> TryAutoconfigAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var xml = await response.Content.ReadAsStringAsync(timeout.Token);
            var parsed = ParseAutoconfig(xml);
            if (parsed is not null) log.LogInformation("Mail settings found via {Url}", url);
            return parsed is null ? null : parsed with { Source = new Uri(url).Host };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Parses a Thunderbird autoconfig (clientConfig v1.1) document; prefers SSL/STARTTLS servers.</summary>
    public static DiscoveredSettings? ParseAutoconfig(string xml)
    {
        var doc = XDocument.Parse(xml);
        var provider = doc.Root?.Element("emailProvider");
        if (provider is null) return null;

        var imap = provider.Elements("incomingServer").Where(e => (string?)e.Attribute("type") == "imap")
            .OrderBy(e => SecurityRank((string?)e.Element("socketType"))).FirstOrDefault();
        var smtp = provider.Elements("outgoingServer").Where(e => (string?)e.Attribute("type") == "smtp")
            .OrderBy(e => SecurityRank((string?)e.Element("socketType"))).FirstOrDefault();
        if (imap is null || smtp is null) return null;
        if (SecurityRank((string?)imap.Element("socketType")) > 1 || SecurityRank((string?)smtp.Element("socketType")) > 1) return null;

        var imapHost = (string?)imap.Element("hostname") ?? "";
        return new DiscoveredSettings(
            ProviderFor(imapHost),
            imapHost, (int?)imap.Element("port") ?? 993,
            (string?)smtp.Element("hostname") ?? "", (int?)smtp.Element("port") ?? 465,
            (string?)imap.Element("username") ?? "%EMAILADDRESS%",
            L.T("autoconfig"),
            SecurityOf((string?)imap.Element("socketType")), SecurityOf((string?)smtp.Element("socketType")));
    }

    private static int SecurityRank(string? socketType) => socketType?.ToUpperInvariant() switch
    {
        "SSL" => 0,
        "STARTTLS" => 1,
        _ => 2, // plain: never used
    };

    private static TlsMode SecurityOf(string? socketType) => socketType?.ToUpperInvariant() == "SSL" ? TlsMode.Tls : TlsMode.StartTls;

    /// <summary>
    /// Last resort: the host names shared hosting (cPanel, Plesk) typically uses, on both kinds of encrypted port.
    /// Everything is tried at once (so a dead name costs one timeout, not one each); the first in order that answers wins.
    /// </summary>
    private async Task<DiscoveredSettings?> ProbeAsync(string domain, string email, CancellationToken ct)
    {
        string[] hosts = [$"mail.{domain}", $"imap.{domain}", domain];
        var imap = await FirstReachableAsync(
            hosts.SelectMany(h => new[] { new ServerEndpoint(h, 993, TlsMode.Tls), new ServerEndpoint(h, 143, TlsMode.StartTls) }),
            e => CanConnectAsync(e, imap: true, ct));
        if (imap is null) return null;

        string[] smtpHosts = imap.Host.StartsWith("imap.", StringComparison.Ordinal)
            ? ["smtp." + imap.Host[5..], imap.Host]
            : [imap.Host, $"smtp.{domain}"];
        var smtp = await FirstReachableAsync(
            smtpHosts.SelectMany(h => new[] { new ServerEndpoint(h, 465, TlsMode.Tls), new ServerEndpoint(h, 587, TlsMode.StartTls) }),
            e => CanConnectAsync(e, imap: false, ct))
            ?? new ServerEndpoint(smtpHosts[0], 465, TlsMode.Tls); // a guess the person can correct

        log.LogInformation("Mail settings guessed by probing {Imap}:{ImapPort} and {Smtp}:{SmtpPort}", imap.Host, imap.Port, smtp.Host, smtp.Port);
        return new DiscoveredSettings(ProviderKind.Imap, imap.Host, imap.Port, smtp.Host, smtp.Port, email, L.T("probe"), imap.Security, smtp.Security);
    }

    private static async Task<ServerEndpoint?> FirstReachableAsync(IEnumerable<ServerEndpoint> candidates, Func<ServerEndpoint, Task<bool>> reachable)
    {
        var tries = candidates.Select(c => (Endpoint: c, Task: reachable(c))).ToList();
        foreach (var (endpoint, task) in tries)
            if (await task) return endpoint;
        return null;
    }

    private static async Task<bool> CanConnectAsync(ServerEndpoint endpoint, bool imap, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            if (imap)
            {
                using var client = await MailConnector.ConnectImapAsync(endpoint, null, timeout.Token);
                await client.DisconnectAsync(true, CancellationToken.None);
            }
            else
            {
                using var client = await MailConnector.ConnectSmtpAsync(endpoint, null, timeout.Token);
                await client.DisconnectAsync(true, CancellationToken.None);
            }
            return true;
        }
        catch (SslHandshakeException)
        {
            return true; // server exists; sign-in will explain the certificate problem (and offer its real name)
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static DiscoveredSettings Fill(DiscoveredSettings s, string email)
    {
        var at = email.LastIndexOf('@');
        var username = s.Username
            .Replace("%EMAILADDRESS%", email, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILLOCALPART%", email[..at], StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILDOMAIN%", email[(at + 1)..], StringComparison.OrdinalIgnoreCase);
        return s with { Username = username };
    }

    public static ProviderKind ProviderFor(string imapHost) => imapHost.ToLowerInvariant() switch
    {
        "imap.gmail.com" => ProviderKind.Gmail,
        "imap.mail.me.com" => ProviderKind.ICloud,
        var h when h.EndsWith("yahoo.com", StringComparison.Ordinal) => ProviderKind.Yahoo,
        "outlook.office365.com" => ProviderKind.Microsoft,
        var h when h.EndsWith(".outlook.com", StringComparison.Ordinal) => ProviderKind.Microsoft,
        _ => ProviderKind.Imap,
    };

    /// <summary>
    /// What a provider wants done on its website before a mail app can sign in (an app password, IMAP switched on),
    /// by its incoming or outgoing server; null when nothing is known to be needed.
    /// </summary>
    public static ProviderHelp? HelpFor(string host)
    {
        var h = "." + host.Trim().ToLowerInvariant();
        if (h.EndsWith(".gmail.com", StringComparison.Ordinal))
            return new("Google", true, L.T("Turn on 2-Step Verification for your Google account, then create one and paste it here."),
                "https://myaccount.google.com/apppasswords");
        if (h.EndsWith(".mail.me.com", StringComparison.Ordinal))
            return new("Apple", true, L.T("At account.apple.com: Sign-In and Security > App-Specific Passwords. It needs two-factor authentication."),
                "https://account.apple.com/");
        if (h.EndsWith(".yahoo.com", StringComparison.Ordinal))
            return new("Yahoo", true, L.F("On {0}'s Account Security page, create an app password and paste it here.", "Yahoo"),
                "https://login.yahoo.com/account/security");
        if (h.EndsWith(".aol.com", StringComparison.Ordinal))
            return new("AOL", true, L.F("On {0}'s Account Security page, create an app password and paste it here.", "AOL"),
                "https://login.aol.com/account/security");
        if (h.EndsWith(".fastmail.com", StringComparison.Ordinal))
            return new("Fastmail", true, L.T("In Fastmail: Settings > Privacy & Security > Manage app passwords and access > New app password."),
                "https://app.fastmail.com/settings/security");
        // GMX and WEB.DE switch mail apps off for new accounts; their settings are in German.
        if (h.EndsWith(".web.de", StringComparison.Ordinal) || h.EndsWith(".gmx.net", StringComparison.Ordinal))
        {
            var name = h.EndsWith(".web.de", StringComparison.Ordinal) ? "WEB.DE" : "GMX";
            return new(name, false, L.F("{0} only lets mail apps in once you allow it: Einstellungen > E-Mail > POP3/IMAP Abruf, then tick POP3 und IMAP Zugriff erlauben.", name), null);
        }
        // Zoho: IMAP has to be switched on, and with two-factor sign-in only an app-specific password works (each region
        // has its own accounts site: accounts.zoho.eu, accounts.zoho.in, ...).
        if (System.Text.RegularExpressions.Regex.Match(h, @"\.(zoho(?:\.com(?:\.au)?|\.eu|\.in|\.jp|\.sa|\.uk))$") is { Success: true } zoho)
            return new("Zoho", false, L.T("Zoho Mail needs IMAP switched on first: Settings > Mail Accounts > IMAP Access. With two-factor authentication on, use an app-specific password here, not your normal one. Zoho's free plan may not offer IMAP."),
                $"https://accounts.{zoho.Groups[1].Value}/home#security/app_password");
        return null;
    }

    /// <summary>
    /// The usual shared-hosting snag: the server's certificate is valid, just issued to the host's own name
    /// (server123.host.example) rather than mail.yourdomain. Returns that name when switching to it would work.
    /// </summary>
    public static string? CertificateHost(SslHandshakeException ex, string attemptedHost)
    {
        if (ex.ServerCertificate is not { } raw) return null;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(raw.GetRawCertData());
            // Only worth suggesting when the certificate is otherwise good: a self-signed one fails under any name.
            using var chain = new X509Chain { ChainPolicy = { RevocationMode = X509RevocationMode.NoCheck } };
            if (!chain.Build(cert)) return null;
            return CertificateNames(cert).FirstOrDefault(n => !n.StartsWith('*') && !string.Equals(n, attemptedHost, StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The host names a certificate covers (its alternative names, else its common name).</summary>
    public static IReadOnlyList<string> CertificateNames(X509Certificate2 cert)
    {
        var names = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        if (names.Count == 0 && cert.GetNameInfo(X509NameType.DnsName, false) is { Length: > 0 } common) names.Add(common);
        return names;
    }

    /// <summary>Turns connection/auth failures into something a person can act on.</summary>
    /// <param name="auth">How the account signs in: a rejected password can be the provider wanting an app password.</param>
    public static string Explain(Exception ex, string host, AuthKind auth = AuthKind.Password) => ex switch
    {
        AuthenticationException { InnerException: SignInRequiredException s } => s.Message,
        SignInRequiredException s => s.Message,
        AuthenticationException when auth == AuthKind.Password && HelpFor(host) is { AppPassword: true } help =>
            L.F("{0} turned down the password. Mail apps need an app password there, not your normal password.", help.Name),
        AuthenticationException when auth == AuthKind.Password && HelpFor(host) is { } help =>
            L.T("The server rejected the user name or password.") + " " + help.Steps,
        // Outlook.com's 5.7.139: the sign-in is fine, but Microsoft won't take mail from apps for this mailbox (no setting for it).
        AuthenticationException a when a.Message.Contains("SmtpClientAuthentication is disabled", StringComparison.OrdinalIgnoreCase) =>
            L.T("Outlook won't let mail apps send from this account: Microsoft has switched it off for the mailbox. Send it from outlook.com instead."),
        // Signed in with Google or Microsoft: no password involved, the mail server turned the sign-in down.
        AuthenticationException when auth != AuthKind.Password =>
            L.T("The mail server turned down the sign-in. Sign in again, and check that mail apps (IMAP) are allowed for this account."),
        AuthenticationException => L.T("The server rejected the user name or password."),
        SslHandshakeException ssl when CertificateHost(ssl, host) is { } real =>
            L.F("The security certificate of {0} is issued to {1}. Use {1} as the server name instead.", host, real),
        SslHandshakeException => L.F("The security certificate for {0} isn't valid for that name. Shared hosts often use the server's own name instead (see your host's \"Connect Devices\" page).", host),
        System.Net.Sockets.SocketException => L.F("Couldn't reach {0}. Check the server name and port.", host),
        OperationCanceledException or TimeoutException => L.F("{0} didn't answer in time.", host),
        _ => ex.Message,
    };
}
