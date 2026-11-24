using Plicbox.Auth;
using Plicbox.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Plicbox.Mail;

public sealed record ServerEndpoint(string Host, int Port, TlsMode Security = TlsMode.Auto);

/// <summary>How to sign in: a password (or app password), or an OAuth access token.</summary>
public sealed record MailLogin(string Username, string? Password = null, string? AccessToken = null);

/// <summary>Opens authenticated IMAP/SMTP connections. TLS is always required; certificates are validated by MailKit.</summary>
public sealed class MailConnector(ICredentialStore credentials, GoogleAuth? google = null, MicrosoftAuth? microsoft = null)
{
    private const int TimeoutMs = 60_000;

    /// <summary>Tests only: lets the local GreenMail server's self-signed certificate through.</summary>
    internal static System.Net.Security.RemoteCertificateValidationCallback? CertificateValidationOverride { get; set; }

    public async Task<ImapClient> ConnectImapAsync(Account a, CancellationToken ct) =>
        await ConnectImapAsync(new ServerEndpoint(a.ImapHost, a.ImapPort, a.ImapSecurity), await LoginForAsync(a, ct), ct);

    public async Task<SmtpClient> ConnectSmtpAsync(Account a, CancellationToken ct) =>
        await ConnectSmtpAsync(new ServerEndpoint(a.SmtpHost, a.SmtpPort, a.SmtpSecurity), await SmtpLoginForAsync(a, ct), ct);

    /// <summary>The outgoing sign-in: the incoming one, a login of its own, or none (servers that relay without one).</summary>
    /// Microsoft and Google accounts too: "same as incoming" sends with their sign-in token, the other two are taken as chosen.
    private async Task<MailLogin?> SmtpLoginForAsync(Account a, CancellationToken ct) => a.SmtpAuth switch
    {
        SmtpAuthMode.None => null,
        SmtpAuthMode.Separate => new MailLogin(
            string.IsNullOrWhiteSpace(a.SmtpUsername) ? a.Username : a.SmtpUsername,
            credentials.GetSmtp(a.Id)
            ?? throw new AuthenticationException(L.F("No password is saved for sending from {0}. Enter it under Accounts > SMTP.", a.Email))),
        _ => await LoginForAsync(a, ct),
    };

    /// <param name="protocol">Records the exchange with the server (account setup keeps it for the log when the test fails).</param>
    public static async Task<ImapClient> ConnectImapAsync(ServerEndpoint server, MailLogin? login, CancellationToken ct, IProtocolLogger? protocol = null)
    {
        var client = new ImapClient(protocol ?? new NullProtocolLogger()) { Timeout = TimeoutMs, ServerCertificateValidationCallback = CertificateValidationOverride };
        try
        {
            await client.ConnectAsync(server.Host, server.Port, SecurityFor(server), ct);
            if (login is not null) await AuthenticateAsync(client, login, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<SmtpClient> ConnectSmtpAsync(ServerEndpoint server, MailLogin? login, CancellationToken ct, IProtocolLogger? protocol = null)
    {
        var client = new SmtpClient(protocol ?? new NullProtocolLogger()) { Timeout = TimeoutMs, ServerCertificateValidationCallback = CertificateValidationOverride };
        try
        {
            await client.ConnectAsync(server.Host, server.Port, SecurityFor(server), ct);
            if (login is not null && client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                await AuthenticateAsync(client, login, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static Task AuthenticateAsync(IMailService client, MailLogin login, CancellationToken ct) =>
        login.AccessToken is { } token
            ? client.AuthenticateAsync(new SaslMechanismOAuth2(login.Username, token), ct)
            : client.AuthenticateAsync(login.Username, login.Password ?? "", ct);

    public static SecureSocketOptions SecurityFor(ServerEndpoint server) => server.Security switch
    {
        TlsMode.Tls => SecureSocketOptions.SslOnConnect,
        TlsMode.StartTls => SecureSocketOptions.StartTls,
        _ => SecurityFor(server.Port),
    };

    /// <summary>Implicit TLS on the TLS ports, mandatory STARTTLS everywhere else (never plaintext).</summary>
    public static SecureSocketOptions SecurityFor(int port) =>
        (port % 1000) is 993 or 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls; // 3993/3465: local test servers

    private async Task<MailLogin> LoginForAsync(Account a, CancellationToken ct)
    {
        if (a.Auth is AuthKind.GoogleOAuth or AuthKind.MicrosoftOAuth)
        {
            try
            {
                var token = a.Auth == AuthKind.GoogleOAuth
                    ? await (google ?? throw new AuthenticationException(L.T("Google sign-in isn't available in this build."))).GetAccessTokenAsync(a.Id, a.Email, ct)
                    : await (microsoft ?? throw new AuthenticationException(L.T("Microsoft sign-in isn't available in this build."))).GetAccessTokenAsync(a.Id, a.Email, ct);
                return new MailLogin(a.Username, AccessToken: token);
            }
            catch (SignInRequiredException ex)
            {
                // Surfaces through the normal auth-failure path: the worker stops retrying and shows the message.
                throw new AuthenticationException(ex.Message, ex);
            }
        }
        var password = credentials.Get(a.Id)
            ?? throw new AuthenticationException(L.F("No password is saved for {0}. Right-click the account and choose \"Sign in again\".", a.Email));
        return new MailLogin(a.Username, password);
    }
}
