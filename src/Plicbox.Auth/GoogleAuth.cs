using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using Plicbox.Core;

namespace Plicbox.Auth;

/// <summary>Who signed in, plus the token JSON to store for the account.</summary>
public sealed record GoogleSignInResult(string TokenJson, string Email, string? Name);

/// <summary>Thrown when an account needs the user to sign in again (revoked, expired or missing token).</summary>
public class SignInRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A Google account needs signing in again.</summary>
public sealed class GoogleSignInRequiredException(string message, Exception? inner = null) : SignInRequiredException(message, inner);

/// <summary>
/// "Sign in with Google" for Gmail and Google Workspace: browser-based OAuth with a loopback redirect
/// (the desktop-app flow), with PKCE and a state check as Google's OAuth best practices ask. Uses the user's own OAuth client (google-client.json) if they set one up, else the one
/// built into Plicbox, so nothing depends on a third-party service. Tokens are stored per account in the
/// credential store and refreshed automatically.
/// </summary>
public sealed class GoogleAuth(ICredentialStore credentials, string clientFilePath)
{
    /// <summary>
    /// Full IMAP/SMTP access, plus the basic profile so the account can be added without typing anything.
    /// Calendar and contacts scopes will be added with those features.
    /// </summary>
    public static readonly string[] Scopes =
        ["https://mail.google.com/", "https://www.googleapis.com/auth/calendar", "https://www.googleapis.com/auth/contacts",
         "https://www.googleapis.com/auth/contacts.other.readonly", "https://www.googleapis.com/auth/tasks", "openid", "email", "profile"];

    public static string DefaultClientFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plicbox", "google-client.json");

    // Browser sign-ins still waiting for the user, with the address each one asked for (null: any account).
    private readonly List<(string? Email, TaskCompletionSource<GoogleSignInResult> Done)> _waiting = [];

    private const string BuiltInClient = "google-client.json";

    public bool IsConfigured => File.Exists(clientFilePath) || typeof(GoogleAuth).Assembly.GetManifestResourceInfo(BuiltInClient) is not null;

    /// <summary>Validates and installs the OAuth client JSON downloaded from Google Cloud Console.</summary>
    public void ImportClientFile(string path)
    {
        try
        {
            var secrets = GoogleClientSecrets.FromFile(path).Secrets;
            if (string.IsNullOrEmpty(secrets.ClientId)) throw new FormatException();
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new FormatException(L.T("That isn't a Google OAuth client file. Download the JSON of a \"Desktop app\" client from Google Cloud Console."), ex);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(clientFilePath)!);
        File.Copy(path, clientFilePath, overwrite: true);
    }

