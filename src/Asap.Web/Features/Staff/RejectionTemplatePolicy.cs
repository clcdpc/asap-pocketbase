using System.Globalization;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Staff;

public sealed record RejectionTemplateChoice(string Id, string Name);

internal sealed record ResolvedRejectionTemplate(
    string Name,
    string Subject,
    string Body,
    bool IsHidden = false);

internal static class RejectionTemplatePolicy
{
    internal static ResolvedRejectionTemplate Default(
        IReadOnlyList<EmailTemplate> rows,
        int libraryOrgId)
    {
        var source = rows.SingleOrDefault(row => row.OrganizationId == 1 && row.TemplateKey == "rejected");
        var overrideRow = source is null ? null : rows.SingleOrDefault(row =>
            row.OrganizationId == libraryOrgId && !row.IsCustom && row.SourceTemplateId == source.Id);
        return new ResolvedRejectionTemplate(
            "Default rejection email",
            Clean(overrideRow?.SubjectTemplate) ?? Clean(source?.SubjectTemplate) ??
                "Update on your suggestion: {{title}}",
            Clean(overrideRow?.BodyTemplate) ?? Clean(source?.BodyTemplate) ??
                "Hello {{name}},\n\nWe are not able to add {{title}} to the collection at this time. Thank you for your suggestion.",
            source?.IsHidden == true || overrideRow?.IsHidden == true);
    }

    internal static IReadOnlyList<RejectionTemplateChoice> Choices(
        IReadOnlyList<EmailTemplate> rows,
        int libraryOrgId) => rows
        .Where(row => row.OrganizationId == 1 ||
            row.OrganizationId == libraryOrgId && (row.IsCustom || row.SourceTemplateId is null))
        .Select(row => (Row: row, Template: Resolve(rows, libraryOrgId, row.Id)))
        .Where(item => item.Template is not null)
        .OrderBy(item => item.Row.SortOrder)
        .ThenBy(item => item.Row.Id)
        .Select(item => new RejectionTemplateChoice(
            item.Row.Id.ToString(CultureInfo.InvariantCulture), item.Template!.Name))
        .ToArray();

    internal static ResolvedRejectionTemplate? Resolve(
        IReadOnlyList<EmailTemplate> rows,
        int libraryOrgId,
        long selectedId)
    {
        var selected = rows.SingleOrDefault(row => row.Id == selectedId);
        if (selected is null || selected.OrganizationId != 1 && selected.OrganizationId != libraryOrgId)
        {
            return null;
        }

        EmailTemplate source;
        EmailTemplate? overrideRow = null;
        if (selected.OrganizationId == 1 || selected.IsCustom || selected.SourceTemplateId is null)
        {
            source = selected;
            if (selected.OrganizationId == 1 && libraryOrgId != 1)
            {
                overrideRow = rows.SingleOrDefault(row => row.OrganizationId == libraryOrgId &&
                    !row.IsCustom && row.SourceTemplateId == source.Id);
            }
        }
        else
        {
            if (!selected.SourceTemplateId.HasValue)
            {
                return null;
            }
            source = rows.SingleOrDefault(row => row.Id == selected.SourceTemplateId.Value &&
                row.OrganizationId == 1)!;
            if (source is null)
            {
                return null;
            }
            overrideRow = selected;
        }

        var subject = Clean(overrideRow?.SubjectTemplate) ?? Clean(source.SubjectTemplate);
        var body = Clean(overrideRow?.BodyTemplate) ?? Clean(source.BodyTemplate);
        if (!source.TemplateKey.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase) ||
            source.IsHidden || overrideRow?.IsHidden == true || subject is null || body is null)
        {
            return null;
        }
        return new ResolvedRejectionTemplate(
            Clean(overrideRow?.DisplayName) ?? Clean(source.DisplayName) ?? source.TemplateKey,
            subject,
            body);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
