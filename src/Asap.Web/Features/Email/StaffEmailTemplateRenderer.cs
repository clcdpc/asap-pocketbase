using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Email;

public sealed record StaffEmailPlaceholderDefinition(string Key, string Name, string Description, string Example);

public sealed record StaffEmailTemplateDefinition(
    string Key,
    string DisplayName,
    string WhenSent,
    IReadOnlyList<StaffEmailPlaceholderDefinition> Placeholders);

public sealed record EffectiveStaffEmailTemplate(string TemplateKey, string Subject, string Body, bool IsHidden);

public sealed record StaffEmailRequestValues(
    int LibraryOrganizationId,
    string? StaffUrl,
    string? RecipientName,
    string? ActorName,
    string? AssigneeName,
    string? LibraryName,
    string? RequestId,
    string? TaskId,
    string? RequestStage,
    string? BibId,
    string? Title,
    string? Author,
    string? Format);

public sealed record StaffEmailSummaryItem(
    string Id,
    int LibraryOrganizationId,
    string? Title,
    string? Author,
    string? BibId,
    bool IsAdditionalCopy,
    string Stage);

public sealed record StaffEmailWeeklyValues(
    int DataScopeOrganizationId,
    string? StaffUrl,
    string? RecipientName,
    string? LibraryName,
    DateTime PeriodStartLocal,
    DateTime PeriodEndLocal,
    int SubmissionCount,
    IReadOnlyList<StaffEmailSummaryItem> SubmissionItems,
    int PurchaseCount,
    IReadOnlyList<StaffEmailSummaryItem> PurchaseItems,
    int AdditionalCopyCount,
    IReadOnlyList<StaffEmailSummaryItem> AdditionalCopyItems);

public static partial class StaffEmailTemplateCatalog
{
    public const string TitleAssignment = "staff_title_assignment";
    public const string PurchaseReminder = "staff_purchase_reminder";
    public const string AdditionalCopyReminder = "staff_additional_copy_reminder";
    public const string AdditionalCopyAssignment = "staff_additional_copy_assignment";
    public const string NewPatronRequest = "staff_new_patron_request";
    public const string WeeklySummary = "staff_weekly_summary";

    private static readonly StaffEmailPlaceholderDefinition[] RequestPlaceholders =
    [
        new("recipientName", "Staff recipient name", "Display name of the staff member receiving this message.", "Casey Staff"),
        new("actorName", "Actor name", "Display name of the staff member who performed the action.", "Morgan Staff"),
        new("assigneeName", "Assignee name", "Display name of the staff member assigned to this request or task.", "Casey Staff"),
        new("libraryName", "Library name", "Name of the library that owns this request or task.", "Library Two"),
        new("requestId", "Title request ID", "Title request ID, preserved as text. Empty for messages without a linked title request.", "9007199254740993"),
        new("bibId", "BIB ID", "Polaris BIB ID when one is available.", "123456"),
        new("title", "Requested title", "Title or task title.", "Example title"),
        new("author", "Author or creator", "Author or creator when one is available; otherwise empty.", "Example creator"),
        new("format", "Material format", "Material-format label when one is available; otherwise empty.", "Print")
    ];

    private static readonly StaffEmailPlaceholderDefinition TaskIdPlaceholder =
        new("taskId", "Additional-copy task ID", "Additional-copy task ID, preserved as text.", "9007199254740994");

    private static readonly StaffEmailPlaceholderDefinition[] TitleAssignmentPlaceholders =
        RequestPlaceholders.Concat([RequestUrlPlaceholder("suggestion", "9007199254740993")]).ToArray();

    private static readonly StaffEmailPlaceholderDefinition[] PurchaseReminderPlaceholders =
        RequestPlaceholders.Where(item => item.Key != "assigneeName")
            .Concat([RequestUrlPlaceholder("outstanding_purchase", "9007199254740993")]).ToArray();

