using System.IO.Compression;
using Plicbox.Mail;

namespace Plicbox.Tests;

/// <summary>Attachments being sent: what mail services would refuse or hold back, and what won't open at the other end.</summary>
public sealed class OutgoingAttachmentTests
{
    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static readonly byte[] Some = [1, 2, 3];

    // ZipArchive can't write password-protected entries: set the "encrypted" flag by hand, as a locked zip has it.
    private static byte[] Encrypt(byte[] zip)
    {
        var bytes = (byte[])zip.Clone();
        for (var i = 0; i + 10 < bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04) bytes[i + 6] |= 1;
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02) bytes[i + 8] |= 1;
        }
        return bytes;
    }

    private static OutgoingWarning? Check(string name, byte[] content) =>
        OutgoingAttachments.Check(name, content.Length, () => new MemoryStream(content));

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("photo.jpg")]
    [InlineData("budget.xlsx")]
    public void Ordinary_files_are_fine(string name) => Assert.Null(Check(name, Some));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("build.BAT")]
    [InlineData("tool.js")]
    [InlineData("disk.iso")]
    public void Programs_would_be_refused(string name) => Assert.Equal(AttachmentRisk.Danger, Check(name, Some)?.Risk);

    [Fact]
    public void A_shortcut_is_not_the_file()
    {
        var w = Check("Quarterly report.lnk", Some);
        Assert.Equal(AttachmentRisk.Danger, w?.Risk);
        Assert.Contains("shortcut", w!.Text);
    }

    [Fact]
    public void Macros_may_be_held_back_and_the_plain_format_is_suggested()
    {
        var w = Check("totals.xlsm", Some);
        Assert.Equal(AttachmentRisk.Caution, w?.Risk);
        Assert.Contains(".xlsx", w!.Text);
    }

    [Fact]
    public void An_empty_file_is_mentioned() => Assert.Equal(AttachmentRisk.Caution, Check("notes.txt", [])?.Risk);

    [Fact]
    public void A_zip_of_documents_is_fine() => Assert.Null(Check("photos.zip", Zip(("a.jpg", Some), ("docs/b.pdf", Some))));

    [Fact]
    public void A_program_inside_a_zip_would_be_refused()
    {
        var w = Check("tools.zip", Zip(("readme.txt", Some), ("bin/setup.exe", Some)));
        Assert.Equal(AttachmentRisk.Danger, w?.Risk);
        Assert.Contains("setup.exe", w!.Text);
    }

    [Fact]
    public void A_program_in_a_zip_inside_a_zip_would_be_refused() =>
        Assert.Equal(AttachmentRisk.Danger, Check("outer.zip", Zip(("inner.zip", Zip(("run.cmd", Some)))))?.Risk);

    [Fact]
    public void A_password_locked_zip_may_be_held_back() =>
        Assert.Equal(AttachmentRisk.Caution, Check("private.zip", Encrypt(Zip(("contract.pdf", Some))))?.Risk);

    [Fact]
    public void A_file_called_zip_that_isnt_one_is_left_alone() => Assert.Null(Check("odd.zip", Some));

    [Fact]
    public void Forwarded_attachments_are_judged_by_name() =>
        Assert.Equal(AttachmentRisk.Danger, OutgoingAttachments.Check("invoice.pdf.exe", null, null)?.Risk);

    [Fact]
    public void Too_much_altogether_would_be_refused()
    {
        Assert.Null(OutgoingAttachments.CheckTotal(OutgoingAttachments.TypicalLimitBytes));
        Assert.Equal(AttachmentRisk.Danger, OutgoingAttachments.CheckTotal(OutgoingAttachments.TypicalLimitBytes + 1)?.Risk);
    }
}
