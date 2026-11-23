using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;

namespace Asap.Tests.Email;

[TestClass]
public sealed class StaffEmailTemplateRendererTests
{
    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void CustomBodyPreservesSpacingAndEncodesMultilineValues(string newline)
    {
        var title = new string('X', 500) + "&" + newline + "<next>";
        var id = new string('3', 150);
        var template = new EffectiveStaffEmailTemplate(StaffEmailTemplateCatalog.NewPatronRequest, " \tSubject:" + newline + "{{title}}\0 ",
            "<Notice>\\n" + newline + "Title: {{title}}" + newline + "Author / creator: {{author}}" + newline +
            "Format: {{format}}" + newline + newline + "View request in ASAP: {{requestUrl}}", false);
        var values = new StaffEmailRequestValues(2, "https://staff.example.org/staff/", null, null, null, null,
            id, null, "suggestion", null, title, "", null);
        var mail = StaffEmailTemplateRenderer.RenderRequest(template, values);
        var url = $"https://staff.example.org/staff/?request={id}&stage=suggestion&scope=2";
        Assert.AreEqual("Subject: " + new string('X', 500) + "& <next>", mail.Subject);
        Assert.AreEqual("<Notice>\n\nTitle: " + title.ReplaceLineEndings("\n") + "\n\nView request in ASAP: " + url, mail.BodyText);
        Assert.AreEqual("&lt;Notice&gt;<br><br>Title: " + new string('X', 500) + "&amp;<br>&lt;next&gt;<br><br>" +
            $"<a href=\"{url.Replace("&", "&amp;")}\">View request in ASAP</a>", mail.BodyHtml);
    }

