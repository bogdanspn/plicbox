using System.IO.Compression;
using MimeKit;
using Plicbox.Mail;

namespace Plicbox.Tests;

/// <summary>Attachments: what's inside a zip, and the "from the internet" mark on files written to disk.</summary>
public sealed class AttachmentProtectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-attach-" + Guid.NewGuid().ToString("N"));

    public AttachmentProtectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

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

    private static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static MimePart Part(string fileName, byte[] content, string type = "application/zip") =>
        new(MimeKit.ContentType.Parse(type)) { FileName = fileName, Content = new MimeContent(new MemoryStream(content)) };

    // ZipArchive can't write password-protected entries: set the "encrypted" flag by hand, as a locked zip has it.
    private static byte[] Encrypt(byte[] zip)
    {
        var bytes = (byte[])zip.Clone();
        for (var i = 0; i + 10 < bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04) bytes[i + 6] |= 1; // local header
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02) bytes[i + 8] |= 1; // central directory
        }
        return bytes;
    }

    [Fact]
    public void A_zip_of_documents_is_fine()
    {
        var a = AttachmentSafety.Assess(Part("invoices.zip", Zip(("march.pdf", Text("%PDF")), ("notes/april.docx", Text("x")))));
        Assert.Equal(AttachmentRisk.None, a.Risk);
    }

    [Fact]
    public void A_program_inside_a_zip_is_danger()
    {
        var a = AttachmentSafety.Assess(Part("Invoice_2041.zip", Zip(("Invoice_2041.pdf.lnk", Text("x")))));
        Assert.Equal(AttachmentRisk.Danger, a.Risk);
        Assert.Contains("Invoice_2041.pdf.lnk", a.Warning);
    }

    [Fact]
    public void A_program_in_a_zip_inside_a_zip_is_danger_too()
    {
        var inner = Zip(("setup.js", Text("WScript.Echo(1)")));
        var a = AttachmentSafety.Assess(Part("documents.zip", Zip(("readme.txt", Text("hi")), ("more.zip", inner))));
        Assert.Equal(AttachmentRisk.Danger, a.Risk);
        Assert.Contains("setup.js", a.Warning);

        // Three levels: not opened, but said.
        var deep = Zip(("level2.zip", Zip(("level3.zip", inner))));
        Assert.Equal(AttachmentRisk.Caution, AttachmentSafety.Assess(Part("deep.zip", deep)).Risk);
    }

    [Fact]
    public void A_password_protected_zip_is_danger()
    {
        var a = AttachmentSafety.Assess(Part("statement.zip", Encrypt(Zip(("statement.pdf", Text("%PDF"))))));
        Assert.Equal(AttachmentRisk.Danger, a.Risk);
        Assert.Contains("password", a.Warning);
    }

    [Fact]
    public void Web_pages_in_a_zip_and_unreadable_archives_are_caution()
    {
        Assert.Equal(AttachmentRisk.Caution, AttachmentSafety.Assess(Part("login.zip", Zip(("Sign in.html", Text("<form>"))))).Risk);
        Assert.Equal(AttachmentRisk.Caution, AttachmentSafety.Assess(Part("broken.zip", Text("PK not really a zip"))).Risk);
        Assert.Equal(AttachmentRisk.Caution, AttachmentSafety.Assess(Part("photos.rar", Text("Rar!"), "application/octet-stream")).Risk);
        // Office files are zips too, but aren't looked into as archives.
        Assert.Equal(AttachmentRisk.None, AttachmentSafety.Assess(Part("report.docx", Zip(("word/document.xml", Text("<w/>"))),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document")).Risk);
    }

    [Fact]
    public void Files_written_to_disk_are_marked_as_from_the_internet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(path, "Meeting at 10.");
        Assert.False(AttachmentQuarantine.HasMark(path));

        Assert.Equal(QuarantineResult.Marked, AttachmentQuarantine.Mark(path, "notes.txt"));
        Assert.True(AttachmentQuarantine.HasMark(path));
        Assert.Equal("Meeting at 10.", File.ReadAllText(path)); // the file itself is untouched
    }
}
