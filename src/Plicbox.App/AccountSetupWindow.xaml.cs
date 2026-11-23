using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Plicbox.Auth;
using Plicbox.Core;
using Plicbox.Mail;
using Microsoft.Win32;
using MimeKit;

namespace Plicbox.App;

public partial class AccountSetupWindow : Window
{
    private enum Step { Identity, Google, Microsoft, Password, Done }

    private static readonly string[] Colors = ["#F7931E", "#3B82F6", "#10B981", "#8B5CF6", "#EF4444", "#0EA5E9"];

    private readonly AccountDiscovery _discovery;
    private readonly MailEngine _engine;
    private readonly IAccountRepository _accounts;
    private readonly GoogleAuth _google;
    private readonly MicrosoftAuth _microsoft;
    private readonly GmailAliases _gmailAliases;
    private CancellationTokenSource? _cts;
    private string? _helpUrl; // the provider's page for what it wants done first
    private Step _step = Step.Identity;
    private DiscoveredSettings? _found;
    private ProviderKind _provider = ProviderKind.Imap;
    private string? _googleHint; // address typed before choosing Google, if any
    private string? _microsoftHint; // the same for Microsoft
    private bool _preferPassword; // user chose "app password instead"
    private bool _filling; // settings being filled in from what was found: leave the ports alone
    private Func<CancellationToken, Task>? _fix; // what the fix button does
    private bool _sendingBlocked; // the last check: Outlook.com reads fine but won't take mail from apps (5.7.139)