    private static readonly StaffEmailPlaceholderDefinition[] AdditionalCopyAssignmentPlaceholders =
        RequestPlaceholders.Concat([TaskIdPlaceholder, RequestUrlPlaceholder("additional_copies", "9007199254740994")]).ToArray();

    private static readonly StaffEmailPlaceholderDefinition[] AdditionalCopyReminderPlaceholders =
        AdditionalCopyAssignmentPlaceholders.Where(item => item.Key != "assigneeName").ToArray();

    private static readonly StaffEmailPlaceholderDefinition[] NewPatronRequestPlaceholders =
        RequestPlaceholders.Where(item => item.Key is "recipientName" or "libraryName" or "requestId" or "title" or "author" or "format")
            .Concat([RequestUrlPlaceholder("suggestion", "9007199254740993")]).ToArray();

    private static readonly StaffEmailPlaceholderDefinition[] WeeklyPlaceholders =
    [
        new("recipientName", "Staff recipient name", "Display name of the staff member receiving this message.", "Casey Staff"),
        new("libraryName", "Data scope", "Selected library name, or All libraries for an all-library summary.", "Library Two"),
        new("periodStart", "Period start", "First local business date in this summary.", "2026-11-05"),
        new("periodEnd", "Period end", "Last local business date in this summary.", "2026-11-11"),
        new("submissionCount", "New submission count", "Number of active suggestions in the summary's data scope.", "4"),
        new("submissionItems", "New submission samples", "Up to five recent suggestions, each with its own request link.", "1. Example title — Example creator\nView request: https://staff.example.org/staff/?request=9007199254740993&stage=suggestion&scope=2"),
        new("purchaseCount", "Purchase awaiting BIB count", "Number of active approved purchases without a BIB.", "2"),
        new("purchaseItems", "Purchase samples", "Up to five recently updated purchases, each with its own request link.", "1. Example title — Example creator\nView request: https://staff.example.org/staff/?request=9007199254740994&stage=outstanding_purchase&scope=2"),
        new("additionalCopyCount", "Additional-copy task count", "Number of open additional-copy tasks in the summary's data scope.", "3"),
        new("additionalCopyItems", "Additional-copy samples", "Up to five recent tasks, each with its own type-qualified task link.", "1. Example title — Example creator\nView task: https://staff.example.org/staff/?request=9007199254740995&stage=additional_copies&scope=2"),
        new("submissionQueueUrl", "Submission queue URL", "Queue link for suggestions within this summary's data scope.", "https://staff.example.org/staff/?stage=submitted&scope=2"),
        new("purchaseQueueUrl", "Purchase queue URL", "Queue link for purchases awaiting BIBs within this summary's data scope.", "https://staff.example.org/staff/?stage=purchased_waiting_for_bib&scope=2"),
        new("additionalCopiesQueueUrl", "Additional-copies queue URL", "Queue link for additional-copy tasks within this summary's data scope.", "https://staff.example.org/staff/?stage=additional_copies&scope=2")
    ];

    public static IReadOnlyList<StaffEmailTemplateDefinition> Definitions { get; } =
    [
        new(TitleAssignment, "Title request assigned", "Sent when a title request is assigned to a staff member.", TitleAssignmentPlaceholders),
        new(PurchaseReminder, "Purchase reminder", "Sent when staff request a reminder while recording a purchase.", PurchaseReminderPlaceholders),
        new(AdditionalCopyReminder, "Additional-copy reminder", "Sent when staff request a reminder after creating an additional-copy task.", AdditionalCopyReminderPlaceholders),
        new(AdditionalCopyAssignment, "Additional-copy task assigned", "Sent when an additional-copy task is assigned to a staff member.", AdditionalCopyAssignmentPlaceholders),
        new(NewPatronRequest, "New patron purchase request", "Sent to subscribed staff who can view a newly submitted patron purchase request.", NewPatronRequestPlaceholders),
        new(WeeklySummary, "Weekly action summary", "Sent weekly to subscribed staff for the summary's authorized library or all-library data scope.", WeeklyPlaceholders)
    ];

