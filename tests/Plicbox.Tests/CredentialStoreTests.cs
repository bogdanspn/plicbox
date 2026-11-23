using Plicbox.Auth;

namespace Plicbox.Tests;

/// <summary>Runs against the real Windows Credential Manager under a throwaway prefix.</summary>
public sealed class CredentialStoreTests : IDisposable
{
    private const long Id = 1;
    private readonly WindowsCredentialStore _store = new($"Plicbox-test-{Guid.NewGuid():N}/");

    public void Dispose() => _store.Delete(Id);

    [Fact]
    public void Short_secrets_round_trip()
    {
        _store.Save(Id, "app-password");
        Assert.Equal("app-password", _store.Get(Id));
    }

    [Fact]
    public void Secrets_over_the_2560_byte_limit_are_split_and_joined_again()
    {
        var token = string.Concat(Enumerable.Range(0, 400).Select(i => $"{i:x4}é")); // 2000 chars, 4000 bytes in UTF-16
        _store.Save(Id, token);
        Assert.Equal(token, _store.Get(Id));

        _store.Save(Id, "short again"); // leftover parts are removed
        Assert.Equal("short again", _store.Get(Id));
    }

    [Fact]
    public void Delete_removes_every_part()
    {
        _store.Save(Id, new string('x', 4000));
        _store.Delete(Id);
        Assert.Null(_store.Get(Id));
        _store.Save(Id, new string('y', 1300));
        Assert.Equal(new string('y', 1300), _store.Get(Id)); // no stale parts from before
    }

    [Fact]
    public void Secrets_saved_under_the_former_name_are_taken_over()
    {
        var run = Guid.NewGuid().ToString("N");
        var former = new WindowsCredentialStore($"Mailroom-test-{run}/");
        var renamed = new WindowsCredentialStore($"Plicbox-test-{run}/", formerPrefix: $"Mailroom-test-{run}/");
        try
        {
            var token = new string('t', 3000); // long enough to be split into parts
            former.Save(Id, token);
            Assert.Equal(token, renamed.Get(Id));
            Assert.Equal(token, new WindowsCredentialStore($"Plicbox-test-{run}/").Get(Id)); // now kept under the new name

            renamed.Delete(Id); // both copies go, so the old one can't come back
            Assert.Null(renamed.Get(Id));
        }
        finally
        {
            renamed.Delete(Id);
        }
    }
}