    public AccountSetupWindow(AccountDiscovery discovery, MailEngine engine, IAccountRepository accounts, GoogleAuth google,
        MicrosoftAuth microsoft, GmailAliases gmailAliases)
    {
        InitializeComponent();
        Icon = Services.WindowIcons.AddAccount;
        (_discovery, _engine, _accounts, _google, _microsoft, _gmailAliases) = (discovery, engine, accounts, google, microsoft, gmailAliases);
        Loaded += (_, _) => GoogleQuickButton.Focus();
        MaxHeight = SystemParameters.WorkArea.Height - 40; // taller content scrolls instead
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>An account was added (or signed in again), however the window was closed.</summary>
    public bool Added { get; private set; }

    /// <summary>The user wants to add one more straight away.</summary>
    public bool AddAnother { get; private set; }

    private string Email => EmailBox.Text.Trim();

    /// <summary>Dev screenshots only: the Google step while waiting for the browser.</summary>
    internal void PreviewWaiting()
    {
        _googleHint = "you@company.example";
        ShowGoogleStep();
        SetBusy(true, "Waiting for you to sign in in the browser…");
    }

    /// <summary>Dev screenshots only: the server settings for a custom domain, with the certificate fix on offer.</summary>
    internal void PreviewServers()
    {
        NameBox.Text = "You";
        EmailBox.Text = "you@yourdomain.example";
        ShowPasswordStep(null);
        ImapHostBox.Text = SmtpHostBox.Text = "mail.yourdomain.example";
        SmtpAuthBox.SelectedIndex = (int)SmtpAuthMode.Separate;
        ShowError("Incoming mail: The security certificate of mail.yourdomain.example is issued to server123.host.example. Use server123.host.example as the server name instead.");
        FixButton.Content = "Use server123.host.example";
        FixButton.Visibility = Visibility.Visible;
    }

    /// <summary>Dev screenshots only: the Microsoft step while waiting for the browser.</summary>
    internal void PreviewMicrosoftWaiting()
    {
        _microsoftHint = "you@outlook.com";
        ShowMicrosoftStep();
        SetBusy(true, L.T("Waiting for you to sign in in the browser…"));
    }

    /// <summary>Dev screenshots only: a provider that wants an app password, with the link to make one.</summary>
    internal void PreviewAppPassword()
    {
        NameBox.Text = "You";
        EmailBox.Text = "you@yahoo.com";
        ShowPasswordStep(new DiscoveredSettings(ProviderKind.Yahoo, "imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, "you@yahoo.com", "built-in"));
    }

    /// <summary>Dev screenshots only: a Google account just added, with its Send mail as addresses.</summary>
    internal void PreviewDone() =>
        ShowDoneStep(L.F("{0} is added. Plicbox is downloading its mail now.", "you@gmail.com") + " "
            + L.F("Its Gmail \"Send mail as\" addresses came along too: {0}.", "you@yourdomain.example, team@yourdomain.example"));

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Done)
        {
            Close();
            return;
        }
        await RunAsync(_step switch
        {
            Step.Identity => DiscoverAsync,
            Step.Google => SignInWithGoogleAsync,
            Step.Microsoft => SignInWithMicrosoftAsync,
            _ => SignInWithPasswordAsync,
        });
    }

    private void Another_Click(object sender, RoutedEventArgs e)
    {
        AddAnother = true;
        Close();
    }

    private async void GoogleQuick_Click(object sender, RoutedEventArgs e)
    {
        _googleHint = null;
        ShowGoogleStep();
        if (_google.IsConfigured) await RunAsync(SignInWithGoogleAsync); // straight to the browser
    }

    private async void MicrosoftQuick_Click(object sender, RoutedEventArgs e)
    {
        _microsoftHint = null;
        ShowMicrosoftStep();
        await RunAsync(SignInWithMicrosoftAsync); // straight to the browser
    }

    private async Task RunAsync(Func<CancellationToken, Task> step)
    {
        HideError();
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await step(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ───────────────────────── step 1 ─────────────────────────

    private async Task DiscoverAsync(CancellationToken ct)
    {
        if (NameBox.Text.Trim().Length == 0) { ShowError(L.T("Enter your name.")); NameBox.Focus(); return; }
        if (!MailboxAddress.TryParse(Email, out var parsed) || !parsed.Address.Contains('@'))
        {
            ShowError(L.T("Enter a valid email address."));
            EmailBox.Focus();
            return;
        }
        EmailBox.Text = parsed.Address;
        if (await IsAlreadyAddedAsync(Email, ct)) return;

        SetBusy(true, L.T("Finding your mail server…"));
        _found = await _discovery.DiscoverAsync(Email, ct);

        if (_found?.Provider == ProviderKind.Gmail && !_preferPassword)
        {
            _googleHint = Email;
            ShowGoogleStep();
        }
        else if (_found?.Provider == ProviderKind.Microsoft)
        {
            // Microsoft refuses passwords from mail apps, so there's no password route to offer.
            _microsoftHint = Email;
            ShowMicrosoftStep();
        }
        else
        {
            ShowPasswordStep(_found);
        }
    }

    // ───────────────────────── step 2a: Google ─────────────────────────

    private void ShowGoogleStep()
    {
        _step = Step.Google;
        _provider = ProviderKind.Gmail;
        StepIdentity.Visibility = StepMicrosoft.Visibility = StepPassword.Visibility = Visibility.Collapsed;
        StepGoogle.Visibility = Visibility.Visible;
        var how = L.T("Plicbox opens your browser so you sign in with Google directly. Your password never passes through Plicbox.");
        GoogleIntro.Text = _googleHint is null ? how : L.F("{0} uses Google.", _googleHint[(_googleHint.LastIndexOf('@') + 1)..]) + " " + how;
        Subtitle.Text = L.T("Sign in with Google.");
        UpdateGoogleState();
    }

    private void UpdateGoogleState()
    {
        GoogleSetup.Visibility = _google.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = L.T("Sign in with Google");
        PrimaryButton.IsEnabled = _google.IsConfigured;
    }

    private async Task SignInWithGoogleAsync(CancellationToken ct)
    {
        SetBusy(true, L.T("Waiting for you to sign in in the browser…"));
        var result = await _google.SignInAsync(_googleHint, ct);
        if (_googleHint is not null && !string.Equals(result.Email, _googleHint, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(L.F("You signed in to Google as {0}, not {1}. Try again and pick the right account.", result.Email, _googleHint));
            return;
        }
        if (await FindAsync(result.Email, ct) is { } added)
        {
            if (added.Auth != AuthKind.GoogleOAuth) { ShowError(L.F("{0} is already added.", result.Email)); return; }
            // Already here (its sign-in expired, or "Sign in again" was finished in another tab): renew it rather than add it twice.
            _engine.UpdateSecret(added.Id, result.TokenJson);
            ShowDoneStep(L.F("{0} is already in Plicbox, so its sign-in was renewed.", result.Email));
            return;
        }

        // Prove the token works for both directions before saving anything.
        var token = Google.Apis.Json.NewtonsoftJsonSerializer.Instance
            .Deserialize<Google.Apis.Auth.OAuth2.Responses.TokenResponse>(result.TokenJson).AccessToken;
        var gmail = new DiscoveredSettings(ProviderKind.Gmail, "imap.gmail.com", 993, "smtp.gmail.com", 465, result.Email, "Google");
        var login = new MailLogin(result.Email, AccessToken: token);
        if (!await TestAsync(gmail, login, login, ct)) return;

        var name = NameBox.Text.Trim() is { Length: > 0 } typed ? typed : result.Name ?? result.Email;
        var aliases = await SendAsAddressesAsync(token, ct);
        await SaveAsync(gmail, result.Email, name, result.Email, AuthKind.GoogleOAuth, result.TokenJson, ct, aliases: aliases);
    }

    /// <summary>Gmail's "Send mail as" addresses, so they're there to pick as From straight away; none if Gmail won't say.</summary>
    private async Task<IReadOnlyList<MailAlias>> SendAsAddressesAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            return await _gmailAliases.GetAsync(accessToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Serilog.Log.Warning(ex, "Couldn't read the Send mail as addresses of a new Google account");
            return [];
        }
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://console.cloud.google.com/projectcreate") { UseShellExecute = true });

    private void ChooseClientFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = L.T("Google client file (*.json)") + "|*.json", Title = L.T("Choose the downloaded client JSON") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _google.ImportClientFile(dialog.FileName);
            HideError();
            UpdateGoogleState();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void UseAppPassword_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        _preferPassword = true;
        if (_googleHint is null)
        {
            // Came via the Google button without an address: go back and ask for it.
            _step = Step.Identity;
            StepGoogle.Visibility = Visibility.Collapsed;
            StepIdentity.Visibility = Visibility.Visible;
            PrimaryButton.Content = L.T("Continue");
            PrimaryButton.IsEnabled = true;
            Subtitle.Text = L.T("Enter your name and Google address, then use an app password.");
            NameBox.Focus();
            return;
        }
        ShowPasswordStep(_found);
    }

    // ───────────────────────── step 2c: Microsoft ─────────────────────────

    private void ShowMicrosoftStep()
    {
        _step = Step.Microsoft;
        _provider = ProviderKind.Microsoft;
        StepIdentity.Visibility = StepGoogle.Visibility = StepPassword.Visibility = Visibility.Collapsed;
        StepMicrosoft.Visibility = Visibility.Visible;
        var how = L.T("Plicbox opens your browser so you sign in with Microsoft directly. Your password never passes through Plicbox.");
        MicrosoftIntro.Text = _microsoftHint is null ? how : L.F("{0} uses Microsoft.", _microsoftHint[(_microsoftHint.LastIndexOf('@') + 1)..]) + " " + how;
        Subtitle.Text = L.T("Sign in with Microsoft.");
        PrimaryButton.Content = L.T("Sign in with Microsoft");
        PrimaryButton.IsEnabled = true;
    }

    private async Task SignInWithMicrosoftAsync(CancellationToken ct)
    {
        SetBusy(true, L.T("Waiting for you to sign in in the browser…"));
        var result = await _microsoft.SignInAsync(_microsoftHint, ct);
        if (_microsoftHint is not null && !string.Equals(result.Email, _microsoftHint, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(L.F("You signed in to Microsoft as {0}, not {1}. Try again and pick the right account.", result.Email, _microsoftHint));
            return;
        }
        if (await FindAsync(result.Email, ct) is { } added)
        {
            if (added.Auth != AuthKind.MicrosoftOAuth) { ShowError(L.F("{0} is already added.", result.Email)); return; }
            _engine.UpdateSecret(added.Id, result.AccountKey);
            ShowDoneStep(L.F("{0} is already in Plicbox, so its sign-in was renewed.", result.Email));
            return;
        }

        // Prove the token works for both directions before saving anything.
        var outlook = (result.IsPersonal ? AccountDiscovery.MicrosoftPersonal : AccountDiscovery.Microsoft) with { Username = result.Email };
        var login = new MailLogin(result.Email, AccessToken: result.AccessToken);
        string Name() => NameBox.Text.Trim() is { Length: > 0 } typed ? typed : result.Name ?? result.Email;
        if (!await TestAsync(outlook, login, login, ct))
        {
            // Only sending is refused (Outlook.com's 5.7.139): it can still be added to read mail, without signing in again.
            if (_sendingBlocked)
            {
                FixButton.Content = L.T("Add without sending");
                _fix = c => SaveAsync(outlook, result.Email, Name(), result.Email, AuthKind.MicrosoftOAuth, result.AccountKey, c);
                FixButton.Visibility = Visibility.Visible;
            }
            return;
        }
        await SaveAsync(outlook, result.Email, Name(), result.Email, AuthKind.MicrosoftOAuth, result.AccountKey, ct);
    }

    // ───────────────────────── step 2b: password ─────────────────────────

    private void ShowPasswordStep(DiscoveredSettings? found)
    {
        _step = Step.Password;
        StepIdentity.Visibility = Visibility.Visible;
        StepIdentity.IsEnabled = false;
        GoogleQuickButton.Visibility = GoogleHint.Visibility = ProviderDivider.Visibility = Visibility.Collapsed;
        MicrosoftQuickButton.Visibility = MicrosoftHint.Visibility = Visibility.Collapsed;
        StepGoogle.Visibility = StepMicrosoft.Visibility = Visibility.Collapsed;
        StepPassword.Visibility = Visibility.Visible;
        PrimaryButton.Content = L.T("Sign in");
        PrimaryButton.IsEnabled = true;

        if (found is not null)
        {
            _filling = true;
            ImapSecurityBox.SelectedIndex = (int)found.ImapSecurity;
            SmtpSecurityBox.SelectedIndex = (int)found.SmtpSecurity;
            _filling = false;
            ImapHostBox.Text = found.ImapHost;
            ImapPortBox.Text = found.ImapPort.ToString();
            SmtpHostBox.Text = found.SmtpHost;
            SmtpPortBox.Text = found.SmtpPort.ToString();
            UsernameBox.Text = found.Username;
            _provider = found.Provider;
            Subtitle.Text = L.F("Settings found ({0}). Enter your password.", found.Source);
            if (found.Warning is { } warning) ShowError(warning);
        }
        else
        {
            UsernameBox.Text = Email;
            ServerExpander.IsExpanded = true;
            Subtitle.Text = L.T("Couldn't detect the server settings. Enter them from your provider.");
        }
        UpdateProviderHelp();
        PasswordBox.Focus();
    }

    private void ImapHost_Changed(object sender, TextChangedEventArgs e) => UpdateProviderHelp();

    /// <summary>What the provider of the incoming server, as typed, wants done first (an app password, IMAP switched on).</summary>
    private void UpdateProviderHelp()
    {
        if (ProviderHelpBox is null) return; // still being built
        var help = AccountDiscovery.HelpFor(ImapHostBox.Text);
        ProviderHelpBox.Visibility = help is null ? Visibility.Collapsed : Visibility.Visible;
        if (help is null) return;
        ProviderHelpText.Text = help.AppPassword
            ? L.F("{0} only lets mail apps in with an app password, not your normal password.", help.Name) + " " + help.Steps
            : help.Steps;
        _helpUrl = help.Url;
        ProviderHelpLinkRow.Visibility = help.Url is null ? Visibility.Collapsed : Visibility.Visible;
        ProviderHelpLink.Text = L.F("Create an app password at {0}", help.Name);
    }

    private void ProviderHelpLink_Click(object sender, RoutedEventArgs e)
    {
        if (_helpUrl is not null) Process.Start(new ProcessStartInfo(_helpUrl) { UseShellExecute = true });
    }

    private async Task SignInWithPasswordAsync(CancellationToken ct)
    {
        if (PasswordBox.Password.Length == 0) { ShowError(L.T("Enter your password.")); PasswordBox.Focus(); return; }
        if (!int.TryParse(ImapPortBox.Text, out var imapPort) || !int.TryParse(SmtpPortBox.Text, out var smtpPort)
            || ImapHostBox.Text.Trim().Length == 0 || SmtpHostBox.Text.Trim().Length == 0)
        {
            ServerExpander.IsExpanded = true;
            ShowError(L.T("Fill in both servers and ports."));
            return;
        }

        var smtpAuth = (SmtpAuthMode)SmtpAuthBox.SelectedIndex;
        if (smtpAuth == SmtpAuthMode.Separate && (SmtpUserBox.Text.Trim().Length == 0 || SmtpPasswordBox.Password.Length == 0))
        {
            ServerExpander.IsExpanded = true;
            ShowError(L.T("Enter the outgoing user name and password."));
            return;
        }

        var settings = new DiscoveredSettings(AccountDiscovery.ProviderFor(ImapHostBox.Text.Trim()),
            ImapHostBox.Text.Trim(), imapPort, SmtpHostBox.Text.Trim(), smtpPort, UsernameBox.Text.Trim(), "manual",
            (TlsMode)ImapSecurityBox.SelectedIndex, (TlsMode)SmtpSecurityBox.SelectedIndex);
        var login = new MailLogin(settings.Username, PasswordBox.Password);
        var smtpLogin = smtpAuth switch
        {
            SmtpAuthMode.None => null,
            SmtpAuthMode.Separate => new MailLogin(SmtpUserBox.Text.Trim(), SmtpPasswordBox.Password),
            _ => login,
        };
        if (!await TestAsync(settings, login, smtpLogin, ct)) return;
        await SaveAsync(settings, Email, NameBox.Text.Trim(), settings.Username, AuthKind.Password, PasswordBox.Password, ct,
            smtpAuth, smtpAuth == SmtpAuthMode.Separate ? SmtpUserBox.Text.Trim() : null,
            smtpAuth == SmtpAuthMode.Separate ? SmtpPasswordBox.Password : null);
    }

    /// <summary>Switching encryption moves a standard port to its partner (993 ↔ 143, 465 ↔ 587); a custom port stays.</summary>
    private void Security_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _filling) return;
        var (box, tls, startTls) = sender == ImapSecurityBox ? (ImapPortBox, "993", "143") : (SmtpPortBox, "465", "587");
        switch (((ComboBox)sender).SelectedIndex)
        {
            case (int)TlsMode.Tls when box.Text.Trim() == startTls: box.Text = tls; break;
            case (int)TlsMode.StartTls when box.Text.Trim() == tls: box.Text = startTls; break;
        }
    }

    private void SmtpAuth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SmtpLoginPanel is null) return; // still being built
        var separate = SmtpAuthBox.SelectedIndex == (int)SmtpAuthMode.Separate;
        SmtpLoginPanel.Visibility = separate ? Visibility.Visible : Visibility.Collapsed;
        if (separate && SmtpUserBox.Text.Length == 0) SmtpUserBox.Text = UsernameBox.Text;
    }