    public static StaffEmailTemplateDefinition? Find(string templateKey) =>
        Definitions.SingleOrDefault(item => string.Equals(item.Key, templateKey, StringComparison.Ordinal));

    public static IReadOnlyList<string> SupportedPlaceholderKeys(string templateKey) =>
        Find(templateKey)?.Placeholders.Select(item => item.Key).ToArray() ?? [];

    public static string? FindUnsupportedPlaceholder(string templateKey, string? content)
    {
        var supported = SupportedPlaceholderKeys(templateKey).ToHashSet(StringComparer.Ordinal);
        foreach (Match match in PlaceholderRegex().Matches(content ?? string.Empty))
        {
            var key = match.Groups[1].Value;
            if (!supported.Contains(key))
            {
                return key;
            }
        }
        return null;
    }

    private static StaffEmailPlaceholderDefinition RequestUrlPlaceholder(string stage, string requestId) =>
        new("requestUrl", "Request or task URL", "Absolute link to this exact record, scoped to its library and routed by record type.",
            $"https://staff.example.org/staff/?request={requestId}&stage={stage}&scope=2");

    public static async Task<EffectiveStaffEmailTemplate?> LoadEffectiveAsync(
        AsapDbContext context,
        int organizationId,
        string templateKey,
        CancellationToken cancellationToken)
    {
        if (Find(templateKey) is null)
        {
            return null;
        }

        var system = await context.EmailTemplates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId &&
                item.TemplateKey == templateKey, cancellationToken);
        if (system is null)
        {
            return null;
        }

