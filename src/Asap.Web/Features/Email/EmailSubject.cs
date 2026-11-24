using System.Text;

namespace Asap.Web.Features.Email;

internal static class EmailSubject
{
    internal static string ToSingleLine(string subject)
    {
        var singleLine = subject.ReplaceLineEndings(" ");
        var builder = new StringBuilder(singleLine.Length);
        foreach (var character in singleLine)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Trim();
    }
}