    /// <summary>
    /// Opens the browser for consent. With <paramref name="email"/> null the user picks any Google account;
    /// the address and name come back from Google's verified ID token. Several can wait at once ("Sign in again"
    /// and Add account each open a tab): whichever tab the user finishes also ends the others waiting for that
    /// account, or for any account, instead of leaving them waiting for a tab nobody will use.
    /// </summary>
    public async Task<GoogleSignInResult> SignInAsync(string? email, CancellationToken ct)
    {
        (string? Email, TaskCompletionSource<GoogleSignInResult> Done) waiting = (email, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_waiting) _waiting.Add(waiting);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var own = BrowserSignInAsync(email, stop.Token);
            if (await Task.WhenAny(own, waiting.Done.Task) == own)
            {
                var result = await own;
                lock (_waiting)
                    foreach (var (wanted, done) in _waiting)
                        if (wanted is null || string.Equals(wanted, result.Email, StringComparison.OrdinalIgnoreCase))
                            done.TrySetResult(result);
                return result;
            }
            stop.Cancel(); // signed in from another tab: this tab's listener isn't needed any more
            return await waiting.Done.Task;
        }
        finally
        {
            lock (_waiting) _waiting.Remove(waiting);
        }
    }

    private async Task<GoogleSignInResult> BrowserSignInAsync(string? email, CancellationToken ct)
    {
        var flow = CreateFlow(new NullDataStore(), email);
        var receiver = new LocalServerCodeReceiver(BrowserPages.SignedIn);
        var redirectUri = receiver.RedirectUri;

        // PKCE: only this app, holding the verifier, can turn the code into tokens. State: the answer on the loopback
        // port belongs to this sign-in, not to a page that called it.
        var request = flow.CreateAuthorizationCodeRequest(redirectUri, out var codeVerifier);
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        request.State = state;
        var answer = await receiver.ReceiveCodeAsync(request, ct);
        if (!string.IsNullOrEmpty(answer.Error)) throw new TokenResponseException(new TokenErrorResponse(answer));
        if (answer.State != state || string.IsNullOrEmpty(answer.Code))
            throw new GoogleSignInRequiredException(L.T("Google's answer didn't match this sign-in. Please try again."));
        var token = await flow.ExchangeCodeForTokenAsync(email ?? "new-account", answer.Code, codeVerifier, redirectUri, ct);
        if (string.IsNullOrEmpty(token.RefreshToken))
            throw new GoogleSignInRequiredException(L.T("Google didn't grant offline access. Please try again."));

        var payload = await GoogleJsonWebSignature.ValidateAsync(token.IdToken, new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [LoadClient().ClientId],
        });
        if (payload.EmailVerified != true || string.IsNullOrEmpty(payload.Email))
            throw new GoogleSignInRequiredException(L.T("Google didn't confirm the account's email address."));

        // The ID token is only needed once; dropping it keeps the stored secret under Credential Manager's size limit.
        token.IdToken = null;
        return new GoogleSignInResult(NewtonsoftJsonSerializer.Instance.Serialize(token), payload.Email, payload.Name);
    }

    /// <summary>A valid access token for the account, refreshing (and persisting) it when expired.</summary>
    public async Task<string> GetAccessTokenAsync(long accountId, string email, CancellationToken ct)
    {
        if (!IsConfigured) throw new GoogleSignInRequiredException(L.T("The Google sign-in client file is missing. Set up Google sign-in again."));
        var json = credentials.Get(accountId) ?? throw new GoogleSignInRequiredException(L.F("Sign in to Google again for {0}.", email));

        TokenResponse token;
        try { token = NewtonsoftJsonSerializer.Instance.Deserialize<TokenResponse>(json); }
        catch (Exception ex) { throw new GoogleSignInRequiredException(L.F("Sign in to Google again for {0}.", email), ex); }

        var flow = CreateFlow(new AccountDataStore(credentials, accountId), email);
        var credential = new UserCredential(flow, email, token);
        try
        {
            return await credential.GetAccessTokenForRequestAsync(cancellationToken: ct);
        }
        catch (TokenResponseException ex)
        {
            throw new GoogleSignInRequiredException(
                L.F("Google sign-in for {0} has expired or was revoked. Right-click the account and choose \"Sign in again\".", email), ex);
        }
    }

    private PkceGoogleAuthorizationCodeFlow CreateFlow(IDataStore store, string? email) => new(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = LoadClient(),
        Scopes = Scopes,
        DataStore = store,
        LoginHint = email,
        // "consent" guarantees a refresh token even when re-authorising; no hint means let the user pick the account.
        Prompt = email is null ? "select_account consent" : "consent",
    });

    /// <summary>The user's own client wins (its accounts' tokens belong to it); otherwise the one Plicbox was built with.</summary>
    private ClientSecrets LoadClient()
    {
        if (File.Exists(clientFilePath)) return GoogleClientSecrets.FromFile(clientFilePath).Secrets;
        using var builtIn = typeof(GoogleAuth).Assembly.GetManifestResourceStream(BuiltInClient)
            ?? throw new GoogleSignInRequiredException(L.T("The Google sign-in client file is missing. Set up Google sign-in again."));
        return GoogleClientSecrets.FromStream(builtIn).Secrets;
    }

    /// <summary>Lets the Google library persist refreshed tokens straight into the account's credential.</summary>
    private sealed class AccountDataStore(ICredentialStore credentials, long accountId) : IDataStore
    {
        public Task StoreAsync<T>(string key, T value)
        {
            // Google sends a fresh ID token with every refresh; it isn't needed and would bloat the stored secret.
            if (value is TokenResponse token) token.IdToken = null;
            credentials.Save(accountId, NewtonsoftJsonSerializer.Instance.Serialize(value));
            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(string key)
        {
            credentials.Delete(accountId);
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            var json = credentials.Get(accountId);
            return Task.FromResult(json is null ? default! : NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
        }

        public Task ClearAsync() => DeleteAsync<object>("");
    }
}