        var library = organizationId == LibraryScope.SystemOrganizationId
            ? null
            : await context.EmailTemplates.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == organizationId &&
                    item.SourceTemplateId == system.Id, cancellationToken);
        return new EffectiveStaffEmailTemplate(
            templateKey,
            library?.SubjectTemplate ?? system.SubjectTemplate ?? string.Empty,
            library?.BodyTemplate ?? system.BodyTemplate ?? string.Empty,
            system.IsHidden || library?.IsHidden == true);
    }

    [GeneratedRegex(@"{{(\w+)}}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}

public static partial class StaffEmailTemplateRenderer
{
    public static bool IsStaffUrlConfigured(string? staffUrl) => StaffApplicationLinkBuilder.TryGetBase(staffUrl, out _);

    public static RenderedEmailTemplate RenderRequest(
        EffectiveStaffEmailTemplate template,
        StaffEmailRequestValues request)
    {
        var requestUrl = request.TaskId is not null
            ? StaffApplicationLinkBuilder.BuildRecordUrl(request.StaffUrl, request.TaskId, "additional_copies", request.LibraryOrganizationId)
            : request.RequestId is not null
                ? StaffApplicationLinkBuilder.BuildRecordUrl(request.StaffUrl, request.RequestId,
                    request.RequestStage ?? RequestStatus.Suggestion, request.LibraryOrganizationId)
                : null;
        var values = new Dictionary<string, TemplateValue>(StringComparer.Ordinal)
        {
            ["recipientName"] = Value(NonEmpty(request.RecipientName, "Staff member")),
            ["actorName"] = Value(NonEmpty(request.ActorName, "Staff member")),
            ["assigneeName"] = Value(NonEmpty(request.AssigneeName, "Staff member")),
            ["libraryName"] = Value(request.LibraryName),
            ["requestId"] = Value(request.RequestId),
            ["taskId"] = Value(request.TaskId),
            ["bibId"] = Value(request.BibId),
            ["title"] = Value(request.Title),
            ["author"] = Value(request.Author),
            ["format"] = Value(request.Format),
            ["requestUrl"] = Link(requestUrl)
        };
        return Render(template, values);
    }

    public static RenderedEmailTemplate RenderWeekly(
        EffectiveStaffEmailTemplate template,
        StaffEmailWeeklyValues summary)
    {
        var allLibraries = summary.DataScopeOrganizationId == LibraryScope.SystemOrganizationId;
        var scope = allLibraries ? "system" : summary.DataScopeOrganizationId.ToString(CultureInfo.InvariantCulture);
        var values = new Dictionary<string, TemplateValue>(StringComparer.Ordinal)
        {
            ["recipientName"] = Value(NonEmpty(summary.RecipientName, "Staff member")),
            ["libraryName"] = Value(allLibraries ? "All libraries" : NonEmpty(summary.LibraryName, "Library")),
            ["periodStart"] = Value(summary.PeriodStartLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["periodEnd"] = Value(summary.PeriodEndLocal.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["submissionCount"] = Value(summary.SubmissionCount.ToString(CultureInfo.InvariantCulture)),
            ["submissionItems"] = SummaryItems(summary.SubmissionItems, summary.StaffUrl, "View request"),
            ["purchaseCount"] = Value(summary.PurchaseCount.ToString(CultureInfo.InvariantCulture)),
            ["purchaseItems"] = SummaryItems(summary.PurchaseItems, summary.StaffUrl, "View request"),
            ["additionalCopyCount"] = Value(summary.AdditionalCopyCount.ToString(CultureInfo.InvariantCulture)),
            ["additionalCopyItems"] = SummaryItems(summary.AdditionalCopyItems, summary.StaffUrl, "View task"),
            ["submissionQueueUrl"] = Link(StaffApplicationLinkBuilder.BuildQueueUrl(summary.StaffUrl, "submitted", scope)),
            ["purchaseQueueUrl"] = Link(StaffApplicationLinkBuilder.BuildQueueUrl(summary.StaffUrl, "purchased_waiting_for_bib", scope)),
            ["additionalCopiesQueueUrl"] = Link(StaffApplicationLinkBuilder.BuildQueueUrl(summary.StaffUrl, "additional_copies", scope))
        };
        return Render(template, values);
    }

    private static RenderedEmailTemplate Render(
        EffectiveStaffEmailTemplate template,
        IReadOnlyDictionary<string, TemplateValue> values)
    {
        var supported = StaffEmailTemplateCatalog.SupportedPlaceholderKeys(template.TemplateKey)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = values.Where(item => supported.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var subject = SanitizeSubject(ReplaceText(template.Subject, filtered));
        var bodyTemplate = template.Body.Replace("\\n", "\n", StringComparison.Ordinal);
        return new RenderedEmailTemplate(subject, ReplaceText(bodyTemplate, filtered), ReplaceHtml(bodyTemplate, filtered));
    }

    private static TemplateValue SummaryItems(
        IReadOnlyList<StaffEmailSummaryItem> items,
        string? staffUrl,
        string linkLabel)
    {
        if (items.Count == 0)
        {
            return Value("None");
        }

        var textLines = new List<string>();
        var htmlLines = new List<string>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var title = NonEmpty(item.Title, "Untitled");
            var author = NonEmpty(item.Author, item.BibId ?? string.Empty);
            var details = $"{index + 1}. {title}{(author.Length == 0 ? string.Empty : $" — {author}")}";
            var url = StaffApplicationLinkBuilder.BuildRecordUrl(staffUrl, item.Id, item.Stage, item.LibraryOrganizationId);
            textLines.Add(url is null ? details : $"{details}\n{linkLabel}: {url}");
            htmlLines.Add(url is null
                ? EncodeWithBreaks(details)
                : $"{EncodeWithBreaks(details)}<br>{HtmlEncoder.Default.Encode(linkLabel)}: {Link(url).Html}");
        }
        return new TemplateValue(string.Join("\n", textLines), string.Join("<br>", htmlLines));
    }

    private static string ReplaceText(string template, IReadOnlyDictionary<string, TemplateValue> values) =>
        PlaceholderRegex().Replace(template, match => values.TryGetValue(match.Groups[1].Value, out var value)
            ? value.Text
            : match.Value);

    private static string ReplaceHtml(string template, IReadOnlyDictionary<string, TemplateValue> values)
    {
        var builder = new StringBuilder(template.Length + 32);
        var offset = 0;
        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            builder.Append(EncodeWithBreaks(template[offset..match.Index]));
            var key = match.Groups[1].Value;
            builder.Append(values.TryGetValue(key, out var value) ? value.Html : HtmlEncoder.Default.Encode(match.Value));
            offset = match.Index + match.Length;
        }
        builder.Append(EncodeWithBreaks(template[offset..]));
        return builder.ToString();
    }

    private static string EncodeWithBreaks(string value) => HtmlEncoder.Default.Encode(value)
        .Replace("\r\n", "<br>", StringComparison.Ordinal)
        .Replace("\r", "<br>", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);

    private static string SanitizeSubject(string subject)
    {
        var builder = new StringBuilder(subject.Length);
        foreach (var character in subject)
        {
            if (character is '\r' or '\n')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Trim();
    }

    private static TemplateValue Link(string? url)
    {
        if (url is null)
        {
            return Value(string.Empty);
        }
        var encoded = HtmlEncoder.Default.Encode(url);
        return new TemplateValue(url, $"<a href=\"{encoded}\">{encoded}</a>");
    }

    private static TemplateValue Value(string? value)
    {
        var text = value ?? string.Empty;
        return new TemplateValue(text, EncodeWithBreaks(text));
    }

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed record TemplateValue(string Text, string Html);

    [GeneratedRegex(@"{{(\w+)}}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}

internal static class StaffApplicationLinkBuilder
{
    internal static string? BuildRecordUrl(string? configuredStaffUrl, string recordId, string stage, int organizationId)
    {
        if (!TryGetBase(configuredStaffUrl, out var baseUrl) || string.IsNullOrWhiteSpace(recordId) ||
            organizationId <= 0 || !IsRecordStage(stage))
        {
            return null;
        }
        return $"{baseUrl}?request={Uri.EscapeDataString(recordId)}&stage={Uri.EscapeDataString(stage)}&scope={ScopeValue(organizationId)}";
    }

    internal static string? BuildQueueUrl(string? configuredStaffUrl, string stage, string scope)
    {
        if (!TryGetBase(configuredStaffUrl, out var baseUrl) ||
            stage is not ("submitted" or "purchased_waiting_for_bib" or "additional_copies") ||
            !(scope == "system" || int.TryParse(scope, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 1))
        {
            return null;
        }
        return $"{baseUrl}?stage={Uri.EscapeDataString(stage)}&scope={Uri.EscapeDataString(scope)}";
    }

    internal static bool TryGetBase(string? configuredStaffUrl, out string baseUrl)
    {
        baseUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(configuredStaffUrl) || configuredStaffUrl.Any(char.IsControl) ||
            configuredStaffUrl.Contains('\\') ||
            !Uri.TryCreate(configuredStaffUrl.Trim(), UriKind.Absolute, out var configured) ||
            !string.IsNullOrEmpty(configured.UserInfo) || configured.Host.Length == 0 ||
            configured.AbsolutePath is not ("/staff" or "/staff/") ||
            configured.Query.Length > 0 || configured.Fragment.Length > 0 ||
            (configured.Scheme != Uri.UriSchemeHttps &&
             !(configured.Scheme == Uri.UriSchemeHttp && configured.Host is "localhost" or "127.0.0.1" or "::1")))
        {
            return false;
        }

        baseUrl = $"{configured.GetLeftPart(UriPartial.Authority)}/staff/";
        return true;
    }

    private static string ScopeValue(int organizationId) => organizationId == LibraryScope.SystemOrganizationId
        ? "system"
        : organizationId.ToString(CultureInfo.InvariantCulture);

    private static bool IsRecordStage(string stage) => stage is
        RequestStatus.Suggestion or RequestStatus.OutstandingPurchase or RequestStatus.PendingHold or RequestStatus.HoldPlaced or RequestStatus.Closed or "additional_copies";
}
