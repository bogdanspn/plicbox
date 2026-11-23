using System.Diagnostics;
using System.IO;
using System.Windows;
using Plicbox.Auth;
using Plicbox.Core;
using Plicbox.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MimeKit;

namespace Plicbox.App.Services;

/// <summary>Window-level UI the view models need without referencing windows directly.</summary>
public sealed class Dialogs(IServiceProvider services)
{
    private static Window? Owner => Application.Current.MainWindow;

    /// <summary>The Add account window, again for each "Add another account"; true if any account was added.</summary>
    public bool ShowAddAccount()
    {
        var added = false;
        AccountSetupWindow dialog;
        do
        {
            dialog = services.GetRequiredService<AccountSetupWindow>();
            dialog.Owner = Owner;
            dialog.ShowDialog();
            added |= dialog.Added;
        } while (dialog.AddAnother);
        return added;
    }

    /// <param name="page">"general", "mail" or "calendar".</param>
    public bool ShowSettings(string? page = null)
    {
        var window = services.GetRequiredService<SettingsWindow>();
        window.Owner = Owner;
        window.StartPage = page;
        return window.ShowDialog() == true;
    }

    /// <summary>The contact window (new, edit, or save someone from your mail); an open one for the same contact comes forward.</summary>
    public void EditContact(ViewModels.ContactEditor editor)
    {
        var id = editor.Original is { IsSaved: true } c ? c.Id : (long?)null;
        if (id is not null && Application.Current.Windows.OfType<ContactWindow>().FirstOrDefault(w => w.ContactId == id) is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        new ContactWindow(editor).Show();
    }

    /// <summary>The new-task window (New → Task…, or Create Task on a message).</summary>
    public void EditTask(ViewModels.TaskEditor editor) => new TaskWindow(editor).Show();

    /// <summary>The Rules window; <paramref name="start"/> is a new rule to begin with (made from a message). True if saved.</summary>
    public bool ShowRules(MailRule? start = null)
    {
        var vm = services.GetRequiredService<ViewModels.RulesViewModel>();
        vm.StartWith = start;
        var window = new RulesWindow(vm) { Owner = Owner };
        return window.ShowDialog() == true;
    }

    /// <summary>The live Operations window; an open one comes forward.</summary>
    public void ShowOperations(IReadOnlyList<Account> accounts, Func<IReadOnlyList<PendingOp>, string> describeOps)
    {
        if (Application.Current.Windows.OfType<OperationsWindow>().FirstOrDefault() is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        new OperationsWindow(accounts, services.GetRequiredService<MailEngine>(), services.GetRequiredService<Dav.CalendarEngine>(),
            services.GetRequiredService<Dav.ContactsEngine>(), services.GetRequiredService<Dav.TasksEngine>(), describeOps) { Owner = Owner }.Show();
    }

    public void ShowAccounts()
    {
        var window = services.GetRequiredService<AccountsWindow>();
        window.Owner = Owner;
        window.ShowDialog();
    }

    /// <summary>Re-authenticates an account: Google and Microsoft open the browser, password accounts ask for the new password.</summary>
    /// <returns>True when a new sign-in was saved.</returns>
    public async Task<bool> SignInAgainAsync(Account account)
    {
        try
        {
            string? secret;
            string email;
            switch (account.Auth)
            {
                case AuthKind.GoogleOAuth:
                    var google = await services.GetRequiredService<GoogleAuth>().SignInAsync(account.Email, CancellationToken.None);
                    (secret, email) = (google.TokenJson, google.Email);
                    break;
                case AuthKind.MicrosoftOAuth:
                    var microsoft = await services.GetRequiredService<MicrosoftAuth>().SignInAsync(account.Email, CancellationToken.None);
                    (secret, email) = (microsoft.AccountKey, microsoft.Email);
                    break;
                default:
                    (secret, email) = (AskPassword(account.Email), account.Email);
                    break;
            }
            if (!string.Equals(email, account.Email, StringComparison.OrdinalIgnoreCase))
            {
                Error(L.F("You signed in as {0}, but this account is {1}.", email, account.Email));
                return false;
            }
            if (secret is null) return false;
            services.GetRequiredService<MailEngine>().UpdateSecret(account.Id, secret);
            return true;
        }
        catch (Exception ex)
        {
            Error(L.F("Signing in failed: {0}", ex.Message));
            return false;
        }
    }

    /// <summary>Opens a message in its own window.</summary>
    public void OpenMessage(ViewModels.MessageItemViewModel item, MimeMessage mime, Account account, bool allowRemoteContent)
    {
        var window = ActivatorUtilities.CreateInstance<MessageWindow>(services, item, mime, account, allowRemoteContent);
        window.Show();
    }

    /// <summary>Asks for a target folder; null when cancelled.</summary>
    public Folder? PickFolder(string heading, string action, IEnumerable<Folder> folders, Func<long, string>? accountName = null)
    {
        var window = new FolderPickerWindow(heading, action, folders, accountName) { Owner = Owner };
        return window.ShowDialog() == true ? window.Selected : null;
    }

    /// <summary>Read-only text viewer (mail source, headers) with a monospace font.</summary>
    public void ShowText(string title, string text)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0), Padding = new Thickness(12),
        };
        new Window
        {
            Title = title, Icon = WindowIcons.Source, Content = box, Owner = Owner, Width = 900, Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        }.Show();
    }

    /// <summary>Save As…: writes the original message as an .eml file.</summary>
    public void SaveMessage(Stream eml, string subject)
    {
        var dialog = new SaveFileDialog { FileName = SafeFileName(string.IsNullOrWhiteSpace(subject) ? L.T("message") : subject) + ".eml", Filter = L.T("Email message (*.eml)") + "|*.eml" };
        if (dialog.ShowDialog(Owner) != true) return;
        using (var file = File.Create(dialog.FileName)) eml.CopyTo(file);
        // Opened elsewhere later, its attachments come out marked too.
        if (AttachmentQuarantine.Mark(dialog.FileName) == QuarantineResult.Blocked) ReportBlocked(Path.GetFileName(dialog.FileName));
    }

    /// <summary>Opens the event editor (new event, or an existing one).</summary>
    public void EditEvent(CalendarEvent? existing, DateTime start, DateTime end, bool allDay, string? title, long? calendarId, bool videoCall = false,
        string? guests = null)
    {
        var window = ActivatorUtilities.CreateInstance<EventWindow>(services,
            new EventEditRequest(existing, start, end, allDay, title, calendarId, videoCall, guests));
        window.Show();
    }

    public void Compose(ComposeDraft draft, Account account)
    {
        // A saved draft that's already open is brought forward rather than opened twice.
        if (draft.MessageId is { } id
            && Application.Current.Windows.OfType<ComposeWindow>().FirstOrDefault(w => w.DraftMessageId == id) is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = ActivatorUtilities.CreateInstance<ComposeWindow>(services, draft, account);
        window.Show();
    }

    /// <summary>Asks for a new password; null when cancelled.</summary>
    public string? AskPassword(string email)
    {
        var box = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 8, 0, 0) };
        var ok = new System.Windows.Controls.Button { Content = L.T("Save"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
        var cancel = new System.Windows.Controls.Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90, Padding = new Thickness(12, 5, 12, 5) };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = L.F("New password for {0}", email), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = L.T("Sign in again"), Icon = WindowIcons.SignIn, Content = panel, Owner = Owner, Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"], FontSize = 14,
        };
        ok.Click += (_, _) => window.DialogResult = box.Password.Length > 0;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }

    /// <summary>Asks for a short piece of text (a folder name, …); null when cancelled or left empty.</summary>
    public string? AskText(string title, string prompt, string action, string initial = "")
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(4, 3, 4, 3) };
        var ok = new System.Windows.Controls.Button { Content = action, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
        var cancel = new System.Windows.Controls.Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90, Padding = new Thickness(12, 5, 12, 5) };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = title, Icon = WindowIcons.Source, Content = panel, Owner = Owner, Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"], FontSize = 14,
        };
        ok.Click += (_, _) => window.DialogResult = box.Text.Trim().Length > 0;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    /// <summary>Asks before doing something; <paramref name="action"/> names the button ("Delete", "Remove", ...).</summary>
    public bool Confirm(string title, string message, string action, DialogKind kind = DialogKind.Question) =>
        MessageDialog.Ask(null, title, message, action, L.T("Cancel"), kind);

    public void Error(string message) =>
        MessageDialog.Show(null, L.T("Something didn't work"), message, DialogKind.Warning);

    public void Info(string title, string message) => MessageDialog.Show(null, title, message);

    public void OpenAttachment(MimePart part)
    {
        if (part.Content is null) throw new InvalidOperationException(L.T("The attachment is empty."));
        var name = SafeFileName(part.FileName ?? L.T("attachment"));
        var safety = AttachmentSafety.Assess(part);
        if (safety.Risk == AttachmentRisk.Danger
            && !Confirm(L.F("Open {0}?", safety.Stem + safety.Extension), safety.Warning ?? "", L.T("Open anyway"), DialogKind.Warning))
            return;

        var dir = Path.Combine(Path.GetTempPath(), "Plicbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using (var file = File.Create(path)) part.Content.DecodeTo(file);
        // Marked as from the internet (and scanned) before anything opens it.
        if (AttachmentQuarantine.Mark(path, part.FileName) == QuarantineResult.Blocked)
        {
            ReportBlocked(name);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void SaveAttachment(MimePart part)
    {
        if (part.Content is null) throw new InvalidOperationException(L.T("The attachment is empty."));
        var dialog = new SaveFileDialog { FileName = SafeFileName(part.FileName ?? L.T("attachment")) };
        if (dialog.ShowDialog(Owner) != true) return;
        using (var file = File.Create(dialog.FileName)) part.Content.DecodeTo(file);
        if (AttachmentQuarantine.Mark(dialog.FileName, part.FileName) == QuarantineResult.Blocked) ReportBlocked(Path.GetFileName(dialog.FileName));
    }

    /// <summary>The antivirus (or a Windows policy) removed a file as it was saved.</summary>
    public void ReportBlocked(string name) =>
        MessageDialog.Show(null, L.F("{0} was blocked", name),
            L.T("Your antivirus or a Windows security policy removed it as it was being saved, because it may be harmful."), DialogKind.Danger);

    /// <summary>Save Attachments…: every attachment into one chosen folder (names made unique). Returns the folder, or null if cancelled.</summary>
    public string? SaveAttachments(IReadOnlyList<MimePart> parts)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Save attachments to") };
        if (dialog.ShowDialog(Owner) != true) return null;
        var blocked = new List<string>();
        foreach (var part in parts.Where(p => p.Content is not null))
        {
            var name = SafeFileName(part.FileName ?? L.T("attachment"));
            var path = Path.Combine(dialog.FolderName, name);
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(dialog.FolderName, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            using (var file = File.Create(path)) part.Content!.DecodeTo(file);
            if (AttachmentQuarantine.Mark(path, part.FileName) == QuarantineResult.Blocked) blocked.Add(Path.GetFileName(path));
        }
        if (blocked.Count > 0) ReportBlocked(string.Join(", ", blocked));
        return dialog.FolderName;
    }

    internal static string SafeFileName(string name)
    {
        name = Path.GetFileName(name);
        // Hidden direction marks would make Explorer show "invoice‮fdp.exe" as "invoiceexe.pdf".
        name = System.Text.RegularExpressions.Regex.Replace(name, "[\u200E\u200F\u202A-\u202E\u2066-\u2069]", "");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? L.T("attachment") : name;
    }
}
