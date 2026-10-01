using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Patron;

public sealed partial class PatronSuggestionService
{
    private static ValidatedSuggestion Validate(
        PatronSuggestionInput input,
        EffectivePatronConfiguration configuration,
        bool allowInformationalMessage = false,
        bool? forcedAutoHold = null,
        DateOnly? exactPublicationDate = null,
        string? notes = null,
        int? verifiedBibId = null)
    {
        var formatCode = Clean(input.Format) ?? "book";
        var format = configuration.Formats.SingleOrDefault(item =>
            item.IsEnabled && string.Equals(item.Code, formatCode, StringComparison.Ordinal));
        if (format is null)
        {
            throw new PatronFlowException(400, "Choose a valid material format.");
        }

        if (!allowInformationalMessage &&
            !string.Equals(format.MessageBehavior, "none", StringComparison.Ordinal))
        {
            var message = format.MessageBehavior switch
            {
                "ebookMessage" => configuration.EbookMessage,
                "eaudiobookMessage" => configuration.EaudiobookMessage,
                _ => format.Message
            };
            throw new PatronFlowException(
                400,
                string.IsNullOrWhiteSpace(message)
                    ? "This format is informational only and cannot be submitted from the patron form."
                    : message);
        }

        var title = ValidateField(input.Title, format.Title, 500, forceRequired: true);
        var author = ValidateField(input.Author, format.Author, 500);
        var identifier = ValidateField(input.Isbn, format.Identifier, 100);
        var publication = ValidateField(input.Publication, format.Publication, 200);
        if (publication is not null &&
            !configuration.PublicationOptions.Contains(publication, StringComparer.Ordinal))
        {
            throw new PatronFlowException(400, $"{format.Publication.Label} is invalid.");
        }

        var customFields = ValidateCustomFields(input.CustomFields, format, configuration.CustomFields);
        var cleanedNotes = Clean(notes);
        if (cleanedNotes?.Length > 10000)
        {
            throw new PatronFlowException(400, "Notes cannot exceed 10000 characters.");
        }

        return new ValidatedSuggestion(
            format,
            TitleCase(title!),
            author,
            identifier,
            publication,
            forcedAutoHold ?? (configuration.AllowPatronAutoholdOptOut ? input.Autohold ?? true : true),
            customFields,
            format.Publication.Mode == "hidden" ? null : exactPublicationDate,
            cleanedNotes,
            verifiedBibId);
    }

    private static string? ValidateField(
        string? raw,
        EffectiveFieldRule rule,
        int maxLength,
        bool forceRequired = false)
    {
        if (rule.Mode == "hidden" && !forceRequired)
        {
            return null;
        }

        var value = Clean(raw);
        if (value?.Length > maxLength)
        {
            throw new PatronFlowException(400, $"{rule.Label} cannot exceed {maxLength} characters.");
        }

        if ((forceRequired || rule.Mode == "required") && value is null)
        {
            throw new PatronFlowException(400, $"{rule.Label} is required.");
        }

        return value;
    }

    private static string? ValidateCustomFields(
        IReadOnlyDictionary<string, string?>? submitted,
        EffectiveFormatRule format,
        IReadOnlyList<EffectiveCustomField> definitions)
    {
        var snapshot = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!format.CustomFields.TryGetValue(definition.Key, out var rule) || rule.Mode == "hidden")
            {
                continue;
            }

            string? raw = null;
            submitted?.TryGetValue(definition.Key, out raw);
            var value = Clean(raw);
            if (value is null)
            {
                if (rule.Mode == "required")
                {
                    throw new PatronFlowException(400, $"{rule.Label ?? definition.Label} is required.");
                }

                continue;
            }

            if (definition.Type == "select")
            {
                var option = definition.Options.SingleOrDefault(item =>
                    string.Equals(item.Key, value, StringComparison.Ordinal) ||
                    string.Equals(item.Label, value, StringComparison.Ordinal));
                if (option is null)
                {
                    if (rule.Mode == "required")
                    {
                        throw new PatronFlowException(400, $"{rule.Label ?? definition.Label} is required.");
                    }

                    continue;
                }

                snapshot[definition.Key] = new
                {
                    label = rule.Label ?? definition.Label,
                    type = definition.Type,
                    value = option.Key,
                    displayValue = option.Label
                };
                continue;
            }

