using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Plicbox.Core;
using Plicbox.Mail;
using Plicbox.Storage;

namespace Plicbox.Tests;

/// <summary>
/// A message goes out once, also when the connection breaks in the middle: against a small SMTP server that breaks off
/// at chosen moments.
/// </summary>
public sealed class SendOnceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-sendonce", Guid.NewGuid().ToString("N"));
    private Database _db = null!;
    private MessageRepository _messages = null!;
    private FolderRepository _folders = null!;
    private PendingOpRepository _ops = null!;
    private MailEngine _engine = null!;
    private Account _account = null!;
    private Folder _sent = null!, _drafts = null!;
    private FakeSmtp _smtp = null!;
    private readonly List<long> _asked = [];

    public async Task InitializeAsync()
    {
        MailConnector.CertificateValidationOverride = (_, _, _, _) => true;
        _smtp = new FakeSmtp();
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "t.db"));
        _db.Migrate();
        (_messages, _folders, _ops) = (new(_db), new(_db), new(_db));
        var accounts = new AccountRepository(_db);
        var creds = new NoCredentials();
        _engine = NewEngine(accounts, creds);
        _account = new Account
        {
            DisplayName = "Ana", Email = "ana@example.com", Username = "ana",
            ImapHost = "127.0.0.1", ImapPort = 1, SmtpHost = "127.0.0.1", SmtpPort = _smtp.Port, SmtpSecurity = TlsMode.Tls,
        };
        _account = _account with { Id = await accounts.AddAsync(_account) };
        await _folders.UpsertAsync(new Folder { AccountId = _account.Id, Path = "Drafts", Name = "Drafts", Role = FolderRole.Drafts });
        await _folders.UpsertAsync(new Folder { AccountId = _account.Id, Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
        var all = await _folders.GetByAccountAsync(_account.Id);
        (_drafts, _sent) = (all.Single(f => f.Role == FolderRole.Drafts), all.Single(f => f.Role == FolderRole.Sent));
    }

    private MailEngine NewEngine(AccountRepository accounts, ICredentialStore creds)
    {
        var engine = new MailEngine(accounts, _folders, _messages, _ops, new FileBodyStore(Path.Combine(_dir, "bodies")), creds,
            new MailConnector(creds), NullLoggerFactory.Instance)
        {
            LookAgain = TimeSpan.FromMilliseconds(100), LookFor = TimeSpan.FromMilliseconds(600), SentLookup = _ => false,
        };
        engine.SendUnsure += (_, id, _) => { lock (_asked) _asked.Add(id); };
        return engine;
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _smtp.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Task<long> SendAsync(string subject = "Hello", int bodySize = 20) => _engine.SendAsync(_account,
        Composing.ToMimeMessage(_account, new ComposeDraft { AccountId = _account.Id }, "bob@example.com", "", "", subject,
            $"<p>{new string('x', bodySize)}</p>", []), DateTimeOffset.Now);

    private static async Task UntilAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await done()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Didn't happen in time.");
    }

    private async Task<int> InOutboxAsync() => await _engine.CountOutboxAsync();
    private int Asked { get { lock (_asked) return _asked.Count; } }

    [Fact]
    public async Task An_ordinary_message_is_sent_and_filed()
    {
        var id = await SendAsync();
        await UntilAsync(async () => (await _messages.GetAsync(id))?.FolderId == _sent.Id);
        Assert.Equal(1, _smtp.Received);
        Assert.Equal(SendState.Waiting, (await _messages.GetAsync(id))!.SendState);
        Assert.Contains(await _ops.GetByAccountAsync(_account.Id), o => o.Kind == PendingOpKind.Append && o.MessageId == id);
    }

    [Fact]
    public async Task No_answer_after_the_message_is_asked_about_not_sent_again()
    {
        _smtp.Next = Break.AfterMessage;
        var id = await SendAsync();
        await UntilAsync(() => Task.FromResult(Asked == 1));
        Assert.Equal(1, _smtp.Received);
        Assert.Equal(SendState.Started, (await _messages.GetAsync(id))!.SendState);

        // Sending something else tries the Outbox again: not this one, and it's not asked about twice.
        var other = await SendAsync("Other");
        await UntilAsync(async () => (await _messages.GetAsync(other))?.FolderId == _sent.Id);
        await Task.Delay(500);
        Assert.Equal(2, _smtp.Received);
        Assert.Equal(1, Asked);
        Assert.Equal(1, await InOutboxAsync());

        // "Send again".
        await _engine.SendNowAsync((await _messages.GetAsync(id))!);
        await UntilAsync(async () => (await _messages.GetAsync(id))?.FolderId == _sent.Id);
        Assert.Equal(3, _smtp.Received);
    }

    [Fact]
    public async Task It_was_sent_files_it_without_sending()
    {
        _smtp.Next = Break.AfterMessage;
        var id = await SendAsync();
        await UntilAsync(() => Task.FromResult(Asked == 1));
        await _engine.MarkSentAsync(id);
        await UntilAsync(async () => (await _messages.GetAsync(id))?.FolderId == _sent.Id);
        Assert.Equal(1, _smtp.Received);
    }

    [Fact]
    public async Task One_found_in_Sent_is_done()
    {
        _engine.SentLookup = _ => true;
        _smtp.Next = Break.AfterMessage;
        var id = await SendAsync();
        await UntilAsync(async () => await _messages.GetAsync(id) is null); // the server's own copy comes in with Sent
        Assert.Equal(1, _smtp.Received);
        Assert.Equal(0, Asked);
    }

    [Fact]
    public async Task It_waits_while_the_server_cant_be_asked()
    {
        _engine.SentLookup = _ => null;
        _smtp.Next = Break.AfterMessage;
        await SendAsync();
        await Task.Delay(1500);
        Assert.Equal((1, 0, 1), (_smtp.Received, Asked, await InOutboxAsync()));
    }

    [Fact]
    public async Task A_crash_while_sending_doesnt_send_it_again_after_the_restart()
    {
        _smtp.Next = Break.AfterMessage;
        _engine.SentLookup = _ => null;
        var id = await SendAsync();
        await UntilAsync(async () => (await _messages.GetAsync(id))!.SendState == SendState.Started && !_engine.IsBusy(id));
        await _engine.DisposeAsync();

        // Started again: nothing but what's on disk.
        _engine = NewEngine(new AccountRepository(_db), new NoCredentials());
        await SendAsync("Other");
        await UntilAsync(() => Task.FromResult(Asked == 1));
        Assert.Equal(2, _smtp.Received);
    }

    [Fact]
    public async Task Taken_by_the_server_but_not_filed_yet_is_only_filed()
    {
        _smtp.Next = Break.AfterMessage;
        var id = await SendAsync();
        await UntilAsync(() => Task.FromResult(Asked == 1));
        await _messages.SetSendStateAsync(id, SendState.Accepted);
        await SendAsync("Other");
        await UntilAsync(async () => (await _messages.GetAsync(id))?.FolderId == _sent.Id);
        await UntilAsync(() => Task.FromResult(_smtp.Received == 2));
    }

    [Theory]
    [InlineData(Break.BeforeMessage)] // broken off at the recipient
    [InlineData(Break.Busy)]          // "try later" after the whole message
    [InlineData(Break.HalfWay)]       // broken off in the middle of a big one
    public async Task One_that_cant_have_gone_out_goes_on_the_next_try(Break how)
    {
        _smtp.Next = how;
        var id = await SendAsync(bodySize: how == Break.HalfWay ? 8_000_000 : 20);
        await UntilAsync(async () => _smtp.Broken == 1 && !_engine.IsBusy(id));
        Assert.Equal(SendState.Waiting, (await _messages.GetAsync(id))!.SendState);

        await SendAsync("Other"); // tries again
        await UntilAsync(async () => (await _messages.GetAsync(id))?.FolderId == _sent.Id);
        await UntilAsync(() => Task.FromResult(_smtp.Received == 2));
        Assert.Equal(0, Asked);
    }

    [Fact]
    public async Task Taken_back_to_edit_it_starts_over()
    {
        _smtp.Next = Break.AfterMessage;
        var id = await SendAsync();
        await UntilAsync(() => Task.FromResult(Asked == 1));
        Assert.True(await _engine.MoveAsync((await _messages.GetAsync(id))!, _drafts));
        Assert.Equal(SendState.Waiting, (await _messages.GetAsync(id))!.SendState);
    }

    public enum Break { None, BeforeMessage, HalfWay, AfterMessage, Busy }

    /// <summary>Just enough of an SMTP server (over TLS) for one message at a time.</summary>
    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private int _received, _broken;

        /// <summary>How the next message goes wrong (the ones after it don't).</summary>
        public Break Next;
        /// <summary>Messages that arrived whole, whether they were answered or not.</summary>
        public int Received => Volatile.Read(ref _received);
        public int Broken => Volatile.Read(ref _broken);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeSmtp()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
            _listener.Start();
            _ = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true) _ = ServeAsync(await _listener.AcceptTcpClientAsync());
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(_certificate);
                using var reader = new StreamReader(tls, Encoding.Latin1);
                async Task Say(string line) => await tls.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
                await Say("220 test");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var command = line.Split(' ')[0].ToUpperInvariant();
                    if (command == "QUIT") { await Say("221 bye"); return; }
                    if (command == "RCPT" && Take(Break.BeforeMessage)) return;
                    if (command != "DATA") { await Say("250 ok"); continue; }

                    await Say("354 go on");
                    var lines = 0;
                    while (await reader.ReadLineAsync() is { } text && text != ".")
                        if (++lines == 100 && Take(Break.HalfWay)) return;
                    if (Take(Break.Busy)) { await Say("451 try later"); continue; }
                    Interlocked.Increment(ref _received);
                    if (Take(Break.AfterMessage)) return;
                    await Say("250 taken");
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        private bool Take(Break how)
        {
            if (Next != how) return false;
            Next = Break.None;
            Interlocked.Increment(ref _broken);
            return true;
        }

        public void Dispose()
        {
            _listener.Stop();
            _certificate.Dispose();
        }
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public void Save(long accountId, string secret) { }
        public string? Get(long accountId) => "secret";
        public void Delete(long accountId) { }
        public void SaveSmtp(long accountId, string secret) { }
        public string? GetSmtp(long accountId) => null;
        public void DeleteSmtp(long accountId) { }
    }
}
