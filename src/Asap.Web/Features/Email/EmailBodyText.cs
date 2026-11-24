using System.Text.Encodings.Web;

namespace Asap.Web.Features.Email;

internal static class EmailBodyText
{
    internal static string? NonBlank(string? body) => string.IsNullOrWhiteSpace(body) ? null : body;

    internal static string FromTemplate(string template) =>
        template.Replace("\\n", "\n", StringComparison.Ordinal).ReplaceLineEndings("\n");

    internal static string ToHtml(string text) =>
        string.Join("<br>", text.ReplaceLineEndings("\n").Split('\n').Select(HtmlEncoder.Default.Encode));
}