    /// <summary>
    /// The usual shared-hosting snag: the certificate is fine but issued to the host's own server name. Offers to use it
    /// (wherever the old name was used: one server usually does both directions).
    /// </summary>
    private void OfferCertificateFix(Exception ex, string host)
    {
        if (_step != Step.Password || ex is not MailKit.Security.SslHandshakeException ssl
            || AccountDiscovery.CertificateHost(ssl, host) is not { } real) return;
        FixButton.Content = L.F("Use {0}", real);
        _fix = ct =>
        {
            if (string.Equals(ImapHostBox.Text.Trim(), host, StringComparison.OrdinalIgnoreCase)) ImapHostBox.Text = real;
            if (string.Equals(SmtpHostBox.Text.Trim(), host, StringComparison.OrdinalIgnoreCase)) SmtpHostBox.Text = real;
            return SignInWithPasswordAsync(ct);
        };
        FixButton.Visibility = Visibility.Visible;
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (_fix is { } fix) await RunAsync(fix);
    }

    // ───────────────────────── shared ─────────────────────────

    private async Task<Account?> FindAsync(string email, CancellationToken ct) =>
        (await _accounts.GetAllAsync(ct)).FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> IsAlreadyAddedAsync(string email, CancellationToken ct)
    {
        if (await FindAsync(email, ct) is null) return false;
        ShowError(L.F("{0} is already added.", email));
        return true;
    }

