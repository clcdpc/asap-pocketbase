using Asap.Web.Features.Email;

namespace Asap.Tests.Email;

[TestClass]
public sealed class FileEmailSenderTests
{
    private string temporaryDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"asap-email-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadinessAcceptsConfiguredOutputPathWithoutCreatingIt()
    {
        var sender = new FileEmailSender(temporaryDirectory);

        var readiness = await sender.CheckReadinessAsync(7, CancellationToken.None);

        Assert.IsTrue(readiness.IsConfigured);
        Assert.IsFalse(Directory.Exists(temporaryDirectory));
    }

    [TestMethod]
    public async Task SendWritesOneUniqueInspectableHtmlFilePerInvocation()
    {
        var sender = new FileEmailSender(temporaryDirectory);
        var envelope = new EmailEnvelope(
            42,
            7,
            "submission:<&\"42",
            "patron+<&\"@example.test",
            "asap@example.test",
            "ASAP <&\"",
            "A <useful> & safe subject",
            "Text <alternative> & details",
            "<p>Useful <strong>HTML body</strong></p>");

        var first = await sender.SendAsync(envelope, CancellationToken.None);
        var second = await sender.SendAsync(envelope, CancellationToken.None);

        var files = Directory.GetFiles(temporaryDirectory, "*.html");
        Assert.HasCount(2, files);
        Assert.AreNotEqual(files[0], files[1]);
        Assert.AreNotEqual(first.ProviderMessageId, second.ProviderMessageId);
        StringAssert.StartsWith(first.ProviderMessageId, "file:");
        Assert.IsEmpty(Directory.GetFiles(temporaryDirectory, "*.tmp"));

        var html = await File.ReadAllTextAsync(files[0]);
        StringAssert.Contains(html, "<td>42</td>");
        StringAssert.Contains(html, "<td>7</td>");
        StringAssert.Contains(html, "submission:&lt;&amp;&quot;42");
        StringAssert.Contains(html, "patron&#x2B;&lt;&amp;&quot;@example.test");
        StringAssert.Contains(html, "ASAP &lt;&amp;&quot; &lt;asap@example.test&gt;");
        StringAssert.Contains(html, "A &lt;useful&gt; &amp; safe subject");
        StringAssert.Contains(html, "<p>Useful <strong>HTML body</strong></p>");
        StringAssert.Contains(html, "Text &lt;alternative&gt; &amp; details");
    }

    [TestMethod]
    public async Task SendHonorsAlreadyCancelledTokenWithoutCreatingOutput()
    {
        var sender = new FileEmailSender(temporaryDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => sender.SendAsync(
            new EmailEnvelope(1, 1, null, "to@example.test", "from@example.test", null, "subject", null, "<p>body</p>"),
            cancellation.Token));

        Assert.IsFalse(Directory.Exists(temporaryDirectory));
    }
}