            var maxLength = definition.Type == "textarea" ? 2000 : 250;
            snapshot[definition.Key] = new
            {
                label = rule.Label ?? definition.Label,
                type = definition.Type,
                value = value[..Math.Min(value.Length, maxLength)]
            };
        }

        return snapshot.Count == 0 ? null : JsonSerializer.Serialize(snapshot);
    }

    private static void EnforcePatronCodeEligibility(
        EffectivePatronConfiguration configuration,
        PatronSnapshot patron)
    {
        if (!configuration.PatronCodeEligibilityEnabled ||
            configuration.AllowedPatronCodeIds.Count == 0 ||
            !patron.PatronCodeId.HasValue ||
            configuration.AllowedPatronCodeIds.Contains(patron.PatronCodeId.Value))
        {
            return;
        }

        throw new PatronFlowException(
            403,
            configuration.PatronCodeEligibilityMessage,
            new { code = "patron_ineligible" });
    }

    private static void EnforceStaffPatronEligibility(
        EffectivePatronConfiguration configuration,
        PatronSnapshot patron)
    {
        if (patron.PatronOrganizationId <= 1 || patron.HomeLibraryOrganizationId <= 1 ||
            !configuration.AllowAnyRegisteredCardLogin &&
            patron.HomeLibraryOrganizationId != configuration.OrganizationId)
        {
            throw new PatronFlowException(
                403,
                "This patron is not eligible for the selected servicing library.",
                new { code = "patron_library_forbidden" });
        }

        EnforcePatronCodeEligibility(configuration, patron);
    }

    private static string? Missing(
        string? recipient,
        string? sender,
        EffectiveEmailTemplate? template,
        RecipientDomainPolicy recipientDomainPolicy,
        EmailTransportReadiness emailTransportReadiness)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return "recipient_missing";
        }

        if (string.IsNullOrWhiteSpace(sender))
        {
            return "sender_missing";
        }

        if (template is null)
        {
            return "template_missing";
        }

        if (!recipientDomainPolicy.IsAllowed(recipient))
        {
            return "recipient_domain_not_allowed";
        }

        if (!emailTransportReadiness.IsConfigured)
        {
            return "mail_not_configured";
        }

        return null;
    }

    private static string ParticipationMessage(EffectivePatronConfiguration configuration) =>
        $"The {configuration.OrganizationName} suggestion service is not currently enabled.";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string MergeCatalogValue(string? catalogValue, string? originalValue)
    {
        var catalog = Clean(catalogValue);
        var original = Clean(originalValue);
        if (catalog is null)
        {
            return original ?? string.Empty;
        }

        if (original is null || string.Equals(original, catalog, StringComparison.Ordinal))
        {
            return catalog;
        }

        if (original.StartsWith(catalog + " (", StringComparison.Ordinal))
        {
            return original;
        }

        var oldBase = Regex.Replace(original, @"\s+\([^()]*\)\s*$", string.Empty).Trim();
        return oldBase.Length > 0 &&
               (string.Equals(oldBase, catalog, StringComparison.Ordinal) ||
                oldBase.StartsWith(catalog, StringComparison.Ordinal) ||
                catalog.StartsWith(oldBase, StringComparison.Ordinal))
            ? original
            : $"{catalog} ({original})";
    }

    private static string? JoinNote(string? first, string second) =>
        first is null ? second : first + Environment.NewLine + second;

    [GeneratedRegex(@"\w\S*", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex TitleWordRegex();

    [GeneratedRegex("{{(\\w+)}}", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicatePlaceholderRegex();

    internal static string TitleCase(string value) =>
        TitleWordRegex().Replace(
            value.Trim(),
            match => char.ToUpperInvariant(match.Value[0]) +
                     match.Value[1..].ToLowerInvariant());

}
