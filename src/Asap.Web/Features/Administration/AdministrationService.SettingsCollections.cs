using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
        if (!TryGetAtRootOrSection(payload, systemSection, out var value, "patronEmbedAllowedOrigins", "origins"))
        {
            return;
        }

        var normalized = ParseStrictStringSet(value, "Embed origins")
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

        var enabled = ParseParticipationIds(value);
        var allOrganizations = await context.Organizations
            .Where(item => item.Id > 1)
            .ToListAsync(cancellationToken);
        var organizations = allOrganizations
            .Where(item => OrganizationAuthority.IsLibrary(item))
            .ToList();
        var knownLibraryIds = organizations.Select(item => item.Id).ToHashSet();
        if (enabled.Any(id => !knownLibraryIds.Contains(id)))
        {
            throw new AdministrationInputException("Participation may only include known Polaris libraries.");
        }
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
        JsonElement payload,
        JsonElement workflow,
        JsonElement patron,
        CancellationToken cancellationToken)
    {
        if (TryGetAtRootOrSection(payload, workflow, out var creators, "commonAuthorsList", "commonCreators", "commonCreatorsList"))
        {
            var values = ParseValues(creators);
            await ReplaceCommonCreatorsAsync(context, organizationId, values,
                creators.ValueKind == JsonValueKind.Null, cancellationToken);
        }

        if (TryGetAtRootOrSection(payload, workflow, out var patronCodes, "allowedPatronCodeIds", "patronCodeIds"))
        {
            if (!TryParsePatronCodeIds(patronCodes, out var values))
            {
                throw new InvalidOperationException("Patron-code payload was not validated.");
            }
            await ReplacePatronCodesAsync(context, organizationId, values,
                patronCodes.ValueKind == JsonValueKind.Null, cancellationToken);
        }

        if (TryGetAtRootOrSection(payload, patron, out var publicationOptions, "publicationOptions", "publicationOptionSet"))
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
        var seenProviderIds = new HashSet<long>();
        foreach (var input in inputs)
        {
            var provider = input.Id.HasValue
                ? systemProviders.SingleOrDefault(item => item.Id == input.Id.Value)
                : systemProviders.SingleOrDefault(item => item.ProviderKey == input.Key);
            if (input.Id.HasValue && provider is null)
            {
                throw new AdministrationInputException("The selected external provider ID is outside the settings scope.");
            }
            if (provider is not null && input.Key is not null &&
                !string.Equals(provider.ProviderKey, input.Key, StringComparison.Ordinal))
            {
                throw new AdministrationInputException("The external provider ID and key do not identify the same provider.");
            }
            if (provider is not null && provider.Id > 0 && !seenProviderIds.Add(provider.Id))
            {
                throw new AdministrationInputException("External provider IDs and keys must identify unique providers.");
            }
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
            !TryGetAtRootOrSection(payload, patron, out _, "formatLabels") &&
            !TryGetAtRootOrSection(payload, patron, out _, "formatOrder") &&
            !TryGetAtRootOrSection(payload, patron, out _, "availableFormats"))
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
            var seenFormatTargets = new HashSet<(int OwnerOrganizationId, long? Id, string? Code)>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new AdministrationInputException("Each format must be an object.");
                }

                await ApplyFormatObjectAsync(context, organizationId, item, systemFormats, customFormats,
                    seenFormatTargets, cancellationToken);
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
        await ApplyLegacyFormatMapsAsync(context, organizationId, payload, patron, systemFormats, customFormats, cancellationToken);
    }

    private static async Task ApplyFormatObjectAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement item,
        IReadOnlyList<MaterialFormat> systemFormats,
        IReadOnlyList<MaterialFormat> customFormats,
        HashSet<(int OwnerOrganizationId, long? Id, string? Code)> seenFormatTargets,
        CancellationToken cancellationToken)
    {
        ValidateFormatInput(item);
        var id = ReadOptionalLong(item, "id", "materialFormatId");
        var code = Clean(ReadOptionalString(item, "code", "format"));
        var explicitOwner = ReadOptionalInt(item, "ownerOrganizationId");
        var customFlag = ReadOptionalBoolean(item, "custom");
        var owner = explicitOwner ?? (customFlag == true ? organizationId : 1);
        var byId = id.HasValue
            ? systemFormats.Concat(customFormats).SingleOrDefault(format => format.Id == id.Value)
            : null;
        if (id.HasValue && byId is null)
        {
            throw new AdministrationInputException("The selected format ID is not available in this settings scope.");
        }
        if (byId is not null && code is not null && !string.Equals(byId.Code, code, StringComparison.Ordinal))
        {
            throw new AdministrationInputException("The selected format ID and code do not identify the same format.");
        }
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            if (byId is not null && byId.OwnerOrganizationId != LibraryScope.SystemOrganizationId ||
                explicitOwner.HasValue && explicitOwner.Value != LibraryScope.SystemOrganizationId || customFlag == true)
            {
                throw new AdministrationInputException("System settings cannot edit a library-owned format.");
            }
            var byCode = code is null ? null : systemFormats.SingleOrDefault(format => format.Code == code);
            var target = byId ?? byCode;
            if (target is null)
            {
                if (owner != LibraryScope.SystemOrganizationId)
                {
                    throw new AdministrationInputException("System settings cannot create a library-owned format.");
                }

                target = CreateFormat(item, code, 1);
                EnsureUniqueFormatTarget(seenFormatTargets, target);
                context.MaterialFormats.Add(target);
                if (systemFormats is List<MaterialFormat> mutableSystemFormats)
                {
                    mutableSystemFormats.Add(target);
                }
            }
            else
            {
                EnsureUniqueFormatTarget(seenFormatTargets, target);
            }
            ApplyOwnedFormat(target, item, allowCode: false);
            if (GetBool(item, "deleted") == true || GetBool(item, "delete") == true)
            {
                target.IsEnabled = false;
            }
            return;
        }

        if (explicitOwner.HasValue && explicitOwner.Value is not (1) && explicitOwner.Value != organizationId)
        {
            throw new AdministrationInputException("The format owner is outside the selected settings scope.");
        }
        if (explicitOwner.HasValue && byId is not null && explicitOwner.Value != byId.OwnerOrganizationId)
        {
            throw new AdministrationInputException("The selected format ID and owner do not identify the same format.");
        }
        var requestedCustom = owner == organizationId || customFlag == true || byId?.OwnerOrganizationId == organizationId;
        if (customFlag == false && (owner == organizationId || byId?.OwnerOrganizationId == organizationId) ||
            customFlag == true && byId?.OwnerOrganizationId == LibraryScope.SystemOrganizationId)
        {
            throw new AdministrationInputException("The custom-format flag conflicts with the selected format ID.");
        }
        if (requestedCustom)
        {
            var byCode = code is null ? null : customFormats.SingleOrDefault(format => format.Code == code);
            if (byId is not null && byId.OwnerOrganizationId != organizationId)
            {
                throw new AdministrationInputException("The selected format ID does not identify a library-owned format.");
            }
            if (byId is not null && byCode is not null && byId.Id != byCode.Id)
            {
                throw new AdministrationInputException("The selected format ID and code do not identify the same format.");
            }
            if (code is not null && systemFormats.Any(format => format.Code == code))
            {
                throw new AdministrationInputException("A custom format code must not collide with a system format.");
            }
            var custom = byId ?? byCode;
            if (custom is null)
            {
                if (code is null)
                {
                    throw new AdministrationInputException("A custom format code must be present and must not collide with a system format.");
                }
                custom = CreateFormat(item, code, organizationId);
                EnsureUniqueFormatTarget(seenFormatTargets, custom);
                context.MaterialFormats.Add(custom);
                if (customFormats is List<MaterialFormat> mutableCustomFormats)
                {
                    mutableCustomFormats.Add(custom);
                }
            }
            else
            {
                EnsureUniqueFormatTarget(seenFormatTargets, custom);
            }
            ApplyOwnedFormat(custom, item, allowCode: false);
            return;
        }

        if (byId is not null && byId.OwnerOrganizationId != LibraryScope.SystemOrganizationId)
        {
            throw new AdministrationInputException("The selected format ID does not identify a system format.");
        }
        var systemByCode = code is null ? null : systemFormats.SingleOrDefault(format => format.Code == code);
        var system = byId ?? systemByCode;
        if (byId is not null && systemByCode is not null && byId.Id != systemByCode.Id)
        {
            throw new AdministrationInputException("The selected format ID and code do not identify the same format.");
        }
        if (system is null)
        {
            throw new AdministrationInputException("The selected system format is not available in this library scope.");
        }
        EnsureUniqueFormatTarget(seenFormatTargets, system);

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

    private static void EnsureUniqueFormatTarget(
        HashSet<(int OwnerOrganizationId, long? Id, string? Code)> seenTargets,
        MaterialFormat target)
    {
        var id = target.Id > 0 ? target.Id : (long?)null;
        var identity = (target.OwnerOrganizationId, id, id.HasValue ? null : target.Code);
        if (!seenTargets.Add(identity))
        {
            throw new AdministrationInputException("A material format may only appear once in a settings replacement.");
        }
    }

    private static async Task ApplyLegacyFormatMapsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        JsonElement patron,
        IReadOnlyList<MaterialFormat> systemFormats,
        IReadOnlyList<MaterialFormat> customFormats,
        CancellationToken cancellationToken)
    {
        var hasLabels = TryGetAtRootOrSection(payload, patron, out var labelValue, "formatLabels");
        if (hasLabels && labelValue.ValueKind != JsonValueKind.Object)
        {
            throw new AdministrationInputException("Format labels must be an object.");
        }
        var labels = hasLabels ? labelValue : default;
        var order = TryGetAtRootOrSection(payload, patron, out var orderValue, "formatOrder") ? ParseValues(orderValue) : [];
        var available = TryGetAtRootOrSection(payload, patron, out var availableValue, "availableFormats")
            ? ParseValues(availableValue).ToHashSet(StringComparer.Ordinal)
            : null;
        var formats = systemFormats.Concat(customFormats).ToArray();
        var knownCodes = formats.Select(format => format.Code).ToHashSet(StringComparer.Ordinal);
        if (order.Any(code => !knownCodes.Contains(code)) ||
            available is not null && available.Any(code => !knownCodes.Contains(code)))
        {
            throw new AdministrationInputException("Legacy format settings contain an unknown format code.");
        }
        if (labels.ValueKind == JsonValueKind.Object && labels.EnumerateObject().Any(item => !knownCodes.Contains(item.Name)))
        {
            throw new AdministrationInputException("Legacy format labels contain an unknown format code.");
        }
        if (labels.ValueKind != JsonValueKind.Object && order.Count == 0 && available is null)
        {
            return;
        }

        foreach (var format in formats)
        {
            var itemLabel = labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(format.Code, out var label)
                ? ReadRequiredStringValue(label, "Format label")
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
        var hasDefinitions = TryGetAny(payload, out var definitions, "customFields", "additionalFieldDefinitions") ||
                             TryGetAny(patron, out definitions, "customFields", "additionalFieldDefinitions");
        var hasRules = TryGetAny(payload, out var rules, "formatRules", "patronFormatRules") ||
                       TryGetAny(patron, out rules, "formatRules", "patronFormatRules");
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            if (hasDefinitions || hasRules && HasCustomFieldRules(rules))
            {
                throw new AdministrationInputException("Custom fields and their rules are library-only settings.");
            }
            return;
        }

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
            var requestedKeys = new HashSet<string>(StringComparer.Ordinal);
            var requestedIds = new HashSet<long>();
            foreach (var item in definitions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new AdministrationInputException("Each custom field must be an object.");
                }

                var id = ReadOptionalLong(item, "id");
                var key = Clean(ReadOptionalString(item, "key", "fieldKey"));
                if (key is null)
                {
                    throw new AdministrationInputException("Each custom field requires a stable key.");
                }
                if (!requestedKeys.Add(key))
                {
                    throw new AdministrationInputException("Custom field keys must be unique.");
                }

                if (id.HasValue && id.Value <= 0)
                {
                    throw new AdministrationInputException("A custom field ID must be a positive integer or null.");
                }
                var fieldById = id.HasValue ? existing.SingleOrDefault(row => row.Id == id.Value) : null;
                if (id.HasValue && fieldById is null)
                {
                    throw new AdministrationInputException("The selected custom field ID is outside the library settings scope.");
                }
                if (fieldById is not null && !string.Equals(fieldById.FieldKey, key, StringComparison.Ordinal))
                {
                    throw new AdministrationInputException("The selected custom field ID and key do not identify the same field.");
                }
                var field = fieldById ?? existing.SingleOrDefault(row => row.FieldKey == key);
                if (field is not null && !requestedIds.Add(field.Id))
                {
                    throw new AdministrationInputException("Custom field IDs must be unique.");
                }
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
                field.FieldType = NormalizeFieldType(ReadOptionalString(item, "type", "fieldType") ?? field.FieldType);
                field.Label = RequireText(ReadOptionalString(item, "label") ?? field.Label, "Custom field label");
                if (HasProperty(item, "helpText"))
                {
                    field.HelpText = Clean(ReadOptionalString(item, "helpText"));
                }

                if (ReadOptionalBoolean(item, "enabled").HasValue)
                {
                    field.IsEnabled = ReadOptionalBoolean(item, "enabled")!.Value;
                }

                if (ReadOptionalInt(item, "sortOrder").HasValue)
                {
                    field.SortOrder = ReadOptionalInt(item, "sortOrder")!.Value;
                }

                if (TryGetAny(item, out var options, "options"))
                {
                    if (options.ValueKind != JsonValueKind.Array)
                    {
                        throw new AdministrationInputException("Custom field options must be an array.");
                    }
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
                throw new AdministrationInputException("Each format rule must include a customFields object.");
            }

            var seenFieldKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fieldProperty in custom.EnumerateObject())
            {
                if (!seenFieldKeys.Add(fieldProperty.Name))
                {
                    throw new AdministrationInputException("Custom field rule keys must be unique.");
                }
                if (!fieldsByKey.TryGetValue(fieldProperty.Name, out var field))
                {
                    throw new AdministrationInputException($"Custom field rule {fieldProperty.Name} is not defined in the selected library.");
                }

                var rule = fieldProperty.Value;
                if (rule.ValueKind != JsonValueKind.Object ||
                    !TryGetAny(rule, out var modeValue, "mode") || modeValue.ValueKind != JsonValueKind.String)
                {
                    throw new AdministrationInputException("Each custom field rule requires a valid mode.");
                }
                var mode = modeValue.GetString();
                if (mode is not ("required" or "optional" or "hidden"))
                {
                    throw new AdministrationInputException("Custom field mode must be required, optional, or hidden.");
                }
                string? labelOverride = null;
                if (HasProperty(rule, "labelOverride") || HasProperty(rule, "label"))
                {
                    labelOverride = ReadOptionalString(rule, "labelOverride", "label");
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
                    LabelOverride = Clean(labelOverride)
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

    private static async Task ValidateEffectiveRequiredSelectRulesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var libraryIds = organizationId == LibraryScope.SystemOrganizationId
            ? await context.Organizations.AsNoTracking()
                .Where(item => item.Id > LibraryScope.SystemOrganizationId &&
                               item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId)
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken)
            : [organizationId];
        foreach (var libraryId in libraryIds)
        {
            await ValidateLibraryRequiredSelectRulesAsync(context, libraryId, cancellationToken);
        }
    }

    private static bool HasEffectiveFormatOrCustomFieldEdits(JsonElement payload, JsonElement patron) =>
        HasEffectiveFormatOrCustomFieldEditsIn(payload) || HasEffectiveFormatOrCustomFieldEditsIn(patron);

    private static bool HasEffectiveFormatOrCustomFieldEditsIn(JsonElement section) =>
        TryGetAny(section, out _, "formats", "materialFormats") ||
        TryGetAny(section, out _, "availableFormats") ||
        TryGetAny(section, out _, "customFields", "additionalFieldDefinitions") ||
        TryGetAny(section, out _, "formatRules", "patronFormatRules");

    private static async Task ValidateLibraryRequiredSelectRulesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var requiredSelectFields = await context.PatronCustomFields.AsNoTracking()
            .Where(field => field.LibraryOrganizationId == organizationId && field.IsEnabled &&
                            field.FieldType == "select" &&
                            !context.PatronCustomFieldOptions.Any(option =>
                                option.PatronCustomFieldId == field.Id && option.IsEnabled))
            .Select(field => field.Id)
            .ToArrayAsync(cancellationToken);
        if (requiredSelectFields.Length == 0)
        {
            return;
        }

        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == LibraryScope.SystemOrganizationId ||
                           item.OwnerOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var overrides = await context.MaterialFormatOverrides.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .ToDictionaryAsync(item => item.MaterialFormatId, cancellationToken);
        var activeFormatIds = formats
            .Where(format => overrides.TryGetValue(format.Id, out var overrideRow)
                ? overrideRow.IsEnabled ?? format.IsEnabled
                : format.IsEnabled)
            .Select(format => format.Id)
            .ToArray();
        if (activeFormatIds.Length == 0)
        {
            return;
        }

        var impossibleRule = await context.MaterialFormatCustomFieldRules.AsNoTracking()
            .AnyAsync(rule => rule.LibraryOrganizationId == organizationId &&
                              rule.Mode == "required" &&
                              requiredSelectFields.Contains(rule.PatronCustomFieldId) &&
                              activeFormatIds.Contains(rule.MaterialFormatId),
                cancellationToken);
        if (impossibleRule)
        {
            throw new AdministrationInputException(
                "An enabled required select field must have at least one enabled option for every active format.");
        }
    }

    private async Task ApplyAutoClaimRulesAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryGetAny(payload, out var value, "autoClaimRules", "formatClaimRules"))
        {
            return;
        }
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            throw new AdministrationInputException("Auto-claim rules are library-only settings.");
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
        var seenFormats = new HashSet<long>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new AdministrationInputException("Each auto-claim rule must be an object.");
            }

            var formatId = ReadOptionalLong(item, "materialFormatId", "formatId");
            if (!formatId.HasValue || formatId.Value <= 0 || formats.All(format => format.Id != formatId.Value))
            {
                throw new AdministrationInputException("Each auto-claim rule must reference a format in the selected library scope.");
            }
            if (!seenFormats.Add(formatId.Value))
            {
                throw new AdministrationInputException("Only one auto-claim rule is allowed per format.");
            }
            var isActive = ReadOptionalBoolean(item, "active", "isActive") ?? true;
            if (!isActive)
            {
                desired.Remove(formatId.Value);
                continue;
            }
            var staffId = ReadOptionalLong(item, "staffUserId", "staffId");
            if (!staffId.HasValue || staffId.Value <= 0)
            {
                throw new AdministrationInputException("An active auto-claim rule requires a staff user.");
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
        var seenTemplateTargets = new HashSet<(int OrganizationId, string Kind, long? Id, string? Key)>();
        if (TryGetAny(payload, out var templates, "templates", "emailTemplates"))
        {
            if (templates.ValueKind != JsonValueKind.Array)
            {
                throw new AdministrationInputException("templates must be an array.");
            }

            foreach (var item in templates.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new AdministrationInputException("Each email template must be an object.");
                }
                await ApplyTemplateObjectAsync(context, organizationId, item, null, seenTemplateTargets, cancellationToken);
            }
            return;
        }

        if (!TryGetAny(payload, out var emails, "emails", "email"))
        {
            return;
        }

        if (emails.ValueKind != JsonValueKind.Object)
        {
            throw new AdministrationInputException("Email template settings must be an object.");
        }

        foreach (var property in emails.EnumerateObject())
        {
            if (property.Name is "fromAddress" or "fromName" or "postmarkToken" or "serverToken" or
                "clearPostmarkToken" or "clearServerToken" or "rejection_templates")
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AdministrationInputException("Each email template must be an object.");
            }
            await ApplyTemplateObjectAsync(context, organizationId, property.Value, property.Name,
                seenTemplateTargets, cancellationToken);
        }
        if (emails.TryGetProperty("rejection_templates", out var rejectionTemplates))
        {
            if (rejectionTemplates.ValueKind != JsonValueKind.Array)
            {
                throw new AdministrationInputException("Rejection templates must be an array.");
            }
            foreach (var item in rejectionTemplates.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new AdministrationInputException("Each rejection template must be an object.");
                }
                await ApplyTemplateObjectAsync(context, organizationId, item, null, seenTemplateTargets, cancellationToken);
            }
        }
    }

    private static async Task ApplyTemplateObjectAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement item,
        string? legacyKey,
        HashSet<(int OrganizationId, string Kind, long? Id, string? Key)> seenTemplateTargets,
        CancellationToken cancellationToken)
    {
        ValidateTemplateInput(item);
        var itemOrganization = ReadOptionalInt(item, "organizationId");
        if (itemOrganization.HasValue && itemOrganization.Value != organizationId)
        {
            throw new AdministrationInputException("An email template belongs to a different settings scope.");
        }

        var suppliedId = ReadOptionalLong(item, "id");
        var key = Clean(ReadOptionalString(item, "templateKey", "key") ?? legacyKey);
        var sourceId = ReadOptionalLong(item, "sourceTemplateId", "sourceId");
        var resetFlag = ReadOptionalBoolean(item, "reset") == true;
        var systemDefaultFlag = ReadOptionalBoolean(item, "useSystemDefault") == true;
        var overridden = ReadOptionalBoolean(item, "overridden");
        var reset = resetFlag || systemDefaultFlag || overridden == false;
        var isCustom = ReadOptionalBoolean(item, "isCustom", "custom") == true;
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
            if (suppliedId.HasValue && (template is null || template.Id != suppliedId.Value))
            {
                throw new AdministrationInputException("The selected system template ID and key do not identify the same template.");
            }
            template ??= new EmailTemplate
            {
                OrganizationId = 1,
                TemplateKey = key,
                IsCustom = false,
                IsHidden = false
            };
            EnsureUniqueTemplateTarget(seenTemplateTargets, organizationId, "system", template.Id, key);
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
            if (suppliedId.HasValue && (custom is null || custom.Id != suppliedId.Value))
            {
                throw new AdministrationInputException("The selected custom template ID and key do not identify the same template.");
            }
            EnsureUniqueTemplateTarget(seenTemplateTargets, organizationId, "custom", custom?.Id ?? 0, key);
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
        if (key is not null && !string.Equals(key, source.TemplateKey, StringComparison.Ordinal))
        {
            throw new AdministrationInputException("The selected system template ID and key do not identify the same template.");
        }

        var overrideRow = await context.EmailTemplates.SingleOrDefaultAsync(
            row => row.OrganizationId == organizationId && row.SourceTemplateId == source.Id, cancellationToken);
        if (suppliedId.HasValue && (overrideRow is null || overrideRow.Id != suppliedId.Value))
        {
            throw new AdministrationInputException("The selected template ID and source do not identify the same override.");
        }
        EnsureUniqueTemplateTarget(seenTemplateTargets, organizationId, "lineage", source.Id, null);
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

    private static void EnsureUniqueTemplateTarget(
        HashSet<(int OrganizationId, string Kind, long? Id, string? Key)> seenTargets,
        int organizationId,
        string kind,
        long id,
        string? key)
    {
        var persistedId = id > 0 ? id : (long?)null;
        var identity = (organizationId, kind, persistedId, persistedId.HasValue ? null : key);
        if (!seenTargets.Add(identity))
        {
            throw new AdministrationInputException("An email template may only appear once in a settings replacement.");
        }
    }

    private static void ApplyTemplateContent(EmailTemplate template, JsonElement item, bool requireContent)
    {
        if (HasProperty(item, "subject"))
        {
            template.SubjectTemplate = ReadOptionalString(item, "subject");
        }
        else if (HasProperty(item, "subjectTemplate"))
        {
            template.SubjectTemplate = ReadOptionalString(item, "subjectTemplate");
        }

        if (HasProperty(item, "body"))
        {
            template.BodyTemplate = ReadOptionalString(item, "body");
        }
        else if (HasProperty(item, "bodyTemplate"))
        {
            template.BodyTemplate = ReadOptionalString(item, "bodyTemplate");
        }

        if (HasProperty(item, "displayName"))
        {
            template.DisplayName = Clean(ReadOptionalString(item, "displayName"));
        }

        if (ReadOptionalBoolean(item, "enabled").HasValue)
        {
            template.IsHidden = !ReadOptionalBoolean(item, "enabled")!.Value;
        }

        if (ReadOptionalBoolean(item, "hidden").HasValue)
        {
            template.IsHidden = ReadOptionalBoolean(item, "hidden")!.Value;
        }

        if (ReadOptionalBoolean(item, "isHidden").HasValue)
        {
            template.IsHidden = ReadOptionalBoolean(item, "isHidden")!.Value;
        }

        if (ReadOptionalInt(item, "sortOrder").HasValue)
        {
            template.SortOrder = ReadOptionalInt(item, "sortOrder")!.Value;
        }

        if (requireContent && (string.IsNullOrWhiteSpace(template.SubjectTemplate) || string.IsNullOrWhiteSpace(template.BodyTemplate)))
        {
            throw new AdministrationInputException("A system or custom email template requires both subject and body content.");
        }
    }

    private static void ValidateTemplateInput(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new AdministrationInputException("Each email template must be an object.");
        }
        var id = ReadOptionalLong(item, "id");
        var sourceId = ReadOptionalLong(item, "sourceTemplateId", "sourceId");
        if (id.HasValue && id.Value <= 0 || sourceId.HasValue && sourceId.Value <= 0)
        {
            throw new AdministrationInputException("Template IDs must be positive integers.");
        }
        ReadOptionalInt(item, "organizationId");
        ReadOptionalString(item, "templateKey", "key");
        ReadOptionalBoolean(item, "reset");
        ReadOptionalBoolean(item, "useSystemDefault");
        ReadOptionalBoolean(item, "overridden");
        ReadOptionalBoolean(item, "isCustom", "custom");
        ReadOptionalBoolean(item, "libraryCustom");
        ReadOptionalString(item, "subject", "subjectTemplate");
        ReadOptionalString(item, "body", "bodyTemplate");
        ReadOptionalString(item, "displayName");
        ReadOptionalBoolean(item, "enabled");
        ReadOptionalBoolean(item, "hidden");
        ReadOptionalBoolean(item, "isHidden");
        ReadOptionalInt(item, "sortOrder");
        ValidateResetOverrideIntent(item, "Template");
        ValidateTemplateVisibilityIntent(item);
        if (ReadOptionalBoolean(item, "isCustom", "custom") == true && sourceId.HasValue)
        {
            throw new AdministrationInputException("A custom template cannot also reference a system source template.");
        }
    }

    private static void ValidateTemplateVisibilityIntent(JsonElement item)
    {
        var enabled = ReadOptionalBoolean(item, "enabled");
        var hidden = ReadOptionalBoolean(item, "hidden", "isHidden");
        if (enabled.HasValue && hidden.HasValue && enabled.Value == hidden.Value)
        {
            throw new AdministrationInputException("Template enabled and hidden flags conflict.");
        }
    }

    private static void ValidateResetOverrideIntent(JsonElement item, string collectionName)
    {
        var reset = ReadOptionalBoolean(item, "reset", "useSystemDefault");
        var overridden = ReadOptionalBoolean(item, "overridden");
        if (((HasProperty(item, "reset") || HasProperty(item, "useSystemDefault")) && !reset.HasValue) ||
            (HasProperty(item, "overridden") && !overridden.HasValue))
        {
            throw new AdministrationInputException($"{collectionName} reset and override flags must be Boolean values when supplied.");
        }
        if (reset.HasValue && overridden.HasValue && reset.Value == overridden.Value)
        {
            throw new AdministrationInputException($"{collectionName} reset and override flags conflict.");
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
        if (branding.ValueKind != JsonValueKind.Object)
        {
            throw new AdministrationInputException("Branding settings must be an object.");
        }

        var altText = ReadOptionalString(branding, "altText", "logoAlt", "logoAltText");
        var logoData = ReadOptionalString(branding, "logoData");
        var contentTypeValue = ReadOptionalString(branding, "contentType", "logoContentType");
        var fileName = ReadOptionalString(branding, "fileName", "logoFileName");
        var clearLogo = ReadOptionalBoolean(branding, "clearLogo", "removeLogo") == true;
        if (clearLogo && logoData is not null)
        {
            throw new AdministrationInputException("A logo image and clear command cannot be submitted together.");
        }

        var hasLogoData = HasProperty(branding, "logoData");
        var hasAlt = HasProperty(branding, "altText") || HasProperty(branding, "logoAlt") || HasProperty(branding, "logoAltText");
        if (!hasLogoData && !hasAlt && !clearLogo)
        {
            return;
        }

        var row = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var isNew = row is null;
        row ??= new Branding { OrganizationId = organizationId };
        if (hasAlt)
        {
            row.LogoAltText = Clean(altText);
        }
        if (clearLogo)
        {
            row.LogoData = null;
            row.LogoContentType = null;
            row.LogoFileName = null;
        }
        if (hasLogoData && !clearLogo)
        {
            var encoded = Clean(logoData);
            if (encoded is null)
            {
                throw new AdministrationInputException("logoData must contain a base64 encoded PNG, JPEG, or GIF image.");
            }

            byte[] data;
            try { data = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new AdministrationInputException("logoData must be valid base64."); }
            var contentType = Clean(contentTypeValue);
            if (!LogoImageValidator.TryValidate(data, contentType, out var logoInfo, out var logoError))
            {
                throw new AdministrationInputException(logoError);
            }
            row.LogoData = data;
            row.LogoContentType = logoInfo!.ContentType;
            row.LogoFileName = Clean(fileName) ?? "logo";
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
        ValidateFormatInput(item);
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
        ValidateFormatInput(item);
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
        if (!TryGetAny(item, out var field, name))
        {
            return;
        }

        if (field.ValueKind != JsonValueKind.Object)
        {
            throw new AdministrationInputException($"The {name} format rule must be an object.");
        }

        var modeInput = ReadOptionalString(field, "mode");
        var labelInput = ReadOptionalString(field, "label");
        var mode = NormalizeFieldMode(modeInput ?? defaultMode, forceRequired);
        var label = Clean(labelInput) ?? defaultLabel;
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
        if (!TryGetAny(item, out var field, name))
        {
            return;
        }

        if (field.ValueKind != JsonValueKind.Object)
        {
            throw new AdministrationInputException($"The {name} format override must be an object.");
        }

        var modeInput = ReadOptionalString(field, "mode");
        var labelInput = ReadOptionalString(field, "label");
        var mode = NormalizeFieldMode(modeInput ?? baselineMode, name == "title");
        var label = Clean(labelInput) ?? baselineLabel;
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

    private static void ValidateFormatInput(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new AdministrationInputException("Each format setting must be an object.");
        }

        var id = ReadOptionalLong(item, "id", "materialFormatId");
        if (id.HasValue && id.Value <= 0)
        {
            throw new AdministrationInputException("A format ID must be a positive integer.");
        }
        ReadOptionalString(item, "code", "format");
        var owner = ReadOptionalInt(item, "ownerOrganizationId");
        if (owner.HasValue && owner.Value <= 0)
        {
            throw new AdministrationInputException("A format owner ID must be positive.");
        }
        ReadOptionalString(item, "label");
        ReadOptionalInt(item, "sortOrder");
        ReadOptionalBoolean(item, "isEnabled", "enabled");
        ReadOptionalBoolean(item, "deleted", "delete");
        ReadOptionalBoolean(item, "custom");
        ReadOptionalBoolean(item, "reset", "useSystemDefault");
        ReadOptionalBoolean(item, "overridden");
        ValidateResetOverrideIntent(item, "Format");
        ReadOptionalString(item, "messageBehavior");
        ReadOptionalString(item, "message");
        foreach (var name in new[] { "title", "author", "identifier", "publication" })
        {
            if (!TryGetAny(item, out var field, name))
            {
                continue;
            }
            if (field.ValueKind != JsonValueKind.Object)
            {
                throw new AdministrationInputException($"The {name} format rule must be an object.");
            }
            var mode = ReadOptionalString(field, "mode");
            ReadOptionalString(field, "label");
            if (mode is not null)
            {
                _ = NormalizeFieldMode(mode, forceRequired: false);
            }
        }
        if (TryGetAny(item, out var customFields, "customFields") &&
            customFields.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new AdministrationInputException("Format customFields must be an object or null.");
        }
    }

    private static string ReadRequiredStringValue(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new AdministrationInputException($"{label} must be a string.");
        }
        return value.GetString() ?? throw new AdministrationInputException($"{label} cannot be blank.");
    }

    private static bool HasCustomFieldRules(JsonElement value) => ContainsPropertyNamed(value, "customFields");

    private static bool ContainsPropertyNamed(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(item => ContainsPropertyNamed(item, name));
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal) || ContainsPropertyNamed(property.Value, name))
            {
                return true;
            }
        }
        return false;
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
            throw new AdministrationInputException("Providers must be an array.");
        }

        var result = new List<ProviderInput>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new AdministrationInputException("Each provider must be an object.");
            }

            var key = Clean(ReadOptionalString(item, "key", "providerKey"));
            var id = ReadOptionalLong(item, "id");
            ValidateResetOverrideIntent(item, "Provider");
            if (id.HasValue && id.Value <= 0)
            {
                throw new AdministrationInputException("External provider IDs must be positive integers.");
            }
            if (!id.HasValue && key is null)
            {
                throw new AdministrationInputException("Each provider requires an ID or key.");
            }
            var identity = id.HasValue ? $"id:{id.Value}" : $"key:{key}";
            if (!identities.Add(identity))
            {
                throw new AdministrationInputException("Provider IDs and keys must be unique.");
            }

            result.Add(new ProviderInput(
                key,
                id,
                ReadOptionalBoolean(item, "isEnabled", "enabled"),
                ReadOptionalString(item, "label"),
                ReadOptionalString(item, "urlTemplate", "url"),
                ReadOptionalBoolean(item, "reset", "useSystemDefault") == true,
                ReadOptionalBoolean(item, "overridden"),
                ReadOptionalInt(item, "sortOrder")));
        }
        return result;
    }

    private static IReadOnlyList<ProviderInput> ParseLegacyProviders(JsonElement workflow, JsonElement payload)
    {
        var result = new List<ProviderInput>();
        for (var index = 1; index <= 4; index++)
        {
            var enabled = ReadOptionalBoolean(workflow, $"externalSearch{index}Enabled") ??
                          ReadOptionalBoolean(payload, $"externalSearch{index}Enabled");
            var label = ReadOptionalString(workflow, $"externalSearch{index}Label") ??
                        ReadOptionalString(payload, $"externalSearch{index}Label");
            var url = ReadOptionalString(workflow, $"externalSearch{index}UrlTemplate") ??
                      ReadOptionalString(payload, $"externalSearch{index}UrlTemplate");
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
        if (value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    if (AdministrationSettingsBinding.HasAmbiguousProperties(document.RootElement))
                    {
                        throw new AdministrationInputException("Set options contain duplicate or conflicting properties.");
                    }
                    return ParseOptions(document.RootElement);
                }
                catch (AdministrationInputException)
                {
                    throw;
                }
                catch (JsonException exception)
                {
                    throw new AdministrationInputException($"Set options contain invalid JSON: {exception.Message}");
                }
            }
            return ParseValues(value).Select((label, index) => new SetOption(OptionKey(label, index), label, true, (index + 1) * 10)).ToArray();
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new AdministrationInputException("Set options must be an array or newline-delimited string.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SetOption>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Object))
            {
                throw new AdministrationInputException("Each set option must be a string or object.");
            }

            var label = item.ValueKind == JsonValueKind.String
                ? Clean(item.GetString())
                : Clean(ReadOptionalString(item, "label", "value", "name"));
            if (label is null)
            {
                throw new AdministrationInputException("Each set option requires a nonblank label.");
            }

            var key = item.ValueKind == JsonValueKind.Object
                ? Clean(ReadOptionalCollectionIdentity(item, "id", "key")) ?? OptionKey(label, index)
                : OptionKey(label, index);
            if (!seen.Add(key))
            {
                throw new AdministrationInputException("Set option IDs must be unique.");
            }

            var enabled = item.ValueKind == JsonValueKind.Object ? ReadOptionalBoolean(item, "enabled") ?? true : true;
            var sortOrder = item.ValueKind == JsonValueKind.Object
                ? ReadOptionalInt(item, "sortOrder") ?? ((index + 1) * 10)
                : ((index + 1) * 10);
            result.Add(new SetOption(key, label, enabled, sortOrder));
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
            if (!result.Add(id))
            {
                return false;
            }
        }
        ids = result.Order().ToArray();
        return true;
    }

    private static IReadOnlyList<string> ParseValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        IEnumerable<string?> source;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            source = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string?>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    values.Add(item.GetString());
                    continue;
                }
                if (item.ValueKind == JsonValueKind.Object)
                {
                    values.Add(ReadCollectionEntryValue(item));
                    continue;
                }
                throw new AdministrationInputException("Collection values must be strings or objects with a string value.");
            }
            source = values;
        }
        else
        {
            throw new AdministrationInputException("Collection values must be an array or newline-delimited string.");
        }

        var parsed = source.Select(item => Clean(item) ??
            throw new AdministrationInputException("Collection values cannot contain blank items."))
            .ToArray();
        if (parsed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsed.Length)
        {
            throw new AdministrationInputException("Collection values cannot contain duplicates.");
        }
        return parsed;
    }

    private static IReadOnlyList<string> ParseStrictStringSet(JsonElement value, string label)
    {
        IEnumerable<string?> source;
        if (value.ValueKind == JsonValueKind.String)
        {
            source = (value.GetString() ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string?>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new AdministrationInputException($"{label} must contain only strings.");
                }
                values.Add(item.GetString());
            }
            source = values;
        }
        else
        {
            throw new AdministrationInputException($"{label} must be a string or an array of strings.");
        }

        var parsed = source.Select(item => Clean(item) ?? throw new AdministrationInputException($"{label} cannot contain blank values."))
            .ToArray();
        if (parsed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsed.Length)
        {
            throw new AdministrationInputException($"{label} cannot contain duplicates.");
        }
        return parsed;
    }

    private static IReadOnlySet<int> ParseParticipationIds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new AdministrationInputException("Participation must be an array of library IDs.");
        }

        var ids = new HashSet<int>();
        JsonValueKind? itemKind = null;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
                itemKind.HasValue && itemKind.Value != item.ValueKind)
            {
                throw new AdministrationInputException("Participation IDs must use one consistent numeric representation.");
            }
            itemKind = item.ValueKind;
            var raw = item.ValueKind switch
            {
                JsonValueKind.Number => item.GetRawText(),
                JsonValueKind.String => item.GetString(),
                _ => throw new InvalidOperationException("Validated participation item has an invalid type.")
            };
            if (raw is null || !int.TryParse(raw, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= LibraryScope.SystemOrganizationId ||
                !ids.Add(id))
            {
                throw new AdministrationInputException("Participation must contain unique positive library IDs.");
            }
        }
        return ids;
    }

    private static IEnumerable<(string Code, JsonElement Rule)> EnumerateFormatRules(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (Clean(property.Name) is null || property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new AdministrationInputException("Each format rule requires a code and an object value.");
                }
                yield return (property.Name, property.Value);
            }

            yield break;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new AdministrationInputException("Format rules must be an object or array.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new AdministrationInputException("Each format rule must be an object.");
            }
            var code = Clean(ReadOptionalString(item, "code", "format"));
            if (code is null || !seen.Add(code))
            {
                throw new AdministrationInputException("Format rules require unique format codes.");
            }
            yield return (code, item);
        }
    }

    private static string? ReadOptionalCollectionIdentity(JsonElement root, params string[] names)
    {
        string? result = null;
        foreach (var name in names)
        {
            var current = ReadOptionalIdentifierText(root, name);
            if (current is null)
            {
                continue;
            }
            if (result is not null && !string.Equals(result, current, StringComparison.Ordinal))
            {
                throw new AdministrationInputException("A collection identity contains conflicting identifiers.");
            }
            result = current;
        }
        return result;
    }

    private static string? ReadOptionalIdentifierText(JsonElement root, string name)
    {
        if (!TryGetAny(root, out var value, name))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _))
        {
            return value.GetRawText();
        }
        throw new AdministrationInputException("A collection identifier must be a string, integer, or null.");
    }

    private static string? ReadCollectionEntryValue(JsonElement item)
    {
        var identity = ReadOptionalCollectionIdentity(item, "id", "key");
        var value = ReadOptionalString(item, "value");
        var name = ReadOptionalString(item, "name");
        if (value is not null && name is not null && !string.Equals(value, name, StringComparison.Ordinal))
        {
            throw new AdministrationInputException("A collection value contains conflicting text values.");
        }
        return identity ?? value ?? name;
    }

    private static string? ReadOptionalString(JsonElement root, params string[] names)
    {
        var found = false;
        string? result = null;
        foreach (var value in ReadAliasValues(root, names))
        {
            string? current = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => throw new AdministrationInputException("A settings value must be a string or null.")
            };
            if (found && !string.Equals(result, current, StringComparison.Ordinal))
            {
                throw new AdministrationInputException("A settings value contains conflicting aliases.");
            }
            result = current;
            found = true;
        }
        return result;
    }

    private static bool? ReadOptionalBoolean(JsonElement root, params string[] names)
    {
        var found = false;
        bool? result = null;
        foreach (var value in ReadAliasValues(root, names))
        {
            bool? current = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => throw new AdministrationInputException("A collection Boolean value is invalid.")
            };
            if (found && result != current)
            {
                throw new AdministrationInputException("A collection Boolean contains conflicting aliases.");
            }
            result = current;
            found = true;
        }
        return result;
    }

    private static int? ReadOptionalInt(JsonElement root, params string[] names)
    {
        var found = false;
        int? result = null;
        foreach (var value in ReadAliasValues(root, names))
        {
            int? current = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number when value.TryGetInt32(out var numericValue) => numericValue,
                JsonValueKind.String when int.TryParse(value.GetString(), out var stringValue) => stringValue,
                _ => throw new AdministrationInputException("A collection integer value is invalid.")
            };
            if (found && result != current)
            {
                throw new AdministrationInputException("A collection integer contains conflicting aliases.");
            }
            result = current;
            found = true;
        }
        return result;
    }

    private static long? ReadOptionalLong(JsonElement root, params string[] names)
    {
        var found = false;
        long? result = null;
        foreach (var value in ReadAliasValues(root, names))
        {
            long? current = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number when value.TryGetInt64(out var numericValue) => numericValue,
                JsonValueKind.String when long.TryParse(value.GetString(), out var stringValue) => stringValue,
                _ => throw new AdministrationInputException("A collection ID value is invalid.")
            };
            if (found && result != current)
            {
                throw new AdministrationInputException("A collection ID contains conflicting aliases.");
            }
            result = current;
            found = true;
        }
        return result;
    }

    private static IReadOnlyList<JsonElement> ReadAliasValues(JsonElement root, IReadOnlyList<string> names)
    {
        var values = new List<JsonElement>();
        foreach (var name in names)
        {
            if (TryGetAny(root, out var value, name))
            {
                values.Add(value);
            }
        }
        return values;
    }

    private static string NormalizeOrigin(string value)
    {
        var normalized = Clean(value) ?? throw new AdministrationInputException("Embed origins cannot be blank.");
        if (normalized.Any(char.IsWhiteSpace) || normalized.Contains('@') || normalized.Contains('\\') ||
            normalized.Any(character => character is '"' or '\'' or '`' or ';'))
        {
            throw new AdministrationInputException("Embed origins cannot contain credentials or invalid authority characters.");
        }
        if (!TryGetRawOriginAuthority(normalized, out var scheme, out var authority, out var suffix))
        {
            throw new AdministrationInputException("Embed origins must contain a valid HTTP or HTTPS authority.");
        }
        if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            authority.StartsWith("*.", StringComparison.Ordinal) &&
            normalized.StartsWith("https://*.", StringComparison.OrdinalIgnoreCase))
        {
            var wildcardHost = authority[2..];
            if (suffix.Length != 0 || !HasValidRawOriginAuthority(authority, allowWildcardHost: true) ||
                !Uri.TryCreate($"https://{wildcardHost}", UriKind.Absolute, out var wildcardUri) ||
                !string.IsNullOrEmpty(wildcardUri.UserInfo) || wildcardUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(wildcardUri.Query) || !string.IsNullOrEmpty(wildcardUri.Fragment) ||
                Uri.CheckHostName(wildcardUri.Host) != UriHostNameType.Dns)
            {
                throw new AdministrationInputException("Wildcard embed origins may not include a path or query.");
            }
            var wildcardPort = wildcardUri.IsDefaultPort ? string.Empty : $":{wildcardUri.Port}";
            return $"https://*.{wildcardUri.IdnHost.ToLowerInvariant()}{wildcardPort}";
        }
        if (suffix is not ("" or "/") || !HasValidRawOriginAuthority(authority, allowWildcardHost: false) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme == "http" && uri.Host is not ("localhost" or "127.0.0.1" or "[::1]")))
        {
            throw new AdministrationInputException("Embed origins must be HTTPS origins, with localhost allowed for HTTP development origins.");
        }
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}";
    }

    private static bool TryGetRawOriginAuthority(
        string value, out string scheme, out string authority, out string suffix)
    {
        scheme = string.Empty;
        authority = string.Empty;
        suffix = string.Empty;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        scheme = value[..separator];
        if (!scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var authorityStart = separator + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = value.Length;
        }
        authority = value[authorityStart..authorityEnd];
        suffix = value[authorityEnd..];
        return authority.Length > 0;
    }

    private static bool HasValidRawOriginAuthority(string authority, bool allowWildcardHost)
    {
        var wildcard = authority.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard && !allowWildcardHost)
        {
            return false;
        }
        if (allowWildcardHost && !wildcard)
        {
            return false;
        }

        var hostAndPort = wildcard ? authority[2..] : authority;
        var host = hostAndPort;
        if (hostAndPort.Length > 0 && hostAndPort[0] == '[')
        {
            if (wildcard)
            {
                return false;
            }

            var closingBracket = hostAndPort.IndexOf(']');
            if (closingBracket <= 1 || hostAndPort.IndexOf(']', closingBracket + 1) >= 0 ||
                hostAndPort[1..closingBracket].Contains('%') ||
                !IPAddress.TryParse(hostAndPort[1..closingBracket], out var address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            var suffix = hostAndPort[(closingBracket + 1)..];
            return suffix.Length == 0 || suffix[0] == ':' && IsValidRawPort(suffix[1..]);
        }

        var firstColon = hostAndPort.IndexOf(':');
        var lastColon = hostAndPort.LastIndexOf(':');
        if (firstColon != lastColon)
        {
            return false;
        }
        if (lastColon >= 0)
        {
            host = hostAndPort[..lastColon];
            if (!IsValidRawPort(hostAndPort[(lastColon + 1)..]))
            {
                return false;
            }
        }

        return IsValidAsciiDnsHost(host, requireMultipleLabels: wildcard) &&
               (!wildcard || !IPAddress.TryParse(host, out var wildcardAddress) ||
                wildcardAddress.AddressFamily != AddressFamily.InterNetwork);
    }

    private static bool IsValidRawPort(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
        port is >= 0 and <= 65535;

    private static bool IsValidAsciiDnsHost(string value, bool requireMultipleLabels)
    {
        if (value.EndsWith(".", StringComparison.Ordinal))
        {
            value = value[..^1];
        }
        if (value.Length is 0 or > 253)
        {
            return false;
        }

        var labels = value.Split('.');
        return (!requireMultipleLabels || labels.Length > 1) && labels.All(label =>
            label.Length is > 0 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
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
