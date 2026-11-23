using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Plicbox.Core;

namespace Plicbox.Auth;

/// <summary>
/// Who signed in, the key to store for the account (its entry in the token cache), a token to test with, and whether
/// it's a personal account (Outlook.com, Hotmail, Live) rather than a work or school one.
/// </summary>
public sealed record MicrosoftSignInResult(string AccountKey, string Email, string? Name, string AccessToken, bool IsPersonal);

/// <summary>A Microsoft account needs signing in again.</summary>
public sealed class MicrosoftSignInRequiredException(string message, Exception? inner = null) : SignInRequiredException(message, inner);

/// <summary>
/// "Sign in with Microsoft" for Outlook.com, Hotmail and Microsoft 365: the system browser with a loopback redirect,
/// through Plicbox's own app registration (a public client, so there's no secret to keep). MSAL keeps the tokens in one
/// cache file encrypted for the Windows user and refreshes them; each account stores only its key into that cache.
/// </summary>
public sealed class MicrosoftAuth(ICredentialStore credentials, string cacheFilePath)
{
    /// <summary>Plicbox's app registration in Microsoft Entra (personal and work accounts).</summary>
    public const string ClientId = "f066c4e8-88af-44cb-999d-f5a843885963";

    /// <summary>IMAP and SMTP on Outlook.com / Exchange Online: one token covers both. MSAL adds offline_access, openid and profile.</summary>
    public static readonly string[] Scopes = ["https://outlook.office.com/IMAP.AccessAsUser.All", "https://outlook.office.com/SMTP.Send"];

    /// <summary>The directory every personal Microsoft account belongs to (Outlook.com, Hotmail, Live).</summary>
    public const string PersonalAccountsTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    public static string DefaultCacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plicbox", "microsoft-tokens.bin");

    private readonly SemaphoreSlim _building = new(1, 1);
    private IPublicClientApplication? _app;

    /// <summary>
    /// Opens the browser for sign-in and consent. With <paramref name="email"/> the account is suggested; the address
    /// and name come back from Microsoft's ID token.
    /// </summary>
    public async Task<MicrosoftSignInResult> SignInAsync(string? email, CancellationToken ct)
    {
        var app = await AppAsync();
        var request = app.AcquireTokenInteractive(Scopes)
            .WithUseEmbeddedWebView(false)
            .WithSystemWebViewOptions(new SystemWebViewOptions { HtmlMessageSuccess = BrowserPages.SignedIn, HtmlMessageError = BrowserPages.SignInFailed });
        if (email is not null) request = request.WithLoginHint(email);

        AuthenticationResult result;
        try
        {
            result = await request.ExecuteAsync(ct);
        }
        catch (MsalServiceException ex) when (ex.ErrorCode == "access_denied")
        {
            throw new MicrosoftSignInRequiredException(
                L.T("Microsoft didn't give Plicbox access. For a work or school account, your organization may have to approve Plicbox first."), ex);
        }
        var name = result.ClaimsPrincipal?.FindFirst("name")?.Value;
        return new MicrosoftSignInResult(result.Account.HomeAccountId.Identifier, result.Account.Username, name, result.AccessToken,
            IsPersonal: result.Account.HomeAccountId.TenantId == PersonalAccountsTenant);
    }

    /// <summary>A valid access token for the account, refreshed through the cache when it has expired.</summary>
    public async Task<string> GetAccessTokenAsync(long accountId, string email, CancellationToken ct)
    {
        var app = await AppAsync();
        if (credentials.Get(accountId) is not { } key || await app.GetAccountAsync(key) is not { } account)
            throw new MicrosoftSignInRequiredException(L.F("Sign in to Microsoft again for {0}.", email));
        try
        {
            return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct)).AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new MicrosoftSignInRequiredException(
                L.F("Microsoft sign-in for {0} has expired or was revoked. Right-click the account and choose \"Sign in again\".", email), ex);
        }
    }

    private async Task<IPublicClientApplication> AppAsync()
    {
        if (_app is not null) return _app;
        await _building.WaitAsync();
        try
        {
            if (_app is not null) return _app;
            var app = PublicClientApplicationBuilder.Create(ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
                .WithRedirectUri("http://localhost")
                .Build();
            // Encrypted for this Windows user (DPAPI), and safe to share with a dev build running beside the installed app.
            var folder = Path.GetDirectoryName(cacheFilePath)!;
            Directory.CreateDirectory(folder);
            var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder(Path.GetFileName(cacheFilePath), folder).Build());
            cache.RegisterCache(app.UserTokenCache);
            return _app = app;
        }
        finally
        {
            _building.Release();
        }
    }
}