    /// <param name="smtpLogin">The outgoing sign-in (null: the server takes mail without one).</param>
    private async Task<bool> TestAsync(DiscoveredSettings s, MailLogin login, MailLogin? smtpLogin, CancellationToken ct)
    {
        var auth = login.AccessToken is null ? AuthKind.Password : _step == Step.Microsoft ? AuthKind.MicrosoftOAuth : AuthKind.GoogleOAuth;
        _sendingBlocked = false;
        SetBusy(true, L.T("Signing in…"));
        // What the server said, for the log when the test fails (passwords and tokens blanked out by MailKit).
        using var imapTalk = new MemoryStream();
        try
        {
            using var protocol = new MailKit.ProtocolLogger(imapTalk, leaveOpen: true) { RedactSecrets = true };
            using var client = await MailConnector.ConnectImapAsync(new ServerEndpoint(s.ImapHost, s.ImapPort, s.ImapSecurity), login, ct, protocol);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailedTest("incoming", s.ImapHost, login.Username, auth, ex, imapTalk);
            // Outlook.com's answer when the token is fine but the mailbox can't be opened by mail apps (inactive
            // mailbox, or IMAP not allowed for it); MailKit's own message only says the sign-in failed.
            var notConnected = System.Text.Encoding.UTF8.GetString(imapTalk.ToArray()).Contains("authenticated but not connected", StringComparison.OrdinalIgnoreCase);
            ShowError(notConnected
                ? L.F("Microsoft signed you in, but Outlook isn't letting mail apps into the mailbox of {0}. Outlook.com keeps this off until you allow it: at outlook.com, open Settings → Mail → Forwarding and IMAP, turn on “Let devices and apps use IMAP” and select Save (a new account is asked to verify itself there first). If it's on already, confirm the sign-in from Plicbox with “This was me” at account.live.com/activity. Then try again.", login.Username)
                : L.F("Incoming mail: {0}", AccountDiscovery.Explain(ex, s.ImapHost, auth)));
            OfferCertificateFix(ex, s.ImapHost);
            if (_step == Step.Password) ServerExpander.IsExpanded = true;
            return false;
        }

        SetBusy(true, L.T("Checking outgoing mail…"));
        using var smtpTalk = new MemoryStream();
        try
        {
            using var protocol = new MailKit.ProtocolLogger(smtpTalk, leaveOpen: true) { RedactSecrets = true };
            using var client = await MailConnector.ConnectSmtpAsync(new ServerEndpoint(s.SmtpHost, s.SmtpPort, s.SmtpSecurity), smtpLogin, ct, protocol);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailedTest("outgoing", s.SmtpHost, smtpLogin?.Username, auth, ex, smtpTalk);
            // Outlook.com (personal) answering 5.7.139: the token is fine and reading works, but Microsoft has SMTP sending off
            // for this mailbox. Neither the user nor an IT admin can change that, so the Microsoft 365 hint below would mislead.
            if (_step == Step.Microsoft && s.SmtpHost.Equals("smtp-mail.outlook.com", StringComparison.OrdinalIgnoreCase)
                && ex.Message.Contains("SmtpClientAuthentication is disabled", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(L.F("Reading mail works, but Outlook won't let mail apps send from {0}. Microsoft has switched this off for the mailbox, and Outlook.com has no setting to turn it on. You can still add it to read mail; sending from it won't work.", smtpLogin?.Username ?? login.Username));
                _sendingBlocked = true;
                return false;
            }
            var hint = ex is not MailKit.Security.AuthenticationException ? ""
                : _step == Step.Password && SmtpAuthBox.SelectedIndex == (int)SmtpAuthMode.SameAsIncoming
                    ? " " + L.T("If your provider gives you a separate login for sending, choose it under Outgoing sign-in.")
                // Microsoft 365 switches sending from mail apps off for new organizations.
                : _step == Step.Microsoft
                    ? " " + L.T("Microsoft 365 organizations can turn off sending from mail apps (SMTP AUTH). Your IT admin can turn it on for your mailbox.")
                    : "";
            ShowError(L.F("Outgoing mail: {0}", AccountDiscovery.Explain(ex, s.SmtpHost, auth) + hint));
            OfferCertificateFix(ex, s.SmtpHost);
            if (_step == Step.Password) ServerExpander.IsExpanded = true;
            return false;
        }
        return true;
    }

    /// <summary>
    /// A failed setup test in the log: the error and the exchange with the server (its greeting, what it offers, and
    /// its answer to the sign-in), so a rejection can be told apart. Secrets are blanked out by MailKit.
    /// </summary>
    private static void LogFailedTest(string direction, string host, string? username, AuthKind auth, Exception ex, MemoryStream talk)
    {
        var exchange = System.Text.Encoding.UTF8.GetString(talk.ToArray());
        if (exchange.Length > 8000) exchange = exchange[..8000] + " …";
        Serilog.Log.Warning(ex, "Account setup: the {Direction} test failed on {Host} as {User} ({Auth}). The exchange:\n{Exchange}",
            direction, host, username, auth, exchange);
    }

    /// <param name="aliases">Extra From addresses found for the account (Gmail's "Send mail as").</param>
    private async Task SaveAsync(DiscoveredSettings s, string email, string name, string username, AuthKind auth, string secret, CancellationToken ct,
        SmtpAuthMode smtpAuth = SmtpAuthMode.SameAsIncoming, string? smtpUsername = null, string? smtpSecret = null,
        IReadOnlyList<MailAlias>? aliases = null)
    {
        var count = (await _accounts.GetAllAsync(ct)).Count;
        await _engine.AddAccountAsync(new Account
        {
            DisplayName = name,
            Email = email,
            Provider = s.Provider == ProviderKind.Imap ? _provider : s.Provider,
            Auth = auth,
            Username = username,
            ImapHost = s.ImapHost, ImapPort = s.ImapPort,
            SmtpHost = s.SmtpHost, SmtpPort = s.SmtpPort,
            ImapSecurity = s.ImapSecurity, SmtpSecurity = s.SmtpSecurity,
            SmtpAuth = smtpAuth, SmtpUsername = smtpUsername,
            AliasesJson = aliases is { Count: > 0 } ? AccountJson.Aliases(aliases) : null,
            Color = Colors[count % Colors.Length],
        }, secret, ct, smtpSecret);

        var done = L.F("{0} is added. Plicbox is downloading its mail now.", email);
        if (aliases is { Count: > 0 })
            done += " " + L.F("Its Gmail \"Send mail as\" addresses came along too: {0}.", string.Join(", ", aliases.Select(a => a.Address)));
        ShowDoneStep(done);
    }

    // ───────────────────────── step 3: done ─────────────────────────

    /// <summary>The account is in: say so, and offer to add another rather than just closing.</summary>
    private void ShowDoneStep(string text)
    {
        _step = Step.Done;
        Added = true;
        HideError();
        StepIdentity.Visibility = StepGoogle.Visibility = StepMicrosoft.Visibility = StepPassword.Visibility = Visibility.Collapsed;
        StepDone.Visibility = Visibility.Visible;
        DoneText.Text = text;
        Subtitle.Text = L.T("All set.");
        CancelButton.Visibility = Visibility.Collapsed;
        AnotherButton.Visibility = Visibility.Visible;
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Focus();
    }

    private void SetBusy(bool busy, string text = "")
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
        PrimaryButton.IsEnabled = !busy && (_step != Step.Google || _google.IsConfigured);
        GoogleQuickButton.IsEnabled = MicrosoftQuickButton.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        FixButton.Visibility = Visibility.Collapsed;
    }
}
