using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Asap.Web.Features.Patron;

namespace Asap.Web.Features.Email;

public sealed record RenderedEmailTemplate(string Subject, string BodyText, string BodyHtml);

public static partial class PatronEmailTemplateRenderer
{
    public static RenderedEmailTemplate Render(
        EffectiveEmailTemplate template,
        PatronSnapshot patron,
        string title,
        string? author,
        string formatLabel,
        string barcode)
    {
        var firstName = patron.NameFirst ?? string.Empty;
        var lastName = patron.NameLast ?? string.Empty;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = string.Join(' ', new[] { firstName, lastName }
                .Where(item => !string.IsNullOrWhiteSpace(item))).Trim() is { Length: > 0 } name
                ? name
                : "Library Patron",
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["title"] = RealValue(title),
            ["author"] = RealValue(author),
            ["format"] = formatLabel,
            ["barcode"] = barcode
        };

        var subjectTemplate = RemoveBlankAuthorPhrase(template.SubjectTemplate, values["author"]);
        var bodyTemplate = RemoveBlankAuthorPhrase(template.BodyTemplate, values["author"])
            .Replace("\\n", "\n", StringComparison.Ordinal);
        return new RenderedEmailTemplate(
            Replace(subjectTemplate, values, escape: false),
            Replace(bodyTemplate, values, escape: false),
            Replace(bodyTemplate, values, escape: true)
                .Replace("\r\n", "<br>", StringComparison.Ordinal)
                .Replace("\n", "<br>", StringComparison.Ordinal));
    }

    private static string Replace(
        string template,
        IReadOnlyDictionary<string, string> values,
        bool escape) =>
        PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
            {
                return match.Value;
            }

            return escape ? HtmlEncoder.Default.Encode(value) : value;
        });

    private static string RemoveBlankAuthorPhrase(string template, string author) =>
        author.Length == 0
            ? BlankAuthorRegex().Replace(template, string.Empty)
            : template;

    private static string RealValue(string? value)
    {
        var clean = value?.Trim() ?? string.Empty;
        var parenthesis = clean.IndexOf(" (", StringComparison.Ordinal);
        return parenthesis > 0 ? clean[..parenthesis].Trim() : clean;
    }

    [GeneratedRegex(@"{{(\w+)}}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s+by\s+{{author}}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlankAuthorRegex();
}
