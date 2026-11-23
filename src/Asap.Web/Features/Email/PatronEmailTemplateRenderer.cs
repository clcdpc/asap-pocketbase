using System.Text.RegularExpressions;
using Asap.Web.Features.Patron;

namespace Asap.Web.Features.Email;

public sealed record RenderedEmailTemplate(string Subject, string BodyText, string BodyHtml);

public static partial class PatronEmailTemplateRenderer
{
    public static IReadOnlyList<string> SupportedPlaceholders { get; } =
        BuildValues("", "", "", null, "", "").Keys.ToArray();

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
        var values = BuildValues(firstName, lastName, title, author, formatLabel, barcode);

        var subjectTemplate = RemoveBlankAuthorPhrase(template.SubjectTemplate, values["author"]);
        var bodyTemplate = EmailBodyText.FromTemplate(RemoveBlankAuthorPhrase(template.BodyTemplate, values["author"]));
        var body = Replace(bodyTemplate, values).ReplaceLineEndings("\n");
        return new RenderedEmailTemplate(
            Replace(subjectTemplate, values), body, EmailBodyText.ToHtml(body));
    }

    private static Dictionary<string, string> BuildValues(
        string firstName,
        string lastName,
        string title,
        string? author,
        string formatLabel,
        string barcode) => new(StringComparer.Ordinal)
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

    private static string Replace(
        string template,
        IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(template, match => values.GetValueOrDefault(match.Groups[1].Value, match.Value));

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
