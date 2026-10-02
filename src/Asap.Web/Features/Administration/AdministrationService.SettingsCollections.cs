using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Shared;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Administration;

public sealed partial class AdministrationService
{
    private async Task ReplaceOriginsIfPresentAsync(
        AsapDbContext context,
        JsonElement payload,
        JsonElement systemSection,
        CancellationToken cancellationToken)
    {
        if (!TryGetAny(systemSection, out var value, "patronEmbedAllowedOrigins") &&
            !TryGetAny(payload, out value, "patronEmbedAllowedOrigins", "origins"))
        {
            return;
        }

        var normalized = ParseValues(value)
            .Select(NormalizeOrigin)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var existing = await context.PatronEmbedAllowedOrigins
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .ToListAsync(cancellationToken);
        var existingNormalized = existing.Select(item => item.NormalizedOrigin)
            .Order(StringComparer.Ordinal).ToArray();
        if (existingNormalized.SequenceEqual(normalized, StringComparer.Ordinal))
        {
            return;
        }
        context.PatronEmbedAllowedOrigins.RemoveRange(existing);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        context.PatronEmbedAllowedOrigins.AddRange(normalized.Select(origin => new PatronEmbedAllowedOrigin
        {
            OrganizationId = 1,
            Origin = origin,
            NormalizedOrigin = origin,
            CreatedUtc = now
        }));
    }

    private async Task ApplyParticipationAsync(
        AsapDbContext context,
        JsonElement payload,
        JsonElement systemSection,
        CancellationToken cancellationToken)
    {
        if (!TryGetAny(systemSection, out var value, "enabledLibraryOrgIds", "enabledLibraries") &&
            !TryGetAny(payload, out value, "enabledLibraryOrgIds", "enabledLibraries"))
        {
            return;
        }

        var enabled = ParseValues(value)
            .Select(item => int.TryParse(item, out var id) ? id : 0)
            .Where(item => item > 1)
            .ToHashSet();
        var organizations = await context.Organizations
            .Where(item => item.Id > 1)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var revoked = organizations
            .Where(item => item.IsActive && !enabled.Contains(item.Id))
            .Select(item => item.Id)
            .ToArray();
        foreach (var organization in organizations)
        {
            organization.IsActive = enabled.Contains(organization.Id);
        }

        if (revoked.Length > 0)
        {
            var sessions = await context.PatronSessions
                .Where(item => revoked.Contains(item.EffectiveOrganizationId) && item.RevokedUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var session in sessions)
            {
                session.RevokedUtc = now;
            }
        }
    }