    [TestMethod]
    public void RequestTemplatesUseStaffTokensTypedScopedLinksAndSafeHtml()
    {
        var template = new EffectiveStaffEmailTemplate(
            StaffEmailTemplateCatalog.TitleAssignment,
            "{{recipientName}}: {{title}}",
            "{{actorName}} assigned {{title}} by {{author}}. {{requestUrl}} {{name}} {{unknown}}",
            false);
        var values = new StaffEmailRequestValues(
            LibraryOrganizationId: 2,
            StaffUrl: "https://staff.example.org/staff/",
            RecipientName: "Casey Staff",
            ActorName: "Morgan Staff",
            AssigneeName: "Casey Staff",
            LibraryName: "Library Two",
            RequestId: "9007199254740993",
            TaskId: null,
            RequestStage: RequestStatus.Suggestion,
            BibId: null,
            Title: "<script>alert('x')</script> & book",
            Author: null,
            Format: "Print");

        var rendered = StaffEmailTemplateRenderer.RenderRequest(template, values);

        Assert.AreEqual("Casey Staff: <script>alert('x')</script> & book", rendered.Subject);
        StringAssert.Contains(rendered.BodyText!, "Morgan Staff assigned <script>alert('x')</script> & book by .");
        StringAssert.Contains(rendered.BodyText!,
            "https://staff.example.org/staff/?request=9007199254740993&stage=suggestion&scope=2");
        StringAssert.Contains(rendered.BodyText!, "{{name}} {{unknown}}");
        Assert.IsFalse(rendered.BodyHtml!.Contains("<script>", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(rendered.BodyHtml,
            "&lt;script&gt;alert(");
        StringAssert.Contains(rendered.BodyHtml, "&lt;/script&gt; &amp; book");
        StringAssert.Contains(rendered.BodyHtml,
            "<a href=\"https://staff.example.org/staff/?request=9007199254740993&amp;stage=suggestion&amp;scope=2\">View request in ASAP</a>");
    }

    [TestMethod]
    public void AdditionalCopyLinksKeepTaskIdentityAndInvalidStaffUrlsDoNotInventAHost()
    {
        var template = new EffectiveStaffEmailTemplate(
            StaffEmailTemplateCatalog.AdditionalCopyAssignment,
            "Task {{taskId}} for {{title}}",
            "{{requestUrl}}",
            false);
        var values = new StaffEmailRequestValues(
            LibraryOrganizationId: 2,
            StaffUrl: "https://staff.example.org/staff/",
            RecipientName: "Casey Staff",
            ActorName: "Morgan Staff",
            AssigneeName: "Casey Staff",
            LibraryName: "Library Two",
            RequestId: "9007199254740993",
            TaskId: "9007199254740993",
            RequestStage: RequestStatus.OutstandingPurchase,
            BibId: "123456",
            Title: "Extra copy",
            Author: "Example Author",
            Format: "Print");

        var rendered = StaffEmailTemplateRenderer.RenderRequest(template, values);

        StringAssert.Contains(rendered.BodyText!,
            "https://staff.example.org/staff/?request=9007199254740993&stage=additional_copies&scope=2");
        Assert.IsFalse(rendered.BodyText!.Contains("stage=outstanding_purchase", StringComparison.Ordinal));
        StringAssert.Contains(rendered.BodyHtml, "&amp;stage=additional_copies&amp;scope=2\">View additional-copy task in ASAP</a>");

        var invalid = StaffEmailTemplateRenderer.RenderRequest(template, values with { StaffUrl = "//attacker.example/" });

        Assert.AreEqual(string.Empty, invalid.BodyText);
        Assert.IsFalse(invalid.BodyText.Contains("attacker.example", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, invalid.BodyHtml);
    }

    [TestMethod]
    public void WeeklyTemplatesUseDataScopeAndKeepPerItemTypeLibraryAndLargeIdLinks()
    {
        var template = new EffectiveStaffEmailTemplate(
            StaffEmailTemplateCatalog.WeeklySummary,
            "{{libraryName}} {{submissionCount}}",
            "{{periodStart}}–{{periodEnd}}\n{{submissionItems}}\n{{purchaseItems}}\n{{additionalCopyItems}}\n" +
            "{{submissionQueueUrl}}\n{{purchaseQueueUrl}}\n{{additionalCopiesQueueUrl}}",
            false);
        var overlappingId = "9007199254740993";
        var summary = new StaffEmailWeeklyValues(
            DataScopeOrganizationId: 2,
            StaffUrl: "https://staff.example.org/staff/",
            RecipientName: "Casey <Staff>",
            LibraryName: "Library Two",
            PeriodStartLocal: new DateTime(2026, 11, 5),
            PeriodEndLocal: new DateTime(2026, 11, 12),
            SubmissionCount: 4,
            SubmissionItems: [new(overlappingId, 2, "<Submission & title>", "Author <script>", null, false, RequestStatus.Suggestion)],
            PurchaseCount: 2,
            PurchaseItems: [new(overlappingId, 3, "Purchase title", "Creator", null, false, RequestStatus.OutstandingPurchase)],
            AdditionalCopyCount: 3,
            AdditionalCopyItems: [new(overlappingId, 2, "Copy title", null, "123456", true, "additional_copies")]);

        var rendered = StaffEmailTemplateRenderer.RenderWeekly(template, summary);

        Assert.AreEqual("Library Two 4", rendered.Subject);
        StringAssert.Contains(rendered.BodyText!, "2026-11-05–2026-11-11");
        StringAssert.Contains(rendered.BodyText!,
            $"request={overlappingId}&stage=suggestion&scope=2");
        StringAssert.Contains(rendered.BodyText!,
            $"request={overlappingId}&stage=outstanding_purchase&scope=3");
        StringAssert.Contains(rendered.BodyText!,
            $"request={overlappingId}&stage=additional_copies&scope=2");
        StringAssert.Contains(rendered.BodyText!, "https://staff.example.org/staff/?stage=submitted&scope=2");
        StringAssert.Contains(rendered.BodyText!, "https://staff.example.org/staff/?stage=purchased_waiting_for_bib&scope=2");
        StringAssert.Contains(rendered.BodyText!, "https://staff.example.org/staff/?stage=additional_copies&scope=2");
        Assert.IsFalse(rendered.BodyHtml!.Contains("<script>", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(rendered.BodyHtml, "&lt;Submission &amp; title&gt;");
        StringAssert.Contains(rendered.BodyHtml,
            $"href=\"https://staff.example.org/staff/?request={overlappingId}&amp;stage=suggestion&amp;scope=2\"");
        StringAssert.Contains(rendered.BodyHtml, $">View request {overlappingId} in ASAP</a>");
        StringAssert.Contains(rendered.BodyHtml, $">View additional-copy task {overlappingId} in ASAP</a>");
        StringAssert.Contains(rendered.BodyHtml, ">View new submissions</a>");
        StringAssert.Contains(rendered.BodyHtml, ">View purchases awaiting BIBs</a>");
        StringAssert.Contains(rendered.BodyHtml, ">View Additional copies</a>");

        var empty = StaffEmailTemplateRenderer.RenderWeekly(template, summary with
        {
            SubmissionCount = 0, SubmissionItems = [], PurchaseCount = 0, PurchaseItems = [],
            AdditionalCopyCount = 0, AdditionalCopyItems = []
        });
        StringAssert.Contains(empty.BodyText, "\nNone\nNone\nNone\n");
        StringAssert.Contains(empty.BodyHtml, "<br>None<br>None<br>None<br>");

        var allLibraries = StaffEmailTemplateRenderer.RenderWeekly(template, summary with
        {
            DataScopeOrganizationId = LibraryScope.SystemOrganizationId,
            LibraryName = null
        });
        Assert.AreEqual("All libraries 4", allLibraries.Subject);
        StringAssert.Contains(allLibraries.BodyText!, "?stage=submitted&scope=system");
        StringAssert.Contains(allLibraries.BodyText!, $"request={overlappingId}&stage=outstanding_purchase&scope=3");

        var invalidUrl = StaffEmailTemplateRenderer.RenderWeekly(template, summary with { StaffUrl = "//attacker.example/" });
        Assert.IsFalse(invalidUrl.BodyText!.Contains("attacker.example", StringComparison.Ordinal));
        Assert.IsFalse(invalidUrl.BodyText.Contains("https://staff.example.org", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StaffPlaceholderCatalogKeepsPatronAndTaskCapabilitiesSeparate()
    {
        CollectionAssert.DoesNotContain(StaffEmailTemplateCatalog.SupportedPlaceholderKeys(StaffEmailTemplateCatalog.TitleAssignment).ToArray(), "name");
        CollectionAssert.DoesNotContain(StaffEmailTemplateCatalog.SupportedPlaceholderKeys(StaffEmailTemplateCatalog.TitleAssignment).ToArray(), "taskId");
        CollectionAssert.DoesNotContain(StaffEmailTemplateCatalog.SupportedPlaceholderKeys(StaffEmailTemplateCatalog.AdditionalCopyReminder).ToArray(), "assigneeName");
        CollectionAssert.Contains(StaffEmailTemplateCatalog.SupportedPlaceholderKeys(StaffEmailTemplateCatalog.AdditionalCopyAssignment).ToArray(), "taskId");
        CollectionAssert.Contains(StaffEmailTemplateCatalog.SupportedPlaceholderKeys(StaffEmailTemplateCatalog.AdditionalCopyAssignment).ToArray(), "assigneeName");
        StringAssert.Contains(StaffEmailTemplateCatalog.Find(StaffEmailTemplateCatalog.TitleAssignment)!.Placeholders
            .Single(item => item.Key == "requestUrl").Example, "stage=suggestion");
        StringAssert.Contains(StaffEmailTemplateCatalog.Find(StaffEmailTemplateCatalog.PurchaseReminder)!.Placeholders
            .Single(item => item.Key == "requestUrl").Example, "stage=outstanding_purchase");
        StringAssert.Contains(StaffEmailTemplateCatalog.Find(StaffEmailTemplateCatalog.AdditionalCopyReminder)!.Placeholders
            .Single(item => item.Key == "requestUrl").Example, "stage=additional_copies");
        Assert.AreEqual("name", StaffEmailTemplateCatalog.FindUnsupportedPlaceholder(
            StaffEmailTemplateCatalog.TitleAssignment, "Hello {{name}}"));
        Assert.AreEqual("assigneeName", StaffEmailTemplateCatalog.FindUnsupportedPlaceholder(
            StaffEmailTemplateCatalog.AdditionalCopyReminder, "Assigned to {{assigneeName}}"));
        Assert.IsNull(StaffEmailTemplateCatalog.FindUnsupportedPlaceholder(
            StaffEmailTemplateCatalog.AdditionalCopyAssignment, "Task {{taskId}}: {{requestUrl}}"));
    }
}