    private static async Task ApplyWholeSetsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement workflow,
        JsonElement patron,
        CancellationToken cancellationToken)
    {
        if (TryGetAny(workflow, out var creators, "commonAuthorsList", "commonCreators", "commonCreatorsList"))
        {
            var values = ParseValues(creators);
            await ReplaceCommonCreatorsAsync(context, organizationId, values,
                creators.ValueKind == JsonValueKind.Null, cancellationToken);
        }

        if (TryGetAny(workflow, out var patronCodes, "allowedPatronCodeIds", "patronCodeIds"))
        {
            if (!TryParsePatronCodeIds(patronCodes, out var values))
            {
                throw new InvalidOperationException("Patron-code payload was not validated.");
            }
            await ReplacePatronCodesAsync(context, organizationId, values,
                patronCodes.ValueKind == JsonValueKind.Null, cancellationToken);
        }

        if (TryGetAny(patron, out var publicationOptions, "publicationOptions", "publicationOptionSet"))
        {
            var values = ParseOptions(publicationOptions);
            await ReplacePublicationOptionsAsync(context, organizationId, values,
                publicationOptions.ValueKind == JsonValueKind.Null, cancellationToken);
        }
    }

    private static async Task ReplaceCommonCreatorsAsync(
        AsapDbContext context,
        int organizationId,
        IReadOnlyList<string> values,
        bool resetToSystem,
        CancellationToken cancellationToken)
    {
        var existingSet = await context.CommonCreatorSets
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var terms = await context.CommonCreatorTerms
            .Where(item => item.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (organizationId != LibraryScope.SystemOrganizationId && resetToSystem)
        {
            if (existingSet is null && terms.Count == 0)
            {
                return;
            }
            context.CommonCreatorTerms.RemoveRange(terms);
            if (existingSet is not null)
            {
                context.CommonCreatorSets.Remove(existingSet);
            }

            return;
        }

        var existingTerms = terms.OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => item.Value).ToArray();
        if (existingSet is not null && existingTerms.SequenceEqual(values, StringComparer.Ordinal))
        {
            return;
        }

        if (existingSet is null)
        {
            context.CommonCreatorSets.Add(new CommonCreatorSet { OrganizationId = organizationId });
        }
        context.CommonCreatorTerms.RemoveRange(terms);
        context.CommonCreatorTerms.AddRange(values.Select((value, index) => new CommonCreatorTerm
        {
            OrganizationId = organizationId,
            Value = value,
            SortOrder = (index + 1) * 10
        }));
    }

    private static async Task ReplacePatronCodesAsync(
        AsapDbContext context,
        int organizationId,
        IReadOnlyList<int> values,
        bool resetToSystem,
        CancellationToken cancellationToken)
    {
        var existingSet = await context.PatronCodeEligibilitySets
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var members = await context.PatronCodeEligibilityMembers
            .Where(item => item.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (organizationId != LibraryScope.SystemOrganizationId && resetToSystem)
        {
            if (existingSet is null && members.Count == 0)
            {
                return;
            }
            context.PatronCodeEligibilityMembers.RemoveRange(members);
            if (existingSet is not null)
            {
                context.PatronCodeEligibilitySets.Remove(existingSet);
            }

            return;
        }

        var existingIds = members.Select(item => item.PatronCodeId).ToHashSet();
        if (existingSet is not null && existingIds.Count == values.Count && existingIds.SetEquals(values))
        {
            return;
        }

        if (existingSet is null)
        {
            context.PatronCodeEligibilitySets.Add(new PatronCodeEligibilitySet { OrganizationId = organizationId });
        }
        context.PatronCodeEligibilityMembers.RemoveRange(members);
        context.PatronCodeEligibilityMembers.AddRange(values.Select(value => new PatronCodeEligibilityMember
        {
            OrganizationId = organizationId,
            PatronCodeId = value
        }));
    }

    private static async Task ReplacePublicationOptionsAsync(
        AsapDbContext context,
        int organizationId,
        IReadOnlyList<SetOption> values,
        bool resetToSystem,
        CancellationToken cancellationToken)
    {
        var existingSet = await context.PublicationOptionSets
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var options = await context.PublicationOptions
            .Where(item => item.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (organizationId != LibraryScope.SystemOrganizationId && resetToSystem)
        {
            if (existingSet is null && options.Count == 0)
            {
                return;
            }
            context.PublicationOptions.RemoveRange(options);
            if (existingSet is not null)
            {
                context.PublicationOptionSets.Remove(existingSet);
            }

            return;
        }

        var desiredOptions = values.Select(item => (item.Key, item.Label, item.Enabled, item.SortOrder))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var existingOptions = options.OrderBy(item => item.SortOrder)
            .ThenBy(item => item.OptionKey, StringComparer.Ordinal)
            .Select(item => (item.OptionKey, item.Label, item.IsEnabled, item.SortOrder)).ToArray();
        if (existingSet is not null && existingOptions.SequenceEqual(desiredOptions))
        {
            return;
        }

        if (existingSet is null)
        {
            context.PublicationOptionSets.Add(new PublicationOptionSet { OrganizationId = organizationId });
        }
        context.PublicationOptions.RemoveRange(options);
        context.PublicationOptions.AddRange(values.Select(value => new PublicationOption
        {
            OrganizationId = organizationId,
            OptionKey = value.Key,
            Label = value.Label,
            IsEnabled = value.Enabled,
            SortOrder = value.SortOrder
        }));
    }

    private static async Task ApplyProvidersAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement workflow,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var found = TryGetAny(payload, out var providerValue, "providers", "externalSearchProviders") ||
                    TryGetAny(workflow, out providerValue, "providers", "externalSearchProviders");
        var inputs = found
            ? ParseProviders(providerValue)
            : ParseLegacyProviders(workflow, payload);
        if (inputs.Count == 0)
        {
            return;
        }

        var systemProviders = await context.ExternalSearchProviders
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .ToListAsync(cancellationToken);
        foreach (var input in inputs)
        {
            var provider = input.Id.HasValue
                ? systemProviders.SingleOrDefault(item => item.Id == input.Id.Value)
                : systemProviders.SingleOrDefault(item => item.ProviderKey == input.Key);
            if (provider is null)
            {
                if (organizationId != LibraryScope.SystemOrganizationId)
                {
                    throw new AdministrationInputException("A library may only override an existing external provider.");
                }

                var key = Clean(input.Key);
                var label = Clean(input.Label);
                var url = Clean(input.UrlTemplate);
                if (key is null || label is null || url is null)
                {
                    throw new AdministrationInputException("A system external provider requires a key, label, and URL template.");
                }

                provider = new ExternalSearchProvider
                {
                    OrganizationId = 1,
                    ProviderKey = key,
                    Label = label,
                    UrlTemplate = url,
                    IsEnabled = input.IsEnabled ?? false,
                    SortOrder = input.SortOrder ?? ((systemProviders.Count + 1) * 10)
                };
                context.ExternalSearchProviders.Add(provider);
                systemProviders.Add(provider);
            }

            if (organizationId == LibraryScope.SystemOrganizationId)
            {
                if (input.IsEnabled.HasValue)
                {
                    provider.IsEnabled = input.IsEnabled.Value;
                }

                if (input.Label is not null)
                {
                    provider.Label = RequireText(input.Label, "Provider label");
                }

                if (input.UrlTemplate is not null)
                {
                    provider.UrlTemplate = RequireText(input.UrlTemplate, "Provider URL template");
                }

                if (input.SortOrder.HasValue)
                {
                    provider.SortOrder = input.SortOrder.Value;
                }

                continue;
            }

            var existing = await context.ExternalSearchProviderOverrides
                .SingleOrDefaultAsync(item => item.LibraryOrganizationId == organizationId && item.ExternalSearchProviderId == provider.Id, cancellationToken);
            if (input.Reset || input.Overridden == false)
            {
                if (existing is not null)
                {
                    context.ExternalSearchProviderOverrides.Remove(existing);
                }

                continue;
            }

            var desiredEnabled = input.IsEnabled ?? provider.IsEnabled;
            var desiredLabel = input.Label is null ? provider.Label : Clean(input.Label) ?? provider.Label;
            var desiredUrl = input.UrlTemplate is null ? provider.UrlTemplate : Clean(input.UrlTemplate) ?? provider.UrlTemplate;
            bool? nextEnabled = desiredEnabled == provider.IsEnabled ? null : desiredEnabled;
            var nextLabel = string.Equals(desiredLabel, provider.Label, StringComparison.Ordinal) ? null : desiredLabel;
            var nextUrl = string.Equals(desiredUrl, provider.UrlTemplate, StringComparison.Ordinal) ? null : desiredUrl;
            if (nextEnabled is null && nextLabel is null && nextUrl is null)
            {
                if (existing is not null)
                {
                    context.ExternalSearchProviderOverrides.Remove(existing);
                }

                continue;
            }

            existing ??= new ExternalSearchProviderOverride
            {
                LibraryOrganizationId = organizationId,
                ExternalSearchProviderId = provider.Id
            };
            var overrideRow = existing;
            if (context.Entry(overrideRow).State == EntityState.Detached)
            {
                context.ExternalSearchProviderOverrides.Add(overrideRow);
            }
            overrideRow.IsEnabled = nextEnabled;
            overrideRow.Label = nextLabel;
            overrideRow.UrlTemplate = nextUrl;
        }
    }

    private async Task ApplyFormatsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        JsonElement patron,
        CancellationToken cancellationToken)
    {
        var hasFormats = TryGetAny(payload, out var value, "formats", "materialFormats") ||
                         TryGetAny(patron, out value, "formats", "materialFormats");
        var hasRules = TryGetAny(payload, out var rulesValue, "formatRules", "patronFormatRules") ||
                       TryGetAny(patron, out rulesValue, "formatRules", "patronFormatRules");
        if (!hasFormats && !hasRules &&
            !TryGetAny(patron, out _, "formatLabels", "formatOrder", "availableFormats"))
        {
            return;
        }

        if (hasFormats && value.ValueKind != JsonValueKind.Array)
        {
            throw new AdministrationInputException("formats must be an array.");
        }

        var systemFormats = await context.MaterialFormats
            .Where(item => item.OwnerOrganizationId == LibraryScope.SystemOrganizationId)
            .ToListAsync(cancellationToken);
        var customFormats = organizationId == LibraryScope.SystemOrganizationId
            ? []
            : await context.MaterialFormats
                .Where(item => item.OwnerOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        if (hasFormats)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                await ApplyFormatObjectAsync(context, organizationId, item, systemFormats, customFormats, cancellationToken);
            }
        }
        if (hasRules)
        {
            foreach (var (code, item) in EnumerateFormatRules(rulesValue))
            {
                var target = (organizationId != LibraryScope.SystemOrganizationId ? customFormats.SingleOrDefault(format => format.Code == code) : null) ??
                             systemFormats.SingleOrDefault(format => format.Code == code);
                if (target is null)
                {
                    throw new AdministrationInputException($"The format rule for {code} is outside the selected scope.");
                }

                if (organizationId == LibraryScope.SystemOrganizationId || target.OwnerOrganizationId == organizationId)
                {
                    ApplyOwnedFormat(target, item, allowCode: false);
                }
                else
                {
                    var overrideRow = await context.MaterialFormatOverrides.SingleOrDefaultAsync(
                        row => row.LibraryOrganizationId == organizationId && row.MaterialFormatId == target.Id,
                        cancellationToken);
                    if (overrideRow is null)
                    {
                        overrideRow = new MaterialFormatOverride
                        {
                            LibraryOrganizationId = organizationId,
                            MaterialFormatId = target.Id
                        };
                        context.MaterialFormatOverrides.Add(overrideRow);
                    }
                    ApplyFormatOverride(overrideRow, target, item);
                    if (IsEmpty(overrideRow))
                    {
                        context.MaterialFormatOverrides.Remove(overrideRow);
                    }
                }
            }
        }
        await ApplyLegacyFormatMapsAsync(context, organizationId, patron, systemFormats, customFormats, cancellationToken);
    }

    private static async Task ApplyFormatObjectAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement item,
        IReadOnlyList<MaterialFormat> systemFormats,
        IReadOnlyList<MaterialFormat> customFormats,
        CancellationToken cancellationToken)
    {
        var id = GetLong(item, "id");
        var code = Clean(GetString(item, "code"));
        var owner = GetInt(item, "ownerOrganizationId") ?? (GetBool(item, "custom") == true ? organizationId : 1);
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            var target = (id.HasValue ? systemFormats.SingleOrDefault(format => format.Id == id.Value) : null) ??
                         (code is null ? null : systemFormats.SingleOrDefault(format => format.Code == code));
            if (target is null)
            {
                if (owner != 1)
                {
                    throw new AdministrationInputException("System settings cannot create a library-owned format.");
                }

                target = CreateFormat(item, code, 1);
                context.MaterialFormats.Add(target);
                if (systemFormats is List<MaterialFormat> mutableSystemFormats)
                {
                    mutableSystemFormats.Add(target);
                }
            }
            ApplyOwnedFormat(target, item, allowCode: false);
            if (GetBool(item, "deleted") == true || GetBool(item, "delete") == true)
            {
                target.IsEnabled = false;
            }
            return;
        }

        var custom = id.HasValue
            ? customFormats.SingleOrDefault(format => format.Id == id.Value)
            : code is null ? null : customFormats.SingleOrDefault(format => format.Code == code);
        if (owner == organizationId || GetBool(item, "custom") == true)
        {
            if (custom is null)
            {
                if (code is null || systemFormats.Any(format => format.Code == code))
                {
                    throw new AdministrationInputException("A custom format code must be present and must not collide with a system format.");
                }
                custom = CreateFormat(item, code, organizationId);
                context.MaterialFormats.Add(custom);
                if (customFormats is List<MaterialFormat> mutableCustomFormats)
                {
                    mutableCustomFormats.Add(custom);
                }
            }
            ApplyOwnedFormat(custom, item, allowCode: false);
            return;
        }

        var system = (id.HasValue ? systemFormats.SingleOrDefault(format => format.Id == id.Value) : null) ??
                     (code is null ? null : systemFormats.SingleOrDefault(format => format.Code == code));
        if (system is null)
        {
            throw new AdministrationInputException("The selected system format is not available in this library scope.");
        }

        var existing = await context.MaterialFormatOverrides
            .SingleOrDefaultAsync(itemRow => itemRow.LibraryOrganizationId == organizationId && itemRow.MaterialFormatId == system.Id, cancellationToken);
        if (GetBool(item, "reset") == true || GetBool(item, "useSystemDefault") == true || GetBool(item, "overridden") == false)
        {
            if (existing is not null)
            {
                context.MaterialFormatOverrides.Remove(existing);
            }

            return;
        }
        existing ??= new MaterialFormatOverride
        {
            LibraryOrganizationId = organizationId,
            MaterialFormatId = system.Id
        };
        var wasDetached = context.Entry(existing).State == EntityState.Detached;
        ApplyFormatOverride(existing, system, item);
        if (IsEmpty(existing))
        {
            if (!wasDetached)
            {
                context.MaterialFormatOverrides.Remove(existing);
            }
        }
        else if (wasDetached)
        {
            context.MaterialFormatOverrides.Add(existing);
        }
    }

    private static async Task ApplyLegacyFormatMapsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement patron,
        IReadOnlyList<MaterialFormat> systemFormats,
        IReadOnlyList<MaterialFormat> customFormats,
        CancellationToken cancellationToken)
    {
        var labels = TryGetAny(patron, out var labelValue, "formatLabels") && labelValue.ValueKind == JsonValueKind.Object
            ? labelValue
            : default;
        var order = TryGetAny(patron, out var orderValue, "formatOrder") ? ParseValues(orderValue) : [];
        var available = TryGetAny(patron, out var availableValue, "availableFormats")
            ? ParseValues(availableValue).ToHashSet(StringComparer.Ordinal)
            : null;
        if (labels.ValueKind != JsonValueKind.Object && order.Count == 0 && available is null)
        {
            return;
        }

        var formats = systemFormats.Concat(customFormats).ToArray();
        foreach (var format in formats)
        {
            var itemLabel = labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(format.Code, out var label)
                ? label.GetString()
                : null;
            var position = order.Select((value, index) => (value, index))
                .Where(item => string.Equals(item.value, format.Code, StringComparison.Ordinal))
                .Select(item => (int?)item.index)
                .FirstOrDefault();
            int? itemOrder = position.HasValue ? (position.Value + 1) * 10 : null;
            var itemEnabled = available?.Contains(format.Code);
            if (organizationId == LibraryScope.SystemOrganizationId || format.OwnerOrganizationId == organizationId)
            {
                if (itemLabel is not null)
                {
                    format.Label = RequireText(itemLabel, "Format label");
                }

                if (itemOrder.HasValue)
                {
                    format.SortOrder = itemOrder.Value;
                }

                if (itemEnabled.HasValue)
                {
                    format.IsEnabled = itemEnabled.Value;
                }
            }
            else
            {
                var row = await context.MaterialFormatOverrides.SingleOrDefaultAsync(
                    item => item.LibraryOrganizationId == organizationId && item.MaterialFormatId == format.Id,
                    cancellationToken);
                row ??= new MaterialFormatOverride { LibraryOrganizationId = organizationId, MaterialFormatId = format.Id };
                if (itemLabel is not null)
                {
                    row.Label = Same(itemLabel, format.Label) ? null : Clean(itemLabel);
                }

                if (itemOrder.HasValue)
                {
                    row.SortOrder = itemOrder.Value == format.SortOrder ? null : itemOrder.Value;
                }

                if (itemEnabled.HasValue)
                {
                    row.IsEnabled = itemEnabled.Value == format.IsEnabled ? null : itemEnabled.Value;
                }

                if (IsEmpty(row))
                {
                    if (context.Entry(row).State != EntityState.Detached)
                    {
                        context.MaterialFormatOverrides.Remove(row);
                    }
                }
                else if (context.Entry(row).State == EntityState.Detached)
                {
                    context.MaterialFormatOverrides.Add(row);
                }
            }
        }
    }

    private static async Task ApplyCustomFieldsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement patron,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            return;
        }

        var hasDefinitions = TryGetAny(payload, out var definitions, "customFields", "additionalFieldDefinitions") ||
                             TryGetAny(patron, out definitions, "customFields", "additionalFieldDefinitions");
        if (hasDefinitions)
        {
            if (definitions.ValueKind != JsonValueKind.Array)
            {
                throw new AdministrationInputException("customFields must be an array.");
            }

            var existing = await context.PatronCustomFields
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
            var keep = new HashSet<long>();
            foreach (var item in definitions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetLong(item, "id");
                var key = Clean(GetString(item, "key") ?? GetString(item, "fieldKey"));
                if (key is null)
                {
                    throw new AdministrationInputException("Each custom field requires a stable key.");
                }

                var field = (id.HasValue ? existing.SingleOrDefault(row => row.Id == id.Value) : null) ??
                            existing.SingleOrDefault(row => row.FieldKey == key);
                var isNewField = field is null;
                field ??= new PatronCustomField
                {
                    LibraryOrganizationId = organizationId,
                    FieldKey = key,
                    FieldType = "text",
                    Label = key,
                    IsEnabled = true,
                    SortOrder = (existing.Count + 1) * 10
                };
                if (isNewField)
                {
                    context.PatronCustomFields.Add(field);
                    // Options reference the generated field key, so materialize the field
                    // before replacing its option set inside this already-open transaction.
                    await context.SaveChangesAsync(cancellationToken);
                }
                keep.Add(field.Id);
                field.FieldKey = key;
                field.FieldType = NormalizeFieldType(GetString(item, "type") ?? GetString(item, "fieldType") ?? field.FieldType);
                field.Label = RequireText(GetString(item, "label") ?? field.Label, "Custom field label");
                if (HasProperty(item, "helpText"))
                {
                    field.HelpText = Clean(GetString(item, "helpText"));
                }

                if (GetBool(item, "enabled").HasValue)
                {
                    field.IsEnabled = GetBool(item, "enabled")!.Value;
                }

                if (GetInt(item, "sortOrder").HasValue)
                {
                    field.SortOrder = GetInt(item, "sortOrder")!.Value;
                }

                if (TryGetAny(item, out var options, "options") && options.ValueKind == JsonValueKind.Array)
                {
                    var oldOptions = await context.PatronCustomFieldOptions
                        .Where(row => row.PatronCustomFieldId == field.Id)
                        .ToListAsync(cancellationToken);
                    var desiredOptions = ParseOptions(options)
                        .OrderBy(option => option.SortOrder)
                        .ThenBy(option => option.Key, StringComparer.Ordinal)
                        .ToArray();
                    var existingOptions = oldOptions
                        .OrderBy(option => option.SortOrder)
                        .ThenBy(option => option.OptionKey, StringComparer.Ordinal)
                        .Select(option => (option.OptionKey, option.Label, option.IsEnabled, option.SortOrder))
                        .ToArray();
                    if (!existingOptions.SequenceEqual(desiredOptions.Select(option =>
                        (option.Key, option.Label, option.Enabled, option.SortOrder))))
                    {
                        context.PatronCustomFieldOptions.RemoveRange(oldOptions);
                        context.PatronCustomFieldOptions.AddRange(desiredOptions.Select(option => new PatronCustomFieldOption
                        {
                            PatronCustomFieldId = field.Id,
                            OptionKey = option.Key,
                            Label = option.Label,
                            IsEnabled = option.Enabled,
                            SortOrder = option.SortOrder
                        }));
                    }
                }
            }

            var removed = existing.Where(item => item.Id != 0 && !keep.Contains(item.Id)).ToArray();
            if (removed.Length > 0)
            {
                var removedIds = removed.Select(item => item.Id).ToArray();
                context.MaterialFormatCustomFieldRules.RemoveRange(
                    await context.MaterialFormatCustomFieldRules
                        .Where(item => item.LibraryOrganizationId == organizationId && removedIds.Contains(item.PatronCustomFieldId))
                        .ToListAsync(cancellationToken));
                context.PatronCustomFieldOptions.RemoveRange(
                    await context.PatronCustomFieldOptions.Where(item => removedIds.Contains(item.PatronCustomFieldId)).ToListAsync(cancellationToken));
                context.PatronCustomFields.RemoveRange(removed);
            }
        }

        var hasRules = TryGetAny(payload, out var rules, "formatRules", "patronFormatRules") ||
                       TryGetAny(patron, out rules, "formatRules", "patronFormatRules");
        if (!hasRules && (TryGetAny(payload, out var formats, "formats", "materialFormats") ||
                          TryGetAny(patron, out formats, "formats", "materialFormats")) && formats.ValueKind == JsonValueKind.Array)
        {
            hasRules = formats.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && HasProperty(item, "customFields"));
            if (hasRules)
            {
                rules = formats;
            }
        }
        if (!hasRules)
        {
            return;
        }

        var currentRules = await context.MaterialFormatCustomFieldRules
            .Where(item => item.LibraryOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var fieldsByKey = await context.PatronCustomFields
            .Where(item => item.LibraryOrganizationId == organizationId)
            .ToDictionaryAsync(item => item.FieldKey, StringComparer.Ordinal, cancellationToken);
        var formatsInScope = await context.MaterialFormats
            .Where(item => item.OwnerOrganizationId == LibraryScope.SystemOrganizationId || item.OwnerOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var desiredRules = new List<MaterialFormatCustomFieldRule>();
        foreach (var (formatCode, formatRule) in EnumerateFormatRules(rules))
        {
            var format = formatsInScope.SingleOrDefault(item => item.Code == formatCode);
            if (format is null)
            {
                throw new AdministrationInputException($"Format rule {formatCode} is outside the selected library scope.");
            }

            if (!TryGetAny(formatRule, out var custom, "customFields") || custom.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var fieldProperty in custom.EnumerateObject())
            {
                if (!fieldsByKey.TryGetValue(fieldProperty.Name, out var field))
                {
                    continue;
                }

                var rule = fieldProperty.Value;
                var mode = GetString(rule, "mode") ?? "hidden";
                if (mode is not ("required" or "optional" or "hidden"))
                {
                    throw new AdministrationInputException("Custom field mode must be required, optional, or hidden.");
                }
                if (mode == "hidden")
                {
                    continue;
                }

                desiredRules.Add(new MaterialFormatCustomFieldRule
                {
                    LibraryOrganizationId = organizationId,
                    MaterialFormatId = format.Id,
                    PatronCustomFieldId = field.Id,
                    Mode = mode,
                    LabelOverride = Clean(GetString(rule, "labelOverride") ?? GetString(rule, "label"))
                });
            }
        }
        var existingRuleValues = currentRules
            .Select(item => (item.MaterialFormatId, item.PatronCustomFieldId, item.Mode, item.LabelOverride))
            .OrderBy(item => item.MaterialFormatId)
            .ThenBy(item => item.PatronCustomFieldId)
            .ToArray();
        var desiredRuleValues = desiredRules
            .Select(item => (item.MaterialFormatId, item.PatronCustomFieldId, item.Mode, item.LabelOverride))
            .OrderBy(item => item.MaterialFormatId)
            .ThenBy(item => item.PatronCustomFieldId)
            .ToArray();
        if (!existingRuleValues.SequenceEqual(desiredRuleValues))
        {
            context.MaterialFormatCustomFieldRules.RemoveRange(currentRules);
            context.MaterialFormatCustomFieldRules.AddRange(desiredRules);
        }
    }

    private async Task ApplyAutoClaimRulesAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (organizationId == LibraryScope.SystemOrganizationId ||
            !TryGetAny(payload, out var value, "autoClaimRules", "formatClaimRules"))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new AdministrationInputException("autoClaimRules must be an array.");
        }

        var formats = await context.MaterialFormats
            .Where(item => item.OwnerOrganizationId == LibraryScope.SystemOrganizationId || item.OwnerOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var active = await context.FormatAutoClaimRules
            .Where(item => item.LibraryOrganizationId == organizationId && item.IsActive)
            .ToListAsync(cancellationToken);
        var desired = new Dictionary<long, long?>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var formatId = GetLong(item, "materialFormatId") ?? GetLong(item, "formatId");
            if (!formatId.HasValue || formats.All(format => format.Id != formatId.Value))
            {
                throw new AdministrationInputException("Each auto-claim rule must reference a format in the selected library scope.");
            }
            if (GetBool(item, "active") == false || GetBool(item, "isActive") == false)
            {
                desired.Remove(formatId.Value);
                continue;
            }
            var staffId = GetLong(item, "staffUserId") ?? GetLong(item, "staffId");
            if (!staffId.HasValue)
            {
                throw new AdministrationInputException("An active auto-claim rule requires a staff user.");
            }

            if (desired.ContainsKey(formatId.Value))
            {
                throw new AdministrationInputException("Only one active auto-claim rule is allowed per format.");
            }

            var staff = context.StaffUsers.Local.SingleOrDefault(itemRow => itemRow.Id == staffId.Value);
            if (staff is null || !StaffEligibilityService.IsAssignmentEligible(staff, organizationId))
            {
                throw new AdministrationInputException("The auto-claim staff user is not active or is outside the selected scope.");
            }
            desired[formatId.Value] = staffId.Value;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var rule in active)
        {
            if (!desired.TryGetValue(rule.MaterialFormatId, out var staffId) || staffId != rule.StaffUserId)
            {
                rule.IsActive = false;
                rule.DeactivatedUtc = now;
            }
        }
        foreach (var pair in desired)
        {
            if (active.Any(rule => rule.MaterialFormatId == pair.Key && rule.IsActive && rule.StaffUserId == pair.Value))
            {
                continue;
            }

            context.FormatAutoClaimRules.Add(new FormatAutoClaimRule
            {
                LibraryOrganizationId = organizationId,
                MaterialFormatId = pair.Key,
                StaffUserId = pair.Value,
                IsActive = true,
                CreatedUtc = now
            });
        }
    }

    private static async Task ApplyTemplatesAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (TryGetAny(payload, out var templates, "templates", "emailTemplates"))
        {
            if (templates.ValueKind != JsonValueKind.Array)
            {
                throw new AdministrationInputException("templates must be an array.");
            }

            foreach (var item in templates.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    await ApplyTemplateObjectAsync(context, organizationId, item, null, cancellationToken);
                }
            }
            return;
        }

        if (!TryGetAny(payload, out var emails, "emails", "email"))
        {
            return;
        }

        if (emails.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in emails.EnumerateObject())
        {
            if (property.Name is "fromAddress" or "fromName" or "postmarkToken" or "serverToken" or "rejection_templates")
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                await ApplyTemplateObjectAsync(context, organizationId, property.Value, property.Name, cancellationToken);
            }
        }
        if (emails.TryGetProperty("rejection_templates", out var rejectionTemplates) && rejectionTemplates.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rejectionTemplates.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    await ApplyTemplateObjectAsync(context, organizationId, item, null, cancellationToken);
                }
            }
        }
    }

    private static async Task ApplyTemplateObjectAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement item,
        string? legacyKey,
        CancellationToken cancellationToken)
    {
        var itemOrganization = GetInt(item, "organizationId");
        if (itemOrganization.HasValue && itemOrganization.Value != organizationId && organizationId != LibraryScope.SystemOrganizationId)
        {
            return;
        }

        var key = Clean(GetString(item, "templateKey") ?? GetString(item, "key") ?? legacyKey);
        var sourceId = GetLong(item, "sourceTemplateId") ?? GetLong(item, "sourceId");
        var reset = GetBool(item, "reset") == true || GetBool(item, "useSystemDefault") == true || GetBool(item, "overridden") == false;
        var isCustom = GetBool(item, "isCustom") == true || GetBool(item, "custom") == true;
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            if (sourceId.HasValue || isCustom)
            {
                throw new AdministrationInputException("System templates cannot use library lineage or custom ownership.");
            }

            if (key is null)
            {
                throw new AdministrationInputException("A system template requires a template key.");
            }

            var template = await context.EmailTemplates.SingleOrDefaultAsync(
                row => row.OrganizationId == LibraryScope.SystemOrganizationId && row.TemplateKey == key, cancellationToken);
            template ??= new EmailTemplate
            {
                OrganizationId = 1,
                TemplateKey = key,
                IsCustom = false,
                IsHidden = false
            };
            if (template.Id == 0)
            {
                context.EmailTemplates.Add(template);
            }

            ApplyTemplateContent(template, item, requireContent: true);
            return;
        }

        if (isCustom || (!sourceId.HasValue && key is not null && GetBool(item, "libraryCustom") == true))
        {
            if (key is null)
            {
                throw new AdministrationInputException("A custom template requires a template key.");
            }

            var custom = await context.EmailTemplates.SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId && row.IsCustom && row.TemplateKey == key, cancellationToken);
            if (reset)
            {
                if (custom is not null)
                {
                    context.EmailTemplates.Remove(custom);
                }

                return;
            }
            custom ??= new EmailTemplate
            {
                OrganizationId = organizationId,
                TemplateKey = key,
                IsCustom = true,
                IsHidden = false
            };
            if (custom.Id == 0)
            {
                context.EmailTemplates.Add(custom);
            }

            ApplyTemplateContent(custom, item, requireContent: true);
            return;
        }

        EmailTemplate? source = sourceId.HasValue
            ? await context.EmailTemplates.SingleOrDefaultAsync(row => row.Id == sourceId.Value && row.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken)
            : key is null
                ? null
                : await context.EmailTemplates.SingleOrDefaultAsync(row => row.OrganizationId == LibraryScope.SystemOrganizationId && row.TemplateKey == key, cancellationToken);
        if (source is null)
        {
            throw new AdministrationInputException("A library template override must reference a system template.");
        }

        var overrideRow = await context.EmailTemplates.SingleOrDefaultAsync(
            row => row.OrganizationId == organizationId && row.SourceTemplateId == source.Id, cancellationToken);
        if (reset)
        {
            if (overrideRow is not null)
            {
                context.EmailTemplates.Remove(overrideRow);
            }

            return;
        }
        overrideRow ??= new EmailTemplate
        {
            OrganizationId = organizationId,
            TemplateKey = source.TemplateKey,
            SourceTemplateId = source.Id,
            IsCustom = false,
            IsHidden = false
        };
        if (overrideRow.Id == 0)
        {
            context.EmailTemplates.Add(overrideRow);
        }

        ApplyTemplateContent(overrideRow, item, requireContent: false);
    }

    private static void ApplyTemplateContent(EmailTemplate template, JsonElement item, bool requireContent)
    {
        if (HasProperty(item, "subject"))
        {
            template.SubjectTemplate = GetString(item, "subject");
        }
        else if (HasProperty(item, "subjectTemplate"))
        {
            template.SubjectTemplate = GetString(item, "subjectTemplate");
        }

        if (HasProperty(item, "body"))
        {
            template.BodyTemplate = GetString(item, "body");
        }
        else if (HasProperty(item, "bodyTemplate"))
        {
            template.BodyTemplate = GetString(item, "bodyTemplate");
        }

        if (HasProperty(item, "displayName"))
        {
            template.DisplayName = Clean(GetString(item, "displayName"));
        }

        if (GetBool(item, "enabled").HasValue)
        {
            template.IsHidden = !GetBool(item, "enabled")!.Value;
        }

        if (GetBool(item, "hidden").HasValue)
        {
            template.IsHidden = GetBool(item, "hidden")!.Value;
        }

        if (GetBool(item, "isHidden").HasValue)
        {
            template.IsHidden = GetBool(item, "isHidden")!.Value;
        }

        if (GetInt(item, "sortOrder").HasValue)
        {
            template.SortOrder = GetInt(item, "sortOrder")!.Value;
        }

        if (requireContent && (string.IsNullOrWhiteSpace(template.SubjectTemplate) || string.IsNullOrWhiteSpace(template.BodyTemplate)))
        {
            throw new AdministrationInputException("A system or custom email template requires both subject and body content.");
        }
    }

    private async Task ApplyBrandingAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement patron,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var found = TryGetAny(payload, out var branding, "branding") || TryGetAny(patron, out branding, "branding");
        if (!found)
        {
            if (!HasProperty(patron, "logoAlt") && !HasProperty(patron, "logoAltText"))
            {
                return;
            }

            branding = patron;
        }

        var hasLogoData = HasProperty(branding, "logoData");
        var hasAlt = HasProperty(branding, "altText") || HasProperty(branding, "logoAlt") || HasProperty(branding, "logoAltText");
        var clearLogo = GetBool(branding, "clearLogo") == true || GetBool(branding, "removeLogo") == true;
        if (!hasLogoData && !hasAlt && !clearLogo)
        {
            return;
        }

        var row = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var isNew = row is null;
        row ??= new Branding { OrganizationId = organizationId };
        if (hasAlt)
        {
            row.LogoAltText = Clean(GetString(branding, "altText") ?? GetString(branding, "logoAlt") ?? GetString(branding, "logoAltText"));
        }
        if (clearLogo)
        {
            row.LogoData = null;
            row.LogoContentType = null;
            row.LogoFileName = null;
        }
        if (hasLogoData && !clearLogo)
        {
            var encoded = Clean(GetString(branding, "logoData"));
            if (encoded is null)
            {
                throw new AdministrationInputException("logoData must contain a base64 encoded PNG, JPEG, or GIF image.");
            }

            byte[] data;
            try { data = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new AdministrationInputException("logoData must be valid base64."); }
            var contentType = Clean(GetString(branding, "contentType") ?? GetString(branding, "logoContentType"));
            if (!LogoImageValidator.TryValidate(data, contentType, out var logoInfo, out var logoError))
            {
                throw new AdministrationInputException(logoError);
            }
            row.LogoData = data;
            row.LogoContentType = logoInfo!.ContentType;
            row.LogoFileName = Clean(GetString(branding, "fileName") ?? GetString(branding, "logoFileName")) ?? "logo";
        }
        row.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (isNew && !IsEmpty(row))
        {
            context.Branding.Add(row);
        }
    }

    private static async Task RemoveEmptyOverrideRowsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var workflows = await context.WorkflowSettings.Where(item => item.OrganizationId == organizationId).ToListAsync(cancellationToken);
        context.WorkflowSettings.RemoveRange(workflows.Where(IsEmpty));
        var patrons = await context.PatronSettings.Where(item => item.OrganizationId == organizationId).ToListAsync(cancellationToken);
        context.PatronSettings.RemoveRange(patrons.Where(IsEmpty));
        var emails = await context.EmailSettings.Where(item => item.OrganizationId == organizationId).ToListAsync(cancellationToken);
        context.EmailSettings.RemoveRange(emails.Where(IsEmpty));
        var providers = await context.ExternalSearchProviderOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken);
        context.ExternalSearchProviderOverrides.RemoveRange(providers.Where(IsEmpty));
        var formats = await context.MaterialFormatOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken);
        context.MaterialFormatOverrides.RemoveRange(formats.Where(IsEmpty));
        var branding = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (branding is not null && IsEmpty(branding))
        {
            context.Branding.Remove(branding);
        }
    }

    private static bool IsEmpty(WorkflowSettings value) =>
        value.SuggestionLimit is null && value.SuggestionLimitMessage is null &&
        value.OutstandingTimeoutEnabled is null && value.OutstandingTimeoutDays is null && value.OutstandingTimeoutSendEmail is null &&
        value.OutstandingTimeoutRejectionTemplateId is null && value.HoldPickupTimeoutEnabled is null && value.HoldPickupTimeoutDays is null &&
        value.PendingHoldTimeoutEnabled is null && value.PendingHoldTimeoutDays is null && value.AdditionalCopyTimeoutEnabled is null &&
        value.AdditionalCopyTimeoutDays is null && value.AutoPromote is null && value.CommonAuthorsEnabled is null &&
        value.CommonAuthorsLabel is null && value.CommonAuthorsHelp is null && value.CommonAuthorsMessage is null &&
        value.AllowPatronAutoholdOptOut is null && value.AllowAnyRegisteredCardLogin is null &&
        value.PatronCodeEligibilityEnabled is null && value.PatronCodeEligibilityMessage is null;

    private static bool IsEmpty(PatronSettings value) =>
        PatronTextValues(value).All(item => item is null);

    private static IEnumerable<string?> PatronTextValues(PatronSettings value) =>
    [
        value.PageTitle, value.BarcodeLabel, value.PinLabel, value.LoginPrompt, value.LoginNote,
        value.SuggestionFormNote, value.NoEmailMessage, value.SuccessTitle, value.SuccessMessage,
        value.AlreadySubmittedMessage, value.EbookMessage, value.EaudiobookMessage,
        value.SuggestionStatusLabel, value.OutstandingPurchaseStatusLabel, value.PendingHoldStatusLabel,
        value.HoldPlacedStatusLabel, value.ClosedStatusLabel, value.RejectedStatusLabel,
        value.HoldCompletedStatusLabel, value.HoldNotPickedUpStatusLabel, value.ManualStatusLabel,
        value.SilentStatusLabel
    ];

    private static bool IsEmpty(EmailSettings value) =>
        value.ProtectedServerToken is null && value.FromAddress is null && value.FromName is null;

    private static bool IsEmpty(ExternalSearchProviderOverride value) =>
        value.IsEnabled is null && value.Label is null && value.UrlTemplate is null;

    private static bool IsEmpty(MaterialFormatOverride value) =>
        value.Label is null && value.SortOrder is null && value.IsEnabled is null && value.MessageBehavior is null &&
        value.Message is null && value.TitleMode is null && value.TitleLabel is null && value.AuthorMode is null &&
        value.AuthorLabel is null && value.IdentifierMode is null && value.IdentifierLabel is null &&
        value.PublicationMode is null && value.PublicationLabel is null;

    private static bool IsEmpty(Branding value) =>
        value.LogoData is null && value.LogoContentType is null && value.LogoFileName is null && value.LogoAltText is null;

    private static void ApplyOwnedFormat(MaterialFormat row, JsonElement item, bool allowCode)
    {
        if (allowCode && HasProperty(item, "code"))
        {
            row.Code = RequireText(GetString(item, "code"), "Format code");
        }

        if (HasProperty(item, "label"))
        {
            row.Label = RequireText(GetString(item, "label"), "Format label");
        }

        if (GetInt(item, "sortOrder").HasValue)
        {
            row.SortOrder = GetInt(item, "sortOrder")!.Value;
        }

        var enabled = GetBool(item, "isEnabled") ?? GetBool(item, "enabled");
        if (enabled.HasValue)
        {
            row.IsEnabled = enabled.Value;
        }

        if (GetBool(item, "deleted") == true || GetBool(item, "delete") == true)
        {
            row.IsEnabled = false;
        }

        if (HasProperty(item, "messageBehavior"))
        {
            row.MessageBehavior = NormalizeMessageBehavior(GetString(item, "messageBehavior"));
        }

        if (HasProperty(item, "message"))
        {
            row.Message = Clean(GetString(item, "message"));
        }

        ApplyOwnedField(item, "title", value => row.TitleMode = value.Mode, value => row.TitleLabel = value.Label, row.TitleMode ?? "required", row.TitleLabel ?? "Title", forceRequired: true);
        ApplyOwnedField(item, "author", value => row.AuthorMode = value.Mode, value => row.AuthorLabel = value.Label, row.AuthorMode ?? "optional", row.AuthorLabel ?? "Author", forceRequired: false);
        ApplyOwnedField(item, "identifier", value => row.IdentifierMode = value.Mode, value => row.IdentifierLabel = value.Label, row.IdentifierMode ?? "optional", row.IdentifierLabel ?? "Identifier number", forceRequired: false);
        ApplyOwnedField(item, "publication", value => row.PublicationMode = value.Mode, value => row.PublicationLabel = value.Label, row.PublicationMode ?? "optional", row.PublicationLabel ?? "Publication Timing", forceRequired: false);
    }

    private static MaterialFormat CreateFormat(JsonElement item, string? code, int ownerOrganizationId)
    {
        var normalizedCode = RequireText(code, "Format code");
        var row = new MaterialFormat
        {
            OwnerOrganizationId = ownerOrganizationId,
            Code = normalizedCode,
            Label = Clean(GetString(item, "label")) ?? normalizedCode,
            SortOrder = GetInt(item, "sortOrder") ?? 10,
            IsEnabled = GetBool(item, "isEnabled") ?? GetBool(item, "enabled") ?? true,
            MessageBehavior = "none",
            TitleMode = "required",
            TitleLabel = "Title",
            AuthorMode = "optional",
            AuthorLabel = "Author",
            IdentifierMode = "optional",
            IdentifierLabel = "Identifier number",
            PublicationMode = "optional",
            PublicationLabel = "Publication Timing"
        };
        ApplyOwnedFormat(row, item, allowCode: false);
        return row;
    }

    private static void ApplyFormatOverride(MaterialFormatOverride row, MaterialFormat baseline, JsonElement item)
    {
        if (HasProperty(item, "label"))
        {
            row.Label = Same(GetString(item, "label"), baseline.Label) ? null : Clean(GetString(item, "label"));
        }

        if (GetInt(item, "sortOrder").HasValue)
        {
            row.SortOrder = GetInt(item, "sortOrder") == baseline.SortOrder ? null : GetInt(item, "sortOrder");
        }

        var enabled = GetBool(item, "isEnabled") ?? GetBool(item, "enabled");
        if (enabled.HasValue)
        {
            row.IsEnabled = enabled.Value == baseline.IsEnabled ? null : enabled.Value;
        }

        if (HasProperty(item, "messageBehavior"))
        {
            var value = NormalizeMessageBehavior(GetString(item, "messageBehavior"));
            row.MessageBehavior = Same(value, baseline.MessageBehavior ?? "none") ? null : value;
        }
        if (HasProperty(item, "message"))
        {
            var value = Clean(GetString(item, "message"));
            row.Message = Same(value, Clean(baseline.Message)) ? null : value;
        }
        ApplyOverrideField(item, "title", baseline.TitleMode ?? "required", baseline.TitleLabel ?? "Title", value => row.TitleMode = value.Mode, value => row.TitleLabel = value.Label);
        ApplyOverrideField(item, "author", baseline.AuthorMode ?? "optional", baseline.AuthorLabel ?? "Author", value => row.AuthorMode = value.Mode, value => row.AuthorLabel = value.Label);
        ApplyOverrideField(item, "identifier", baseline.IdentifierMode ?? "optional", baseline.IdentifierLabel ?? "Identifier number", value => row.IdentifierMode = value.Mode, value => row.IdentifierLabel = value.Label);
        ApplyOverrideField(item, "publication", baseline.PublicationMode ?? "optional", baseline.PublicationLabel ?? "Publication Timing", value => row.PublicationMode = value.Mode, value => row.PublicationLabel = value.Label);
    }

    private static void ApplyOwnedField(
        JsonElement item,
        string name,
        Action<(string Mode, string Label)> modeSetter,
        Action<(string Mode, string Label)> labelSetter,
        string defaultMode,
        string defaultLabel,
        bool forceRequired)
    {
        if (!TryGetAny(item, out var field, name) || field.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var mode = NormalizeFieldMode(GetString(field, "mode") ?? defaultMode, forceRequired);
        var label = Clean(GetString(field, "label")) ?? defaultLabel;
        var value = (Mode: mode, Label: label);
        modeSetter(value);
        labelSetter(value);
    }

    private static void ApplyOverrideField(
        JsonElement item,
        string name,
        string baselineMode,
        string baselineLabel,
        Action<(string Mode, string Label)> modeSetter,
        Action<(string Mode, string Label)> labelSetter)
    {
        if (!TryGetAny(item, out var field, name) || field.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var mode = NormalizeFieldMode(GetString(field, "mode") ?? baselineMode, name == "title");
        var label = Clean(GetString(field, "label")) ?? baselineLabel;
        modeSetter((Same(mode, baselineMode) ? null! : mode, Same(label, baselineLabel) ? null! : label));
        labelSetter((Same(mode, baselineMode) ? null! : mode, Same(label, baselineLabel) ? null! : label));
    }

    private static string NormalizeMessageBehavior(string? value)
    {
        var normalized = Clean(value) ?? "none";
        return normalized is "none" or "message" or "ebookMessage" or "eaudiobookMessage"
            ? normalized
            : throw new AdministrationInputException("Message behavior is invalid.");
    }

    private static string NormalizeFieldMode(string value, bool forceRequired)
    {
        var normalized = Clean(value) ?? "optional";
        if (forceRequired)
        {
            return "required";
        }

        return normalized is "required" or "optional" or "hidden"
            ? normalized
            : throw new AdministrationInputException("Format field mode is invalid.");
    }

    private static string NormalizeFieldType(string value) =>
        value is "text" or "textarea" or "select"
            ? value
            : throw new AdministrationInputException("Custom field type is invalid.");

    private static IReadOnlyList<ProviderInput> ParseProviders(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new ProviderInput(
                Clean(GetString(item, "key") ?? GetString(item, "providerKey")),
                GetLong(item, "id"),
                GetBool(item, "isEnabled") ?? GetBool(item, "enabled"),
                GetString(item, "label"),
                GetString(item, "urlTemplate") ?? GetString(item, "url"),
                GetBool(item, "reset") == true || GetBool(item, "useSystemDefault") == true,
                GetBool(item, "overridden"),
                GetInt(item, "sortOrder")))
            .ToArray();
    }

    private static IReadOnlyList<ProviderInput> ParseLegacyProviders(JsonElement workflow, JsonElement payload)
    {
        var result = new List<ProviderInput>();
        for (var index = 1; index <= 4; index++)
        {
            var enabled = GetBool(workflow, $"externalSearch{index}Enabled") ?? GetBool(payload, $"externalSearch{index}Enabled");
            var label = GetString(workflow, $"externalSearch{index}Label") ?? GetString(payload, $"externalSearch{index}Label");
            var url = GetString(workflow, $"externalSearch{index}UrlTemplate") ?? GetString(payload, $"externalSearch{index}UrlTemplate");
            if (!enabled.HasValue && label is null && url is null)
            {
                continue;
            }

            result.Add(new ProviderInput($"external_search_{index}", null, enabled, label, url, false, null, index * 10));
        }
        return result;
    }

    private static IReadOnlyList<SetOption> ParseOptions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(text);
                return ParseOptions(document.RootElement);
            }
            return ParseValues(value).Select((label, index) => new SetOption(OptionKey(label, index), label, true, (index + 1) * 10)).ToArray();
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SetOption>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var label = item.ValueKind == JsonValueKind.String
                ? Clean(item.GetString())
                : Clean(GetString(item, "label") ?? GetString(item, "value") ?? GetString(item, "name"));
            if (label is null)
            {
                continue;
            }

            var key = Clean(GetString(item, "id") ?? GetString(item, "key")) ?? OptionKey(label, index);
            if (!seen.Add(key))
            {
                throw new AdministrationInputException("Set option IDs must be unique.");
            }

            result.Add(new SetOption(key, label, GetBool(item, "enabled") ?? true, GetInt(item, "sortOrder") ?? ((index + 1) * 10)));
            index++;
        }
        return result;
    }

    private static bool TryParsePatronCodeIds(JsonElement value, out IReadOnlyList<int> ids)
    {
        ids = [];
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        IEnumerable<string> source;
        if (value.ValueKind == JsonValueKind.String)
        {
            source = (value.GetString() ?? string.Empty).Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.EnumerateArray().Any(item => item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)))
            {
                return false;
            }
            source = value.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText());
        }
        else
        {
            return false;
        }
        var result = new HashSet<int>();
        foreach (var item in source)
        {
            if (!int.TryParse(item, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return false;
            }
            result.Add(id);
        }
        ids = result.Order().ToArray();
        return true;
    }

    private static IReadOnlyList<string> ParseValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Clean)
                .Where(item => item is not null)
                .Select(item => item!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? Clean(item.GetString())
                : Clean(GetString(item, "id") ?? GetString(item, "key") ?? GetString(item, "value") ?? GetString(item, "name")))
            .Where(item => item is not null)
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<(string Code, JsonElement Rule)> EnumerateFormatRules(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                yield return (property.Name, property.Value);
            }

            yield break;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in value.EnumerateArray())
        {
            var code = Clean(GetString(item, "code") ?? GetString(item, "format"));
            if (code is not null)
            {
                yield return (code, item);
            }
        }
    }

    private static string NormalizeOrigin(string value)
    {
        var normalized = Clean(value) ?? throw new AdministrationInputException("Embed origins cannot be blank.");
        if (normalized.StartsWith("https://*.", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains('/', StringComparison.Ordinal) || normalized.Contains('?', StringComparison.Ordinal) || normalized.Contains('#', StringComparison.Ordinal))
            {
                throw new AdministrationInputException("Wildcard embed origins may not include a path or query.");
            }
            return "https://*." + normalized[10..].ToLowerInvariant();
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme == "http" && uri.Host is not ("localhost" or "127.0.0.1" or "[::1]")))
        {
            throw new AdministrationInputException("Embed origins must be HTTPS origins, with localhost allowed for HTTP development origins.");
        }
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}";
    }

    private static string OptionKey(string label, int index) =>
        new string(label.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray()).Trim('_') switch
        {
            { Length: > 0 } value => value,
            _ => $"option_{index + 1}"
        };

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static string RequireText(string? value, string label) =>
        Clean(value) ?? throw new AdministrationInputException($"{label} cannot be blank.");

    private sealed record SetOption(string Key, string Label, bool Enabled, int SortOrder);

    private sealed record ProviderInput(
        string? Key,
        long? Id,
        bool? IsEnabled,
        string? Label,
        string? UrlTemplate,
        bool Reset,
        bool? Overridden,
        int? SortOrder);

    private sealed record EffectiveRejectionTemplate(
        long ReferenceId,
        long SourceTemplateId,
        string TemplateKey,
        bool IsHidden,
        string? Subject,
        string? Body);

    private static async Task<long?> ResolveTemplateReferenceAsync(
        AsapDbContext context,
        string? value,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var normalized = Clean(value);
        if (normalized is null)
        {
            return null;
        }

        var rows = await LoadTemplateRowsIncludingPendingAsync(context, cancellationToken);
        EffectiveRejectionTemplate? template;
        if (long.TryParse(normalized, out var id))
        {
            template = ResolveEffectiveTemplate(rows, id, organizationId);
        }
        else
        {
            var forceSystem = normalized.StartsWith("system:", StringComparison.OrdinalIgnoreCase);
            var key = forceSystem ? normalized[7..] : normalized;
            if (!forceSystem && organizationId != LibraryScope.SystemOrganizationId)
            {
                var custom = rows.Where(item => item.OrganizationId == organizationId &&
                        item.IsCustom && string.Equals(item.TemplateKey, key, StringComparison.Ordinal))
                    .ToArray();
                if (custom.Length > 1)
                {
                    throw new AdministrationInputException("The timeout rejection template reference is ambiguous in this library scope.");
                }
                template = custom.Length == 1
                    ? ResolveEffectiveTemplate(rows, custom[0].Id, organizationId)
                    : null;
                if (template is null)
                {
                    var system = rows.Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId &&
                            string.Equals(item.TemplateKey, key, StringComparison.Ordinal))
                        .ToArray();
                    if (system.Length > 1)
                    {
                        throw new AdministrationInputException("The timeout rejection template reference is ambiguous at system scope.");
                    }
                    template = system.Length == 1
                        ? ResolveEffectiveTemplate(rows, system[0].Id, organizationId)
                        : null;
                }
            }
            else
            {
                var system = rows.Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId &&
                        string.Equals(item.TemplateKey, key, StringComparison.Ordinal))
                    .ToArray();
                if (system.Length > 1)
                {
                    throw new AdministrationInputException("The timeout rejection template reference is ambiguous at system scope.");
                }
                template = system.Length == 1
                    ? ResolveEffectiveTemplate(rows, system[0].Id, organizationId)
                    : null;
            }
        }
        if (template is null || !IsEligibleRejectionTemplate(template))
        {
            throw new AdministrationInputException("The timeout rejection template is unavailable, hidden, or outside the selected configuration scope.");
        }
        return template.ReferenceId;
    }

    private static bool IsEligibleRejectionTemplate(EffectiveRejectionTemplate template) =>
        template.TemplateKey.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase) &&
        !template.IsHidden &&
        !string.IsNullOrWhiteSpace(template.Subject) &&
        !string.IsNullOrWhiteSpace(template.Body);

    private static EffectiveRejectionTemplate? ResolveEffectiveTemplate(
        IReadOnlyList<EmailTemplate> rows,
        long requestedId,
        int organizationId)
    {
        var requested = rows.SingleOrDefault(item => item.Id == requestedId);
        if (requested is null)
        {
            return null;
        }

        if (requested.OrganizationId == LibraryScope.SystemOrganizationId)
        {
            var overrideRow = organizationId == LibraryScope.SystemOrganizationId
                ? null
                : rows.SingleOrDefault(item => item.OrganizationId == organizationId &&
                    !item.IsCustom && item.SourceTemplateId == requested.Id);
            return ToEffectiveTemplate(requested, overrideRow, requested.Id);
        }
        if (requested.OrganizationId != organizationId)
        {
            return null;
        }

        if (requested.IsCustom || requested.SourceTemplateId is null)
        {
            return new EffectiveRejectionTemplate(
                requested.Id,
                requested.Id,
                requested.TemplateKey,
                requested.IsHidden,
                Clean(requested.SubjectTemplate),
                Clean(requested.BodyTemplate));
        }
        var source = rows.SingleOrDefault(item => item.OrganizationId == LibraryScope.SystemOrganizationId && item.Id == requested.SourceTemplateId.Value);
        return source is null ? null : ToEffectiveTemplate(source, requested, requested.Id);
    }

    private static EffectiveRejectionTemplate ToEffectiveTemplate(
        EmailTemplate source,
        EmailTemplate? overrideRow,
        long referenceId) => new(
        referenceId,
        source.Id,
        source.TemplateKey,
        source.IsHidden || overrideRow?.IsHidden == true,
        Clean(overrideRow?.SubjectTemplate) ?? Clean(source.SubjectTemplate),
        Clean(overrideRow?.BodyTemplate) ?? Clean(source.BodyTemplate));

    private static async Task<List<EmailTemplate>> LoadTemplateRowsIncludingPendingAsync(
        AsapDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.EmailTemplates.ToListAsync(cancellationToken);
        foreach (var entry in context.ChangeTracker.Entries<EmailTemplate>())
        {
            var existing = entry.Entity.Id == 0
                ? null
                : rows.SingleOrDefault(item => item.Id == entry.Entity.Id);
            if (entry.State == EntityState.Deleted)
            {
                if (existing is not null)
                {
                    rows.Remove(existing);
                }

                continue;
            }
            if (existing is not null)
            {
                var index = rows.IndexOf(existing);
                rows[index] = entry.Entity;
            }
            else if (entry.State == EntityState.Added)
            {
                rows.Add(entry.Entity);
            }
        }
        return rows;
    }

    private static async Task<List<WorkflowSettings>> LoadWorkflowRowsIncludingPendingAsync(
        AsapDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.WorkflowSettings.ToListAsync(cancellationToken);
        foreach (var entry in context.ChangeTracker.Entries<WorkflowSettings>())
        {
            var existing = rows.SingleOrDefault(item => item.OrganizationId == entry.Entity.OrganizationId);
            if (entry.State == EntityState.Deleted)
            {
                if (existing is not null)
                {
                    rows.Remove(existing);
                }

                continue;
            }
            if (existing is not null)
            {
                var index = rows.IndexOf(existing);
                rows[index] = entry.Entity;
            }
            else if (entry.State == EntityState.Added)
            {
                rows.Add(entry.Entity);
            }
        }
        return rows;
    }

    private static async Task ValidateRejectionTemplateReferencesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var templates = await LoadTemplateRowsIncludingPendingAsync(context, cancellationToken);
        var workflows = await LoadWorkflowRowsIncludingPendingAsync(context, cancellationToken);
        var selected = organizationId == LibraryScope.SystemOrganizationId
            ? workflows
            : workflows.Where(item => item.OrganizationId == organizationId).ToList();
        foreach (var workflow in selected)
        {
            if (!workflow.OutstandingTimeoutRejectionTemplateId.HasValue)
            {
                continue;
            }

            var template = ResolveEffectiveTemplate(
                templates,
                workflow.OutstandingTimeoutRejectionTemplateId.Value,
                workflow.OrganizationId);
            if (template is null || !IsEligibleRejectionTemplate(template))
            {
                throw new AdministrationInputException("The effective timeout rejection template is unavailable, hidden, or outside the selected configuration scope.");
            }
        }
    }
}
