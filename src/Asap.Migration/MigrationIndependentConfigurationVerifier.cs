using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

/// <summary>
/// Re-derives imported configuration from the immutable package during report
/// reconcile/recovery. This code intentionally owns its source interpretation
/// and SQL projections; it does not call the configuration importer.
/// </summary>
internal static class MigrationIndependentConfigurationVerifier
{
    private sealed record ExpectedCustomFieldOption(string Key, string Label, bool Enabled, int SortOrder);

    private sealed record ExpectedCustomFieldDefinition(
        string Key,
        string Type,
        string Label,
        string? HelpText,
        bool Enabled,
        int SortOrder,
        IReadOnlyList<ExpectedCustomFieldOption> Options);

    private static readonly string[] SeededSystemEmailTemplateKeys = ["suggestion_submitted"];

    private static readonly (string Source, string Target)[] PatronTextFields =
    [
        ("pageTitle", "PageTitle"),
        ("barcodeLabel", "BarcodeLabel"),
        ("pinLabel", "PinLabel"),
        ("loginPrompt", "LoginPrompt"),
        ("loginNote", "LoginNote"),
        ("suggestionFormNote", "SuggestionFormNote"),
        ("noEmailMessage", "NoEmailMessage"),
        ("successTitle", "SuccessTitle"),
        ("successMessage", "SuccessMessage"),
        ("alreadySubmittedMessage", "AlreadySubmittedMessage"),
        ("ebookMessage", "EbookMessage"),
        ("eaudiobookMessage", "EaudiobookMessage")
    ];

    private static readonly (string Source, string Target)[] DuplicateLabelFields =
    [
        ("suggestion", "SuggestionStatusLabel"),
        ("outstanding_purchase", "OutstandingPurchaseStatusLabel"),
        ("pending_hold", "PendingHoldStatusLabel"),
        ("hold_placed", "HoldPlacedStatusLabel"),
        ("closed", "ClosedStatusLabel"),
        ("rejected", "RejectedStatusLabel"),
        ("hold_completed", "HoldCompletedStatusLabel"),
        ("hold_not_picked_up", "HoldNotPickedUpStatusLabel"),
        ("manual", "ManualStatusLabel"),
        ("silent", "SilentStatusLabel")
    ];

    public static void Verify(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        bool postmarkTokenProvisioned)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var organizationIds = organizations.ToDictionary(
            row => row.RequiredString("id"),
            row => row.Int32("organizationId") ?? Fail<int>("A source organization has no native identity."),
            StringComparer.Ordinal);

        VerifySystemSettings(connection, transaction, package);
        VerifyPolarisSettings(connection, transaction, package);
        VerifyEmailSettings(connection, transaction, package, organizationIds, postmarkTokenProvisioned);
        VerifyWorkflowSettings(connection, transaction, package, organizationIds);
        VerifyPatronSettings(connection, transaction, package, organizationIds);
        VerifyEmailTemplates(connection, transaction, package, organizationIds);
        VerifyConfigurationPopulations(connection, transaction, package, organizationIds);
        VerifySystemDefaults(connection, transaction, package);
    }

    private static void VerifyConfigurationPopulations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[SystemSettings];").SetEquals([1]),
            "system settings scope population");
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[PolarisSettings];").SetEquals([1]),
            "Polaris settings scope population");

        var workflowRows = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings");
        var expectedWorkflowScopes = new HashSet<int> { 1 };
        foreach (var row in workflowRows)
        {
            expectedWorkflowScopes.Add(ResolveScopedOrganization(row, organizationIds, package));
        }
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[WorkflowSettings];").SetEquals(expectedWorkflowScopes),
            "workflow settings exact scope population");

        var uiRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings");
        var overrideRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var modernOverrideScopes = overrideRows
            .Select(row => row.Int32("orgId") ?? Fail<int>("A patron settings override has no organization identity."))
            .ToHashSet();
        var expectedPatronScopes = new HashSet<int> { 1 };
        foreach (var row in uiRows)
        {
            expectedPatronScopes.Add(ResolveScopedOrganization(row, organizationIds, package));
        }
        foreach (var organizationId in modernOverrideScopes)
        {
            expectedPatronScopes.Add(organizationId);
        }
        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"))
        {
            var organizationId = ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package);
            if (!modernOverrideScopes.Contains(organizationId))
            {
                expectedPatronScopes.Add(organizationId);
            }
        }
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[PatronSettings];").SetEquals(expectedPatronScopes),
            "patron settings exact scope population");

        var templateRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates");
        var expectedEmailScopes = new HashSet<int> { 1 };
        foreach (var group in templateRows
                     .Where(row => string.Equals(row.String("scope"), "library", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(row => ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package)))
        {
            if (group.Any(row => Meaningful(row.Text("fromAddress")) is not null || Meaningful(row.Text("fromName")) is not null))
            {
                expectedEmailScopes.Add(group.Key);
            }
        }
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[EmailSettings];").SetEquals(expectedEmailScopes),
            "email settings exact scope population");

        VerifyWorkflowSetPopulations(connection, transaction, package, organizationIds, workflowRows);
        VerifyPublicationOptionPopulations(connection, transaction, package, organizationIds, uiRows, overrideRows);
        VerifyExternalSearchPopulations(connection, transaction, package, organizationIds, workflowRows);
        VerifyEmailTemplatePopulation(connection, transaction, package);
        VerifyCustomFieldPopulations(connection, transaction, package);
    }

    private static void VerifyWorkflowSetPopulations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyList<SourceRow> workflowRows)
    {
        var expectedCreatorSets = new HashSet<int> { 1 };
        var expectedPatronCodeSets = new HashSet<int> { 1 };
        foreach (var row in workflowRows)
        {
            var organizationId = ResolveScopedOrganization(row, organizationIds, package);
            if (organizationId == 1 || SplitCommonCreatorLines(row.Text("commonAuthorsList")).Count > 0)
            {
                expectedCreatorSets.Add(organizationId);
            }
            if (organizationId == 1 || ParseExpectedPatronCodeIds(row.Text("allowedPatronCodeIds")).Length > 0)
            {
                expectedPatronCodeSets.Add(organizationId);
            }
        }
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[CommonCreatorSet];").SetEquals(expectedCreatorSets),
            "common creator exact scope population");
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[PatronCodeEligibilitySet];").SetEquals(expectedPatronCodeSets),
            "patron-code eligibility exact scope population");

        var system = workflowRows.SingleOrDefault(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase));
        VerifyOrderedSet(connection, transaction, "CommonCreatorSet", "CommonCreatorTerm", "Value",
            1, SplitCommonCreatorLines(system?.Text("commonAuthorsList")), system: true);
        var expectedCodes = ParseExpectedPatronCodeIds(system?.Text("allowedPatronCodeIds"));
        using var codeCommand = new SqlCommand(
            "SELECT [PatronCodeId] FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 1 ORDER BY [PatronCodeId];",
            connection,
            transaction);
        using var codeReader = codeCommand.ExecuteReader();
        var actualCodes = new List<int>();
        while (codeReader.Read())
        {
            actualCodes.Add(codeReader.GetInt32(0));
        }
        Ensure(expectedCodes.SequenceEqual(actualCodes), "system patron-code eligibility members");
    }

    private static void VerifyPublicationOptionPopulations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyList<SourceRow> uiRows,
        IReadOnlyList<SourceRow> overrideRows)
    {
        var expectedSets = new HashSet<int> { 1 };
        foreach (var row in overrideRows)
        {
            var organizationId = row.Int32("orgId") ?? Fail<int>("A patron settings override has no organization identity.");
            if (HasLegacyCustomFieldText(row.Text("publicationOptions")) && ParsePublicationOptions(row.Text("publicationOptions")!, isSystem: false).Count > 0)
            {
                expectedSets.Add(organizationId);
            }
        }
        Ensure(TargetOrganizationIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[PublicationOptionSet];").SetEquals(expectedSets),
            "publication option exact set population");

        var system = uiRows.SingleOrDefault(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase));
        var systemRaw = system?.Text("publicationOptions");
        if (systemRaw is null || !HasLegacyCustomFieldText(systemRaw))
        {
            var seed = new[]
            {
                new PublicationOption("already_published", "Already published", true, 10),
                new PublicationOption("coming_soon", "Coming soon", true, 20),
                new PublicationOption("published_a_while_back", "Published a while back", true, 30)
            };
            Ensure(ReadPublicationOptions(connection, transaction, 1).SequenceEqual(seed), "seeded system publication options");
        }
        else
        {
            VerifyPublicationOptions(connection, transaction, 1, systemRaw);
        }
    }

    private static void VerifyExternalSearchPopulations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyList<SourceRow> workflowRows)
    {
        var defaults = new[]
        {
            (Enabled: true, Label: "Search Amazon", Url: "https://www.amazon.com/s?k={{title}}", SortOrder: 10),
            (Enabled: true, Label: "Search Goodreads", Url: "https://www.goodreads.com/search?q={{title}}", SortOrder: 20),
            (Enabled: true, Label: "Search WorldCat", Url: "https://www.worldcat.org/search?q={{title}}", SortOrder: 30),
            (Enabled: false, Label: "", Url: "", SortOrder: 40)
        };
        var system = workflowRows.SingleOrDefault(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase));
        var actualProviders = new Dictionary<string, (int OrganizationId, bool Enabled, string Label, string Url, int SortOrder)>(StringComparer.Ordinal);
        using (var command = new SqlCommand(
                   "SELECT [OrganizationId], [ProviderKey], [IsEnabled], [Label], [UrlTemplate], [SortOrder] FROM [asap].[ExternalSearchProvider];",
                   connection,
                   transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                actualProviders.Add(reader.GetString(1), (reader.GetInt32(0), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
            }
        }
        Ensure(actualProviders.Count == 4, "exact external-search system provider population");
        for (var index = 1; index <= 4; index++)
        {
            var expectedEnabled = system?.Bool($"externalSearch{index}Enabled", defaults[index - 1].Enabled) ?? defaults[index - 1].Enabled;
            var expectedLabel = system?.Text($"externalSearch{index}Label") ?? defaults[index - 1].Label;
            var expectedUrl = system?.Text($"externalSearch{index}UrlTemplate") ?? defaults[index - 1].Url;
            Ensure(actualProviders.TryGetValue($"external_search_{index}", out var actual) &&
                actual == (1, expectedEnabled, expectedLabel, expectedUrl, defaults[index - 1].SortOrder),
                "external-search seeded/source provider identity, scope, values and order");
        }

        var expectedOverrides = new HashSet<(int LibraryId, string ProviderKey)>();
        foreach (var row in workflowRows.Where(item => string.Equals(item.String("scope"), "library", StringComparison.OrdinalIgnoreCase)))
        {
            var libraryId = ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package);
            for (var slot = 1; slot <= 4; slot++)
            {
                if (row.HasValue($"externalSearch{slot}Enabled") || row.HasValue($"externalSearch{slot}Label") || row.HasValue($"externalSearch{slot}UrlTemplate"))
                {
                    expectedOverrides.Add((libraryId, $"external_search_{slot}"));
                }
            }
        }
        var actualOverrides = new HashSet<(int LibraryId, string ProviderKey)>();
        using (var command = new SqlCommand(
                   "SELECT o.[LibraryOrganizationId], p.[ProviderKey] FROM [asap].[ExternalSearchProviderOverride] o JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId];",
                   connection,
                   transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                actualOverrides.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }
        Ensure(actualOverrides.SetEquals(expectedOverrides), "exact external-search library override population");
    }

    private static void VerifyEmailTemplatePopulation(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package)
    {
        var sourceRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates")
            .Concat(MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates"))
            .ToArray();
        var expectedIds = sourceRows
            .Select(row => ReadMapping(connection, transaction, "email_template", row.RequiredString("id")))
            .ToHashSet();
        using (var seed = new SqlCommand(
                   "SELECT [Id], [TemplateKey] FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted';",
                   connection,
                   transaction))
        {
            using var seedReader = seed.ExecuteReader();
            Ensure(seedReader.Read() && Same(seedReader, 1, "suggestion_submitted"), "seeded system suggestion template exact key");
            expectedIds.Add(seedReader.GetInt64(0));
            Ensure(!seedReader.Read(), "single seeded system suggestion template");
        }
        var sourceOverridesSeed = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates")
            .Any(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.String("templateKey"), "suggestion_submitted", StringComparison.Ordinal));
        if (!sourceOverridesSeed)
        {
            const string defaultBody = "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection.";
            using var seedCommand = new SqlCommand(
                "SELECT [TemplateKey], [SourceTemplateId], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder] FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted';",
                connection,
                transaction);
            using var seedReader = seedCommand.ExecuteReader();
            Ensure(seedReader.Read() && Same(seedReader, 0, "suggestion_submitted") && seedReader.IsDBNull(1) &&
                Same(seedReader, 2, "Suggestion submitted") && Same(seedReader, 3, "Suggestion received: {{title}}") &&
                Same(seedReader, 4, defaultBody) && !seedReader.GetBoolean(5) && !seedReader.GetBoolean(6) &&
                seedReader.GetInt32(7) == 10,
                "seeded system suggestion template values");
        }
        var actualIds = TargetLongIds(connection, transaction, "SELECT [Id] FROM [asap].[EmailTemplate];");
        Ensure(actualIds.SetEquals(expectedIds), "exact source and pinned-seed email template population");
    }

    private static void VerifyCustomFieldPopulations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var libraryIds = organizations
            .Where(row => row.Int32("organizationId") is > 1 &&
                (row.HasValue("organizationCodeId") ? row.Int32("organizationCodeId") : row.Int32("organization_code_id")) == 2)
            .Select(row => row.Int32("organizationId")!.Value)
            .ToHashSet();
        var overrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var fieldCounts = libraryIds.ToDictionary(id => id, _ => 0);
        var overrideByLibrary = new Dictionary<int, SourceRow>();
        foreach (var row in overrides)
        {
            var libraryId = row.Int32("orgId") ?? Fail<int>("A custom-field override has no organization identity.");
            Ensure(libraryIds.Contains(libraryId) && overrideByLibrary.TryAdd(libraryId, row), "unique custom-field library override");
            if (row.JsonText("additionalFieldDefinitions") is { } definitionsJson)
            {
                using var definitions = JsonDocument.Parse(definitionsJson);
                Ensure(definitions.RootElement.ValueKind == JsonValueKind.Array, "source custom-field array");
                fieldCounts[libraryId] = definitions.RootElement.GetArrayLength();
            }
        }
        foreach (var libraryId in libraryIds)
        {
            Ensure(Count(connection, transaction,
                    "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @id;", libraryId) == fieldCounts[libraryId],
                "exact custom-field population for every source library");
            var definitionCount = fieldCounts[libraryId];
            var expectedRules = definitionCount == 0
                ? 0
                : ReadEffectiveFormatRows(connection, transaction, libraryId).Count * definitionCount;
            Ensure(Count(connection, transaction,
                    "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @id;", libraryId) == expectedRules,
                "exact custom-field rule population for every source library");
        }
        Ensure(CountAll(connection, transaction, "SELECT COUNT(*) FROM [asap].[PatronCustomField];") == fieldCounts.Values.Sum(),
            "global custom-field population");
        Ensure(CountAll(connection, transaction, "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule];") == libraryIds.Sum(libraryId =>
                fieldCounts[libraryId] == 0 ? 0 : ReadEffectiveFormatRows(connection, transaction, libraryId).Count * fieldCounts[libraryId]),
            "global custom-field rule population");
    }

    private static void VerifySystemDefaults(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package)
    {
        if (MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings").Count == 0)
        {
            using var command = new SqlCommand(
                "SELECT [Host], [AccessId], [ProtectedApiKey], [StaffDomain], [AdminUser], [ProtectedAdminPassword], [WorkstationId], [SystemPolarisUserId] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;",
                connection,
                transaction);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && Enumerable.Range(0, 8).All(reader.IsDBNull), "empty seeded Polaris settings values");
        }

        var workflow = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings")
            .SingleOrDefault(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase));
        if (workflow is null)
        {
            using var command = new SqlCommand(
                "SELECT [SuggestionLimit], [SuggestionLimitMessage], [OutstandingTimeoutEnabled], [OutstandingTimeoutDays], [OutstandingTimeoutSendEmail], [OutstandingTimeoutRejectionTemplateId], [HoldPickupTimeoutEnabled], [HoldPickupTimeoutDays], [PendingHoldTimeoutEnabled], [PendingHoldTimeoutDays], [AdditionalCopyTimeoutEnabled], [AdditionalCopyTimeoutDays], [AutoPromote], [CommonAuthorsEnabled], [CommonAuthorsLabel], [CommonAuthorsHelp], [CommonAuthorsMessage], [AllowPatronAutoholdOptOut], [AllowAnyRegisteredCardLogin], [PatronCodeEligibilityEnabled], [PatronCodeEligibilityMessage], [UpdatedUtc] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 1;",
                connection,
                transaction);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && NullableInt(reader, 0) == 5 &&
                Same(reader, 1, "Weekly suggestion limit reached. You can try again after {{next_available_date}}.") &&
                NullableBoolean(reader, 2) == false && NullableInt(reader, 3) == 30 && NullableBoolean(reader, 4) == false && reader.IsDBNull(5) &&
                NullableBoolean(reader, 6) == false && NullableInt(reader, 7) == 14 && NullableBoolean(reader, 8) == false && NullableInt(reader, 9) == 14 &&
                NullableBoolean(reader, 10) == false && NullableInt(reader, 11) == 14 && NullableBoolean(reader, 12) == false &&
                NullableBoolean(reader, 13) == false && Same(reader, 14, "Popular Creators") &&
                Same(reader, 15, "See if this is a creator we already collect.") &&
                Same(reader, 16, "We automatically purchase all upcoming titles by this creator. Please check the catalog to place a hold on 'On Order' items.") &&
                NullableBoolean(reader, 17) == true && NullableBoolean(reader, 18) == false && NullableBoolean(reader, 19) == false &&
                Same(reader, 20, "Your patron account is not eligible to submit suggestions.") &&
                Same(reader, 21, package.Manifest.ExportedAtUtc.UtcDateTime),
                "seeded system workflow settings defaults");
        }

        var uiSystem = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings")
            .SingleOrDefault(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase));
        if (uiSystem is null)
        {
            using var command = new SqlCommand(
                "SELECT [PageTitle], [BarcodeLabel], [PinLabel], [LoginPrompt], [LoginNote], [SuggestionFormNote], [NoEmailMessage], [SuccessTitle], [SuccessMessage], [AlreadySubmittedMessage], [EbookMessage], [EaudiobookMessage], [SuggestionStatusLabel], [OutstandingPurchaseStatusLabel], [PendingHoldStatusLabel], [HoldPlacedStatusLabel], [ClosedStatusLabel], [RejectedStatusLabel], [HoldCompletedStatusLabel], [HoldNotPickedUpStatusLabel], [ManualStatusLabel], [SilentStatusLabel], [UpdatedUtc] FROM [asap].[PatronSettings] WHERE [OrganizationId] = 1;",
                connection,
                transaction);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && Same(reader, 0, "Material Suggestion") && Same(reader, 1, "Library Card") && Same(reader, 2, "Pin") &&
                Same(reader, 3, string.Empty) && Same(reader, 4, string.Empty) && Same(reader, 5, string.Empty) &&
                Same(reader, 6, "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.") &&
                Same(reader, 7, "Suggestion Submitted") &&
                Same(reader, 8, "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>") &&
                Same(reader, 9, "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>") &&
                Same(reader, 10, "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>") &&
                Same(reader, 11, "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>") &&
                Same(reader, 12, "Received") && Same(reader, 13, "Under review") && Same(reader, 14, "Being prepared") &&
                Same(reader, 15, "Hold placed") && Same(reader, 16, "Completed") && Same(reader, 17, "Not selected for purchase") &&
                Same(reader, 18, "Completed") && Same(reader, 19, "Closed") && Same(reader, 20, "Closed") && Same(reader, 21, "Closed") &&
                Same(reader, 22, package.Manifest.ExportedAtUtc.UtcDateTime),
                "seeded system patron settings defaults");
        }
    }

    private static void VerifySystemSettings(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package)
    {
        var source = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings").SingleOrDefault();
        var uiSystem = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings")
            .SingleOrDefault(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase));
        var runtime = MigrationPackageReader.ReadMetadata(package, "effective-legacy-runtime-config.json");
        var settings = runtime.GetProperty("settings");
        var expectedStaffUrl = settings.GetProperty("StaffApplicationUrl").GetProperty("value").GetString();
        var expectedIconPattern = settings.GetProperty("MaterialTypeIconUrlPattern").GetProperty("value").GetString();
        const string defaultSystemNotEnabledMessage = "{{library}} does not currently participate in this suggestion service.";
        const string defaultMisconfiguredMessage = "The {{library}} suggestion system is currently misconfigured. Please contact staff.";
        var expectedSystemNotEnabledMessage = uiSystem?.Text("systemNotEnabledMessage") ?? defaultSystemNotEnabledMessage;
        using (var command = new SqlCommand(
                   "SELECT [StaffApplicationUrl], [LeapBibUrlPattern], [LeapPatronUrlPattern], [MaterialTypeIconUrlPattern], [SystemNotEnabledMessage], [MisconfiguredMessage], [UpdatedUtc] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;",
                   connection,
                   transaction))
        using (var reader = command.ExecuteReader())
        {
            var expectedUpdated = uiSystem is not null
                ? uiSystem.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime
                : source?.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime;
            Ensure(reader.Read(), "system settings row");
            Ensure(
                Same(reader, 0, expectedStaffUrl) &&
                Same(reader, 1, source?.String("leapBibUrlPattern")) &&
                Same(reader, 2, source?.String("leapPatronUrlPattern")) &&
                Same(reader, 3, expectedIconPattern) &&
                Same(reader, 4, expectedSystemNotEnabledMessage) &&
                Same(reader, 5, defaultMisconfiguredMessage) &&
                Same(reader, 6, expectedUpdated),
                "system settings source fields and imported timestamp precedence");
        }

        var expectedOrigins = Split(source?.String("patronEmbedAllowedOrigins"))
            .Select(NormalizeOriginForOracle)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(origin => (OrganizationId: 1, Origin: origin, CreatedUtc: package.Manifest.ExportedAtUtc.UtcDateTime))
            .ToArray();
        using var originsCommand = new SqlCommand(
            "SELECT [OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc] FROM [asap].[PatronEmbedAllowedOrigin];",
            connection,
            transaction);
        using var originsReader = originsCommand.ExecuteReader();
        var actualOrigins = new List<(int OrganizationId, string Origin, string Normalized, DateTime CreatedUtc)>();
        while (originsReader.Read())
        {
            actualOrigins.Add((originsReader.GetInt32(0), originsReader.GetString(1), originsReader.GetString(2), originsReader.GetDateTime(3)));
        }
        actualOrigins = actualOrigins
            .OrderBy(item => item.OrganizationId)
            .ThenBy(item => item.Normalized, StringComparer.Ordinal)
            .ToList();
        Ensure(
            actualOrigins.Count == expectedOrigins.Length &&
            actualOrigins.Zip(expectedOrigins).All(pair =>
                pair.First.OrganizationId == pair.Second.OrganizationId &&
                string.Equals(pair.First.Origin, pair.Second.Origin, StringComparison.Ordinal) &&
                string.Equals(pair.First.Normalized, pair.Second.Origin, StringComparison.Ordinal) &&
                pair.First.CreatedUtc.Ticks == pair.Second.CreatedUtc.Ticks),
            "complete patron embed origin set and package timestamp");
    }

    private static string NormalizeOriginForOracle(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace) ||
            candidate.IndexOfAny(['"', '\'', '`', ';', '\\']) >= 0)
        {
            return Fail<string>("A source patron embed origin is invalid.");
        }

        var wildcard = Regex.Match(candidate, "^(https?)://\\*\\.(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (wildcard.Success)
        {
            var authority = wildcard.Groups[2].Value;
            var wildcardAuthorityMatch = Regex.Match(authority,
                "^(?<host>[a-z0-9.-]+)(?::(?<port>[0-9]+))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!string.Equals(wildcard.Groups[1].Value, "https", StringComparison.OrdinalIgnoreCase) ||
                !wildcardAuthorityMatch.Success ||
                !IsValidExplicitPort(wildcardAuthorityMatch) ||
                !IsDnsHostname(wildcardAuthorityMatch.Groups["host"].Value) ||
                !wildcardAuthorityMatch.Groups["host"].Value.TrimEnd('.').Contains('.') ||
                !Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var wildcardUri) ||
                wildcardUri.HostNameType != UriHostNameType.Dns ||
                !IsDnsHostname(wildcardUri.Host) ||
                wildcardUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(wildcardUri.Query) ||
                !string.IsNullOrEmpty(wildcardUri.Fragment) ||
                !string.IsNullOrEmpty(wildcardUri.UserInfo) ||
                wildcardUri.Port is < 0 or > 65535)
            {
                return Fail<string>("A source patron embed wildcard origin is invalid.");
            }
            return $"https://*.{authority.ToLowerInvariant()}";
        }

        var plain = Regex.Match(candidate, "^(https?)://([^/?#]+)(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!plain.Success || plain.Groups[3].Value.Length > 0 && plain.Groups[3].Value != "/")
        {
            return Fail<string>("A source patron embed origin is invalid.");
        }
        var protocol = plain.Groups[1].Value.ToLowerInvariant();
        var authorityText = plain.Groups[2].Value;
        var authorityMatch = Regex.Match(authorityText,
            "^(?<host>[a-z0-9.-]+|\\[[0-9a-f:.]+\\])(?::(?<port>[0-9]+))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var rawHost = authorityMatch.Success ? authorityMatch.Groups["host"].Value : string.Empty;
        var ipv6Authority = rawHost.StartsWith("[", StringComparison.Ordinal);
        var unbracketedHost = ipv6Authority ? rawHost[1..^1] : rawHost;
        if (!authorityMatch.Success ||
            !IsValidExplicitPort(authorityMatch) ||
            authorityText.Contains('@') ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Scheme != protocol ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is < 0 or > 65535 ||
            (ipv6Authority && uri.HostNameType != UriHostNameType.IPv6) ||
            (!ipv6Authority && (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
                uri.HostNameType == UriHostNameType.Dns && !IsDnsHostname(unbracketedHost))))
        {
            return Fail<string>("A source patron embed origin authority is invalid.");
        }
        var host = unbracketedHost.ToLowerInvariant();
        var local = host is "localhost" or "127.0.0.1" or "::1";
        if (protocol != "https" && !(protocol == "http" && local))
        {
            return Fail<string>("Only HTTPS or local HTTP embed origins are allowed.");
        }
        return $"{protocol}://{authorityText.ToLowerInvariant()}";
    }

    private static bool IsValidExplicitPort(Match authority)
    {
        if (!authority.Groups["port"].Success)
        {
            return true;
        }
        return int.TryParse(authority.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is >= 0 and <= 65535;
    }

    private static bool IsDnsHostname(string host)
    {
        var value = host.EndsWith(".", StringComparison.Ordinal) ? host[..^1] : host;
        if (value.Length is 0 or > 253)
        {
            return false;
        }
        return value.Split('.').All(label => label.Length is > 0 and <= 63 &&
            IsAsciiAlphaNumeric(label[0]) && IsAsciiAlphaNumeric(label[^1]) &&
            label.All(character => IsAsciiAlphaNumeric(character) || character == '-'));
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static void VerifyPolarisSettings(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package)
    {
        var rows = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings");
        Ensure(rows.Count <= 1, "single Polaris settings source row");
        var source = rows.SingleOrDefault();
        using var command = new SqlCommand(
            "SELECT [Host], [AccessId], [ProtectedApiKey], [StaffDomain], [AdminUser], [ProtectedAdminPassword], [WorkstationId], [SystemPolarisUserId], [UpdatedUtc] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        Ensure(reader.Read(), "Polaris settings row");
        Ensure(
            Same(reader, 0, source?.String("host")) &&
            Same(reader, 1, source?.String("accessId")) &&
            reader.IsDBNull(2) == (source?.String("apiKey") is null) &&
            Same(reader, 3, source?.String("staffDomain")) &&
            Same(reader, 4, source?.String("adminUser")) &&
            reader.IsDBNull(5) == (source?.String("adminPassword") is null) &&
            NullableInt(reader, 6) == source?.PositiveInt32("workstationId", "source_polaris_identity_invalid") &&
            NullableInt(reader, 7) == source?.PositiveInt32("userId", "source_polaris_identity_invalid") &&
            Same(reader, 8, source?.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime),
            "Polaris source fields, protected-secret presence and imported timestamp");
    }

    private static void VerifyEmailSettings(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        bool postmarkTokenProvisioned)
    {
        var smtpRows = MigrationPackageReader.ReadRows(package, "email-settings.json", "smtp_settings");
        var templateRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates");
        var systemTemplates = templateRows
            .Where(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expectedAddress = ResolveSender(systemTemplates, "fromAddress") ?? Meaningful(smtpRows.SingleOrDefault()?.Text("fromAddress"));
        var expectedName = ResolveSender(systemTemplates, "fromName") ?? Meaningful(smtpRows.SingleOrDefault()?.Text("fromName"));
        using (var command = new SqlCommand(
                   "SELECT [ProtectedServerToken], [FromAddress], [FromName], [UpdatedUtc] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1;",
                   connection,
                   transaction))
        using (var reader = command.ExecuteReader())
        {
            Ensure(reader.Read(), "system email settings row");
            Ensure(
                !reader.IsDBNull(0) == postmarkTokenProvisioned &&
                Same(reader, 1, expectedAddress) &&
                Same(reader, 2, expectedName) &&
                Same(reader, 3, smtpRows.SingleOrDefault()?.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime),
                "system email sender, operator-secret presence and imported timestamp");
        }

        var libraryTemplates = templateRows
            .Where(row => string.Equals(row.String("scope"), "library", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package),
                EqualityComparer<int>.Default);
        foreach (var group in libraryTemplates)
        {
            var address = ResolveSender(group.ToArray(), "fromAddress");
            var name = ResolveSender(group.ToArray(), "fromName");
            if (address is null && name is null)
            {
                continue;
            }
            using var command = new SqlCommand(
                "SELECT [FromAddress], [FromName], [UpdatedUtc] FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", group.Key);
            using var reader = command.ExecuteReader();
            var expectedUpdated = group.Select(row => row.UtcDateTime("updated"))
                .Where(value => value.HasValue)
                .OrderBy(value => value)
                .FirstOrDefault() ?? package.Manifest.ExportedAtUtc.UtcDateTime;
            Ensure(reader.Read() && Same(reader, 0, address) && Same(reader, 1, name) &&
                Same(reader, 2, expectedUpdated), "library email sender settings and imported timestamp");
        }
    }

    private static string? ResolveSender(IReadOnlyList<SourceRow> rows, string field)
    {
        var candidates = rows.Select(row => row.Text(field))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Ensure(candidates.Length <= 1, "unambiguous source sender");
        return candidates.SingleOrDefault();
    }

    private static string? Meaningful(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void VerifyWorkflowSettings(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var rows = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings");
        var templates = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates")
            .Concat(MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates"))
            .ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var rejectionTemplateIds = MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates")
            .Select(row => row.RequiredString("id"))
            .ToHashSet(StringComparer.Ordinal);
        var templateTargetIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var sourceId in templates.Keys)
        {
            templateTargetIds.Add(sourceId, ReadMapping(connection, transaction, "email_template", sourceId));
        }

        foreach (var row in rows)
        {
            var organizationId = ResolveScopedOrganization(row, organizationIds, package);
            var system = organizationId == 1;
            var sourceTemplateId = row.String("outstandingTimeoutRejectionTemplate");
            var targetTemplateId = sourceTemplateId is null
                ? (long?)null
                : templateTargetIds.TryGetValue(sourceTemplateId, out var mapped)
                    ? mapped
                    : Fail<long?>("A source workflow rejection template is unresolved.");
            if (sourceTemplateId is not null)
            {
                Ensure(templates.TryGetValue(sourceTemplateId, out var sourceTemplate), "workflow template source row exists");
                Ensure(rejectionTemplateIds.Contains(sourceTemplateId), "workflow selects a rejection-template collection row");
                var templateScope = sourceTemplate!.RequiredString("scope").Trim().ToLowerInvariant();
                var templateOrganizationId = templateScope == "system"
                    ? 1
                    : templateScope == "library"
                        ? ResolveLibrary(sourceTemplate.RequiredString("libraryOrganization"), organizationIds, package)
                        : Fail<int>("A workflow template has an invalid source scope.");
                Ensure(templateOrganizationId == 1 || templateOrganizationId == organizationId,
                    "workflow rejection template is in the effective source scope");
                var sourceTemplateKey = "rejection:" + (templateScope == "library"
                    ? sourceTemplate.String("sourceTemplateId") ?? sourceTemplateId
                    : sourceTemplateId);
                Ensure(sourceTemplateKey.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase),
                    "workflow selects a rejection template");
                VerifyEffectiveWorkflowTemplate(connection, transaction, targetTemplateId!.Value, organizationId);
            }
            using (var command = new SqlCommand(
                       "SELECT [SuggestionLimit], [SuggestionLimitMessage], [OutstandingTimeoutEnabled], [OutstandingTimeoutDays], [OutstandingTimeoutSendEmail], [OutstandingTimeoutRejectionTemplateId], [HoldPickupTimeoutEnabled], [HoldPickupTimeoutDays], [PendingHoldTimeoutEnabled], [PendingHoldTimeoutDays], [AdditionalCopyTimeoutEnabled], [AdditionalCopyTimeoutDays], [AutoPromote], [CommonAuthorsEnabled], [CommonAuthorsLabel], [CommonAuthorsHelp], [CommonAuthorsMessage], [AllowPatronAutoholdOptOut], [AllowAnyRegisteredCardLogin], [PatronCodeEligibilityEnabled], [PatronCodeEligibilityMessage], [UpdatedUtc] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @organizationId;",
                       connection,
                       transaction))
            {
                command.Parameters.AddWithValue("@organizationId", organizationId);
                using var reader = command.ExecuteReader();
                Ensure(reader.Read(), "workflow settings row");
                Ensure(
                    NullableInt(reader, 0) == (system ? row.Int32("suggestionLimit") ?? 5 : row.Int32("suggestionLimit")) &&
                    Same(reader, 1, ScopedText(row, "suggestionLimitMessage", system)) &&
                NullableBoolean(reader, 2) == (system ? row.Bool("outstandingTimeoutEnabled", false) : row.NullableBool("outstandingTimeoutEnabled")) &&
                    NullableInt(reader, 3) == (system ? row.Int32("outstandingTimeoutDays") ?? 30 : row.Int32("outstandingTimeoutDays")) &&
                NullableBoolean(reader, 4) == (system ? row.Bool("outstandingTimeoutSendEmail", false) : row.NullableBool("outstandingTimeoutSendEmail")) &&
                    NullableLong(reader, 5) == targetTemplateId &&
                NullableBoolean(reader, 6) == (system ? row.Bool("holdPickupTimeoutEnabled", false) : row.NullableBool("holdPickupTimeoutEnabled")) &&
                    NullableInt(reader, 7) == (system ? row.Int32("holdPickupTimeoutDays") ?? 14 : row.Int32("holdPickupTimeoutDays")) &&
                NullableBoolean(reader, 8) == (system ? row.Bool("pendingHoldTimeoutEnabled", false) : row.NullableBool("pendingHoldTimeoutEnabled")) &&
                    NullableInt(reader, 9) == (system ? row.Int32("pendingHoldTimeoutDays") ?? 14 : row.Int32("pendingHoldTimeoutDays")) &&
                NullableBoolean(reader, 10) == (system ? row.Bool("additionalCopyTimeoutEnabled", false) : row.NullableBool("additionalCopyTimeoutEnabled")) &&
                    NullableInt(reader, 11) == (system ? row.Int32("additionalCopyTimeoutDays") ?? 14 : row.Int32("additionalCopyTimeoutDays")) &&
                NullableBoolean(reader, 12) == (system ? row.Bool("autoPromote", false) : row.NullableBool("autoPromote")) &&
                NullableBoolean(reader, 13) == (system ? row.Bool("commonAuthorsEnabled", false) : row.NullableBool("commonAuthorsEnabled")) &&
                    Same(reader, 14, ScopedText(row, "commonAuthorsLabel", system)) &&
                    Same(reader, 15, ScopedText(row, "commonAuthorsHelp", system)) &&
                    Same(reader, 16, ScopedText(row, "commonAuthorsMessage", system)) &&
                NullableBoolean(reader, 17) == (system ? row.Bool("allowPatronAutoholdOptOut", true) : row.NullableBool("allowPatronAutoholdOptOut")) &&
                NullableBoolean(reader, 18) == (system ? row.Bool("allowAnyRegisteredCardLogin", false) : row.NullableBool("allowAnyRegisteredCardLogin")) &&
                NullableBoolean(reader, 19) == (system ? row.Bool("patronCodeEligibilityEnabled", false) : row.NullableBool("patronCodeEligibilityEnabled")) &&
                    Same(reader, 20, ScopedText(row, "patronCodeEligibilityMessage", system)) &&
                    Same(reader, 21, row.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime),
                    "workflow setting values, nullability and imported timestamp");
            }
            VerifyOrderedSet(connection, transaction, "CommonCreatorSet", "CommonCreatorTerm", "Value",
                organizationId, SplitCommonCreatorLines(row.Text("commonAuthorsList")), system);
            VerifyPatronCodeSet(connection, transaction, organizationId, row.Text("allowedPatronCodeIds"), system);
            VerifyExternalSearch(connection, transaction, organizationId, row, system);
        }
        if (!rows.Any(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase)))
        {
            using var seed = new SqlCommand(
                "SELECT [UpdatedUtc] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 1;",
                connection,
                transaction);
            using var seedReader = seed.ExecuteReader();
            Ensure(seedReader.Read() && Same(seedReader, 0, package.Manifest.ExportedAtUtc.UtcDateTime),
                "seeded system workflow timestamp");
        }
    }

    private static void VerifyEffectiveWorkflowTemplate(
        SqlConnection connection,
        SqlTransaction? transaction,
        long selectedTemplateId,
        int organizationId)
    {
        var selected = ReadTemplateTarget(connection, transaction, selectedTemplateId);
        Ensure(selected.OrganizationId == 1 || selected.OrganizationId == organizationId,
            "selected rejection template target ownership");

        TemplateTarget baseTemplate;
        TemplateTarget? overrideTemplate = null;
        if (selected.OrganizationId == 1)
        {
            baseTemplate = selected;
            if (organizationId != 1)
            {
                using var command = new SqlCommand(
                    "SELECT [Id] FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @organizationId AND [SourceTemplateId] = @sourceTemplateId;",
                    connection,
                    transaction);
                command.Parameters.AddWithValue("@organizationId", organizationId);
                command.Parameters.AddWithValue("@sourceTemplateId", selectedTemplateId);
                var value = command.ExecuteScalar();
                if (value is not null and not DBNull)
                {
                    overrideTemplate = ReadTemplateTarget(connection, transaction, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                }
            }
        }
        else if (selected.SourceTemplateId is { } sourceTemplateId)
        {
            baseTemplate = ReadTemplateTarget(connection, transaction, sourceTemplateId);
            overrideTemplate = selected;
        }
        else
        {
            baseTemplate = selected;
        }

        var isHidden = baseTemplate.IsHidden || overrideTemplate?.IsHidden == true;
        var subject = Meaningful(overrideTemplate?.Subject) ?? Meaningful(baseTemplate.Subject);
        var body = Meaningful(overrideTemplate?.Body) ?? Meaningful(baseTemplate.Body);
        Ensure(baseTemplate.TemplateKey.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase) &&
            !isHidden && subject is not null && body is not null,
            "effective timeout rejection template availability and content");
    }

    private static TemplateTarget ReadTemplateTarget(SqlConnection connection, SqlTransaction? transaction, long templateId)
    {
        using var command = new SqlCommand(
            "SELECT [Id], [OrganizationId], [TemplateKey], [SourceTemplateId], [IsHidden], [SubjectTemplate], [BodyTemplate] FROM [asap].[EmailTemplate] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", templateId);
        using var reader = command.ExecuteReader();
        Ensure(reader.Read(), "selected rejection template target exists");
        return new TemplateTarget(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            NullableLong(reader, 3),
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static void VerifyOrderedSet(
        SqlConnection connection,
        SqlTransaction? transaction,
        string setTable,
        string memberTable,
        string valueColumn,
        int organizationId,
        IReadOnlyList<string> sourceValues,
        bool system)
    {
        var existsExpected = system || sourceValues.Count > 0;
        using (var count = new SqlCommand($"SELECT COUNT(*) FROM [asap].[{setTable}] WHERE [OrganizationId] = @id;", connection, transaction))
        {
            count.Parameters.AddWithValue("@id", organizationId);
            Ensure(Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) == (existsExpected ? 1 : 0), $"{setTable} population");
        }
        using var command = new SqlCommand(
            $"SELECT [{valueColumn}], [SortOrder] FROM [asap].[{memberTable}] WHERE [OrganizationId] = @id ORDER BY [SortOrder], [Id];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        using var reader = command.ExecuteReader();
        var actual = new List<(string Value, int SortOrder)>();
        while (reader.Read())
        {
            actual.Add((reader.GetString(0), reader.GetInt32(1)));
        }
        var expected = sourceValues.Select((value, index) => (Value: value, SortOrder: (index + 1) * 10)).ToArray();
        Ensure(expected.SequenceEqual(actual), $"{setTable} members and source-derived sort order");
    }

    private static void VerifyPatronCodeSet(
        SqlConnection connection,
        SqlTransaction? transaction,
        int organizationId,
        string? source,
        bool system)
    {
        var expected = ParseExpectedPatronCodeIds(source);
        var setExpected = system || expected.Length > 0;
        using (var count = new SqlCommand("SELECT COUNT(*) FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @id;", connection, transaction))
        {
            count.Parameters.AddWithValue("@id", organizationId);
            Ensure(Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) == (setExpected ? 1 : 0), "patron-code eligibility set population");
        }
        using var command = new SqlCommand(
            "SELECT [PatronCodeId] FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @id ORDER BY [PatronCodeId];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        using var reader = command.ExecuteReader();
        var actual = new List<int>();
        while (reader.Read())
        {
            actual.Add(reader.GetInt32(0));
        }
        Ensure(expected.SequenceEqual(actual), "patron-code eligibility members");
    }

    private static void VerifyExternalSearch(
        SqlConnection connection,
        SqlTransaction? transaction,
        int organizationId,
        SourceRow source,
        bool system)
    {
        var defaults = new[]
        {
            (Enabled: true, Label: "Search Amazon", Url: "https://www.amazon.com/s?k={{title}}"),
            (Enabled: true, Label: "Search Goodreads", Url: "https://www.goodreads.com/search?q={{title}}"),
            (Enabled: true, Label: "Search WorldCat", Url: "https://www.worldcat.org/search?q={{title}}"),
            (Enabled: false, Label: "", Url: "")
        };
        for (var slot = 1; slot <= 4; slot++)
        {
            var enabledName = $"externalSearch{slot}Enabled";
            var labelName = $"externalSearch{slot}Label";
            var urlName = $"externalSearch{slot}UrlTemplate";
            if (!system && !source.HasValue(enabledName) && !source.HasValue(labelName) && !source.HasValue(urlName))
            {
                continue;
            }
            var expectedEnabled = system ? source.Bool(enabledName, defaults[slot - 1].Enabled) : source.NullableBool(enabledName);
            var expectedLabel = system ? source.Text(labelName) ?? defaults[slot - 1].Label : ScopedText(source, labelName, isSystem: false);
            var expectedUrl = system ? source.Text(urlName) ?? defaults[slot - 1].Url : ScopedText(source, urlName, isSystem: false);
            var providerKey = $"external_search_{slot}";
            using var command = new SqlCommand(
                system
                    ? "SELECT [IsEnabled], [Label], [UrlTemplate] FROM [asap].[ExternalSearchProvider] WHERE [ProviderKey] = @providerKey;"
                    : "SELECT o.[IsEnabled], o.[Label], o.[UrlTemplate] FROM [asap].[ExternalSearchProvider] p LEFT JOIN [asap].[ExternalSearchProviderOverride] o ON o.[ExternalSearchProviderId] = p.[Id] AND o.[LibraryOrganizationId] = @organizationId WHERE p.[ProviderKey] = @providerKey;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@providerKey", providerKey);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && NullableBoolean(reader, 0) == expectedEnabled && Same(reader, 1, expectedLabel) && Same(reader, 2, expectedUrl), "external search provider fields");
        }
    }

    private static void VerifyPatronSettings(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var expected = new Dictionary<int, Dictionary<string, string?>>();
        var expectedUpdated = new Dictionary<int, DateTime>();
        var uiRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings");
        var publicationSources = new Dictionary<int, string>(EqualityComparer<int>.Default);
        foreach (var row in uiRows)
        {
            var organizationId = ResolveScopedOrganization(row, organizationIds, package);
            var isSystem = organizationId == 1;
            expectedUpdated[organizationId] = row.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime;
            var values = GetExpectedPatronValues(expected, organizationId);
            foreach (var (source, target) in PatronTextFields)
            {
                if (isSystem || row.HasValue(source))
                {
                    values[target] = ScopedText(row, source, isSystem);
                }
            }
            if (isSystem)
            {
                AddDuplicateLabels(values, row, null, fromSystemRow: true);
                if (row.HasValue("systemNotEnabledMessage"))
                {
                    using var message = new SqlCommand(
                        "SELECT [SystemNotEnabledMessage] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;",
                        connection,
                        transaction);
                    using var messageReader = message.ExecuteReader();
                    Ensure(messageReader.Read() && Same(messageReader, 0,
                        row.Text("systemNotEnabledMessage") ?? "{{library}} does not currently participate in this suggestion service."),
                        "system patron not-enabled message");
                }
            }
            if (isSystem && HasLegacyCustomFieldText(row.Text("publicationOptions")))
            {
                publicationSources[organizationId] = row.Text("publicationOptions")!;
            }
        }

        var overrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var modernOverrideOrganizations = new HashSet<int>();
        foreach (var row in overrides)
        {
            var organizationId = row.Int32("orgId") ?? Fail<int>("A patron settings override lacks a native organization identity.");
            Ensure(organizationId != 1 && IsSourceLibrary(organizationId, package), "patron settings override library ownership");
            modernOverrideOrganizations.Add(organizationId);
            expectedUpdated[organizationId] = row.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime;
            var values = GetExpectedPatronValues(expected, organizationId);
            values["EbookMessage"] = ScopedText(row, "ebookMessage", isSystem: false);
            values["EaudiobookMessage"] = ScopedText(row, "eaudiobookMessage", isSystem: false);
            AddDuplicateLabels(values, null, ParseStringObject(row.JsonText("duplicateStatusLabels"), modernOverride: true), fromSystemRow: false);
            if (HasLegacyCustomFieldText(row.Text("publicationOptions")))
            {
                publicationSources[organizationId] = row.Text("publicationOptions")!;
            }
            VerifyCustomFieldDefinitions(connection, transaction, package, organizationIds, organizationId, row);
        }

        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"))
        {
            var organizationId = ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package);
            if (modernOverrideOrganizations.Contains(organizationId))
            {
                continue;
            }
            expectedUpdated[organizationId] = row.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime;
            AddDuplicateLabels(
                GetExpectedPatronValues(expected, organizationId),
                null,
                ParseStringObject(row.JsonText("duplicateRequestStatusLabels"), modernOverride: false),
                fromSystemRow: false);
        }

        foreach (var (organizationId, values) in expected)
        {
            using var command = new SqlCommand(
                "SELECT [PageTitle], [BarcodeLabel], [PinLabel], [LoginPrompt], [LoginNote], [SuggestionFormNote], [NoEmailMessage], [SuccessTitle], [SuccessMessage], [AlreadySubmittedMessage], [EbookMessage], [EaudiobookMessage], [SuggestionStatusLabel], [OutstandingPurchaseStatusLabel], [PendingHoldStatusLabel], [HoldPlacedStatusLabel], [ClosedStatusLabel], [RejectedStatusLabel], [HoldCompletedStatusLabel], [HoldNotPickedUpStatusLabel], [ManualStatusLabel], [SilentStatusLabel], [UpdatedUtc] FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read(), "patron settings row");
            var targetColumns = new[]
            {
                "PageTitle", "BarcodeLabel", "PinLabel", "LoginPrompt", "LoginNote", "SuggestionFormNote",
                "NoEmailMessage", "SuccessTitle", "SuccessMessage", "AlreadySubmittedMessage", "EbookMessage",
                "EaudiobookMessage", "SuggestionStatusLabel", "OutstandingPurchaseStatusLabel",
                "PendingHoldStatusLabel", "HoldPlacedStatusLabel", "ClosedStatusLabel", "RejectedStatusLabel",
                "HoldCompletedStatusLabel", "HoldNotPickedUpStatusLabel", "ManualStatusLabel", "SilentStatusLabel"
            };
            var ordinalByName = targetColumns.Select((name, index) => (name, index))
                .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
            foreach (var (target, value) in values)
            {
                Ensure(Same(reader, ordinalByName[target], value), "patron settings source value");
            }
            Ensure(Same(reader, 22, expectedUpdated[organizationId]), "patron settings imported timestamp precedence");
        }

        foreach (var (organizationId, raw) in publicationSources)
        {
            VerifyPublicationOptions(connection, transaction, organizationId, raw);
        }
    }

    private static Dictionary<string, string?> GetExpectedPatronValues(
        IDictionary<int, Dictionary<string, string?>> expected,
        int organizationId)
    {
        if (!expected.TryGetValue(organizationId, out var values))
        {
            values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (_, target) in PatronTextFields.Concat(DuplicateLabelFields))
            {
                values.Add(target, null);
            }
            expected.Add(organizationId, values);
        }
        return values;
    }

    private static void AddDuplicateLabels(
        IDictionary<string, string?> target,
        SourceRow? row,
        IReadOnlyDictionary<string, string>? labels,
        bool fromSystemRow)
    {
        foreach (var (key, column) in DuplicateLabelFields)
        {
            string rowName = key switch
            {
                "suggestion" => "duplicateLabelSuggestion",
                "outstanding_purchase" => "duplicateLabelOutstandingPurchase",
                "pending_hold" => "duplicateLabelPendingHold",
                "hold_placed" => "duplicateLabelHoldPlaced",
                "closed" => "duplicateLabelClosed",
                "rejected" => "duplicateLabelRejected",
                "hold_completed" => "duplicateLabelHoldCompleted",
                "hold_not_picked_up" => "duplicateLabelHoldNotPickedUp",
                "manual" => "duplicateLabelManual",
                "silent" => "duplicateLabelSilent",
                _ => Fail<string>("A duplicate status label mapping is unknown.")
            };
            if (labels is not null)
            {
                var labelKey = key == "silent" ? "Silently Closed" : key;
                target[column] = labels.GetValueOrDefault(labelKey);
            }
            else if (fromSystemRow)
            {
                target[column] = row?.Text(rowName);
            }
            else
            {
                target[column] = null;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseStringObject(string? json, bool modernOverride)
    {
        if (json is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        using var document = JsonDocument.Parse(json);
        Ensure(document.RootElement.ValueKind == JsonValueKind.Object, "source label object shape");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                Ensure(values.TryAdd(property.Name, property.Value.GetString()!), "unique source label key");
            }
            else if (property.Value.ValueKind != JsonValueKind.Null)
            {
                Fail("A source status-label value must be a string or null.");
            }
        }
        if (modernOverride && !values.Values.Any(value => TrimLegacyCustomFieldText(value).Length > 0))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        Ensure(values.Values.All(value =>
                TrimLegacyCustomFieldText(value).Length > 0 && !string.IsNullOrWhiteSpace(value)),
            "duplicate status labels with target-invisible whitespace are unsupported");
        return values;
    }

    private static void VerifyPublicationOptions(
        SqlConnection connection,
        SqlTransaction? transaction,
        int organizationId,
        string raw)
    {
        var expected = ParsePublicationOptions(raw, isSystem: organizationId == 1);
        var expectsSet = organizationId == 1 || expected.Count > 0;
        using (var set = new SqlCommand("SELECT COUNT(*) FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @id;", connection, transaction))
        {
            set.Parameters.AddWithValue("@id", organizationId);
            Ensure(Convert.ToInt32(set.ExecuteScalar(), CultureInfo.InvariantCulture) == (expectsSet ? 1 : 0), "publication option set population");
        }
        using var command = new SqlCommand(
            "SELECT [OptionKey], [Label], [IsEnabled], [SortOrder] FROM [asap].[PublicationOption] WHERE [OrganizationId] = @id ORDER BY [SortOrder], [Id];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        using var reader = command.ExecuteReader();
        var actual = new List<PublicationOption>();
        while (reader.Read())
        {
            actual.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3)));
        }
        Ensure(expected.SequenceEqual(actual), "publication option exact identity and values");
    }

    private static IReadOnlyList<PublicationOption> ParsePublicationOptions(string raw, bool isSystem)
    {
        var result = new List<PublicationOption>();
        var trimmed = TrimLegacyCustomFieldText(raw);
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(trimmed);
            Ensure(document.RootElement.ValueKind == JsonValueKind.Array, "publication option array");
            var index = 0;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                string label;
                string? id = null;
                var enabled = true;
                int? sortOrder = null;
                if (item.ValueKind == JsonValueKind.String)
                {
                    label = item.GetString() ?? string.Empty;
                }
                else
                {
                    Ensure(item.ValueKind == JsonValueKind.Object, "publication option item type");
                    label = ReadPublicationOptionLabel(item);
                    id = ReadPublicationOptionId(item);
                    enabled = JsonBooleanStrict(item, "enabled", defaultValue: true);
                    sortOrder = JsonIntegerStrict(item, "sortOrder");
                }
                label = TrimLegacyCustomFieldText(label);
                Ensure(label.Length > 0 && seenLabels.Add(label), "unique nonblank publication labels");
                var key = id is null ? PublicationKey(label, $"option_{index + 1}") : id;
                Ensure(key.Length > 0 && seenIds.Add(key), "unique publication option IDs");
                result.Add(new(key, label, enabled, sortOrder is null or 0 ? (index + 1) * 10 : sortOrder.Value));
                index++;
            }
        }
        else
        {
            var labels = trimmed.Split('\n')
                .Select(TrimLegacyCustomFieldText)
                .Where(label => label.Length > 0)
                .ToArray();
            for (var index = 0; index < labels.Length; index++)
            {
                var label = labels[index];
                var key = PublicationKey(label, $"option_{index + 1}");
                Ensure(label.Length > 0, "nonblank publication label");
                result.Add(new(key, label, true, (index + 1) * 10));
            }
            Ensure(result.Select(item => item.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Count, "unique publication labels");
            Ensure(result.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() == result.Count, "unique publication option IDs");
        }

        if (result.Count == 0 || IsNumericLabelFallback(result))
        {
            return isSystem ? SystemPublicationOptionDefaults() : [];
        }
        return result.OrderBy(option => option.SortOrder).ToArray();
    }

    private static string PublicationKey(string label, string fallback) =>
        Regex.Replace(TrimLegacyCustomFieldText(label).Replace("\u0130", "i\u0307", StringComparison.Ordinal).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } key
            ? key
            : fallback;

    private static string ReadPublicationOptionLabel(JsonElement item)
    {
        foreach (var name in new[] { "label", "name", "value" })
        {
            if (!item.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }
            Ensure(property.ValueKind == JsonValueKind.String, "publication option label alias type");
            var value = property.GetString() ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }
            var label = TrimLegacyCustomFieldText(value);
            Ensure(label.Length > 0, "publication option whitespace-only selected alias is unsupported");
            return label;
        }
        return string.Empty;
    }

    private static string? ReadPublicationOptionId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        Ensure(property.ValueKind == JsonValueKind.String, "publication option ID type");
        var raw = property.GetString()!;
        if (raw.Length == 0)
        {
            return null;
        }
        var normalized = TrimLegacyCustomFieldText(raw);
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsNumericLabelFallback(IReadOnlyList<PublicationOption> options) =>
        options.Count > 3 && options.Count(option => option.Label.Length > 0 && option.Label.All(character => character is >= '0' and <= '9')) * 2 > options.Count;

    private static IReadOnlyList<PublicationOption> SystemPublicationOptionDefaults() =>
    [
        new("already_published", "Already published", true, 10),
        new("coming_soon", "Coming soon", true, 20),
        new("published_a_while_back", "Published a while back", true, 30)
    ];

    private static bool JsonBoolean(JsonElement value, string name, bool defaultValue)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaultValue;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => Fail<bool>("A source publication-option boolean is invalid.")
        };
    }

    private static int CustomFieldSortOrder(JsonElement value, int index)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("sortOrder", out var property))
        {
            return checked((index + 1) * 10);
        }
        if (property.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed)
            ? parsed
            : Fail<int>("A source custom-field sort order is invalid.");
    }

    private static IReadOnlyList<ExpectedCustomFieldDefinition> NormalizeExpectedCustomFieldDefinitions(JsonElement definitions)
    {
        if (definitions.ValueKind != JsonValueKind.Array)
        {
            return Fail<IReadOnlyList<ExpectedCustomFieldDefinition>>("A source custom-field definition collection is not an array.");
        }

        var result = new List<ExpectedCustomFieldDefinition>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var definitionIndex = 0;
        foreach (var definition in definitions.EnumerateArray())
        {
            if (definition.ValueKind != JsonValueKind.Object)
            {
                return Fail<IReadOnlyList<ExpectedCustomFieldDefinition>>("A source custom-field definition is not an object.");
            }

            var label = RequiredJsonString(definition, "label");
            var type = RequiredJsonString(definition, "type");
            if (type is not ("text" or "textarea" or "select"))
            {
                return Fail<IReadOnlyList<ExpectedCustomFieldDefinition>>("A source custom-field type is unsupported.");
            }
            var identity = OptionalCustomFieldIdentity(definition, "key") ?? label;
            var key = NormalizeExpectedCustomFieldIdentity(identity);
            Ensure(key.Length > 0 && keys.Add(key), "unique normalized source custom-field keys");
            var options = type == "select"
                ? NormalizeExpectedCustomFieldOptions(definition)
                : Array.Empty<ExpectedCustomFieldOption>();
            result.Add(new(
                key,
                type,
                label,
                OptionalJsonString(definition, "helpText"),
                JsonBoolean(definition, "enabled", defaultValue: true),
                CustomFieldSortOrder(definition, definitionIndex),
                options));
            definitionIndex++;
        }

        return result
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Label, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static IReadOnlyList<ExpectedCustomFieldOption> NormalizeExpectedCustomFieldOptions(JsonElement definition)
    {
        if (!definition.TryGetProperty("options", out var options) || options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<ExpectedCustomFieldOption>();
        }
        if (options.ValueKind != JsonValueKind.Array)
        {
            return Fail<IReadOnlyList<ExpectedCustomFieldOption>>("A source custom-field options value is not an array or null.");
        }

        var result = new List<ExpectedCustomFieldOption>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var optionIndex = 0;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                return Fail<IReadOnlyList<ExpectedCustomFieldOption>>("A source custom-field option is not an object.");
            }
            var label = ExpectedCustomFieldOptionLabel(option);
            var identity = OptionalCustomFieldIdentity(option, "id") ?? label;
            var key = NormalizeExpectedCustomFieldIdentity(identity);
            Ensure(key.Length > 0 && keys.Add(key), "unique normalized source custom-field option IDs");
            result.Add(new(
                key,
                label,
                JsonBoolean(option, "enabled", defaultValue: true),
                CustomFieldSortOrder(option, optionIndex)));
            optionIndex++;
        }

        return result
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Label, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static string ExpectedCustomFieldOptionLabel(JsonElement option)
    {
        foreach (var propertyName in new[] { "label", "name", "value" })
        {
            if (!option.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }
            if (property.ValueKind != JsonValueKind.String)
            {
                return Fail<string>($"A source custom-field option {propertyName} is not text.");
            }
            var rawText = property.GetString()!;
            if (rawText.Length == 0)
            {
                continue;
            }
            var label = TrimLegacyCustomFieldText(rawText);
            return label.Length > 0
                ? label
                : Fail<string>("A source custom-field option label is blank.");
        }
        return Fail<string>("A source custom-field option has no label, name or value.");
    }

    private static string? OptionalCustomFieldIdentity(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return Fail<string?>($"A source custom-field {name} is not text.");
        }
        var text = property.GetString()!;
        return text.Length == 0 ? null : text;
    }

    private static string NormalizeExpectedCustomFieldIdentity(string value) =>
        Regex.Replace(TrimLegacyCustomFieldText(value).Replace("\u0130", "i\u0307", StringComparison.Ordinal).ToLowerInvariant(), "[^a-z0-9]+", "_", RegexOptions.CultureInvariant).Trim('_');

    private static bool JsonBooleanStrict(JsonElement value, string name, bool defaultValue)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            return defaultValue;
        }
        if (property.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => Fail<bool>("A source publication-option enabled value is invalid.")
        };
    }

    private static int? JsonIntegerStrict(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed)
            ? parsed
            : Fail<int?>("A source publication-option sort order is invalid.");
    }

    private static void VerifyCustomFieldDefinitions(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        int libraryId,
        SourceRow source)
    {
        var definitionsJson = source.JsonText("additionalFieldDefinitions");
        if (definitionsJson is null)
        {
            Ensure(Count(connection, transaction,
                "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @id;", libraryId) == 0,
                "empty custom field population");
            Ensure(Count(connection, transaction,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @id;", libraryId) == 0,
                "empty custom field rule population");
            return;
        }

        using var definitionsDocument = JsonDocument.Parse(definitionsJson);
        var definitions = NormalizeExpectedCustomFieldDefinitions(definitionsDocument.RootElement);
        Ensure(Count(connection, transaction,
            "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @id;", libraryId) == definitions.Count,
            "exact custom field population");
        var actualFieldIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            long fieldId;
            using (var fieldCommand = new SqlCommand(
                       "SELECT [FieldKey], [Id], [FieldType], [Label], [HelpText], [IsEnabled], [SortOrder] FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @libraryId AND [FieldKey] = @fieldKey;",
                       connection,
                       transaction))
            {
                fieldCommand.Parameters.AddWithValue("@libraryId", libraryId);
                fieldCommand.Parameters.AddWithValue("@fieldKey", definition.Key);
                using var fieldReader = fieldCommand.ExecuteReader();
                Ensure(fieldReader.Read(), "custom field identity");
                fieldId = fieldReader.GetInt64(1);
                Ensure(
                    Same(fieldReader, 0, definition.Key) && Same(fieldReader, 2, definition.Type) && Same(fieldReader, 3, definition.Label) &&
                    Same(fieldReader, 4, definition.HelpText) && fieldReader.GetBoolean(5) == definition.Enabled &&
                    fieldReader.GetInt32(6) == definition.SortOrder,
                    "custom field key, type, label, help, enabled state and order");
                Ensure(actualFieldIds.TryAdd(definition.Key, fieldId), "unique custom field key");
            }

            using (var optionCount = new SqlCommand(
                       "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] = @fieldId;",
                       connection,
                       transaction))
            {
                optionCount.Parameters.AddWithValue("@fieldId", fieldId);
                Ensure(Convert.ToInt32(optionCount.ExecuteScalar(), CultureInfo.InvariantCulture) == definition.Options.Count,
                    "exact custom field option population");
            }
            var expectedOptions = new Dictionary<string, (string Label, bool Enabled, int SortOrder)>(StringComparer.Ordinal);
            foreach (var option in definition.Options)
            {
                Ensure(expectedOptions.TryAdd(option.Key, (
                    option.Label,
                    option.Enabled,
                    option.SortOrder)), "unique custom field option identity");
            }
            using (var optionsCommand = new SqlCommand(
                       "SELECT [OptionKey], [Label], [IsEnabled], [SortOrder] FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] = @fieldId;",
                       connection,
                       transaction))
            {
                optionsCommand.Parameters.AddWithValue("@fieldId", fieldId);
                using var optionsReader = optionsCommand.ExecuteReader();
                var actualOptions = new Dictionary<string, (string Label, bool Enabled, int SortOrder)>(StringComparer.Ordinal);
                while (optionsReader.Read())
                {
                    Ensure(actualOptions.TryAdd(optionsReader.GetString(0), (
                        optionsReader.GetString(1), optionsReader.GetBoolean(2), optionsReader.GetInt32(3))),
                        "unique target custom field option identity");
                }
                Ensure(expectedOptions.Count == actualOptions.Count && expectedOptions.All(pair =>
                    actualOptions.TryGetValue(pair.Key, out var actual) && actual == pair.Value),
                    "custom field option IDs, labels, enabled state and order");
            }
        }
        VerifyCustomFieldRules(connection, transaction, package, organizationIds, libraryId, source, definitions, actualFieldIds);
    }

    private static void VerifyCustomFieldRules(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        int libraryId,
        SourceRow settings,
        IReadOnlyList<ExpectedCustomFieldDefinition> definitions,
        IReadOnlyDictionary<string, long> fieldIds)
    {
        var effectiveFormats = ReadEffectiveFormatRows(connection, transaction, libraryId);
        var expectedCount = effectiveFormats.Count * definitions.Count;
        Ensure(Count(connection, transaction,
            "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @id;", libraryId) == expectedCount,
            "exact custom field per-format rule population");

        JsonDocument? rulesDocument = null;
        try
        {
            if (settings.JsonText("patronFormatRules") is { } rulesJson)
            {
                rulesDocument = JsonDocument.Parse(rulesJson);
                Ensure(rulesDocument.RootElement.ValueKind == JsonValueKind.Object, "source patron format rules object");
            }
            var sourceFormats = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
            foreach (var format in effectiveFormats)
            {
                var sourceCode = ResolveEffectiveSourceFormatCode(sourceFormats, organizationIds, package, libraryId, format.Code);
                foreach (var definition in definitions)
                {
                    var fieldKey = definition.Key;
                    var expected = ExpectedCustomFieldRule(
                        rulesDocument?.RootElement,
                        sourceCode,
                        fieldKey,
                        definition.Type,
                        definition.Enabled,
                        definition.Options.Count(option => option.Enabled));
                    using var command = new SqlCommand(
                        "SELECT [Mode], [LabelOverride] FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @libraryId AND [MaterialFormatId] = @formatId AND [PatronCustomFieldId] = @fieldId;",
                        connection,
                        transaction);
                    command.Parameters.AddWithValue("@libraryId", libraryId);
                    command.Parameters.AddWithValue("@formatId", format.Id);
                    command.Parameters.AddWithValue("@fieldId", fieldIds[fieldKey]);
                    using var reader = command.ExecuteReader();
                    Ensure(reader.Read() && Same(reader, 0, expected.Mode) && Same(reader, 1, expected.Label),
                        "custom field format mode and label override");
                }
            }
        }
        finally
        {
            rulesDocument?.Dispose();
        }
    }

    private static IReadOnlyList<(long Id, string Code, int OwnerId)> ReadEffectiveFormatRows(
        SqlConnection connection,
        SqlTransaction? transaction,
        int libraryId)
    {
        using var command = new SqlCommand(
            "WITH scoped AS (SELECT [Id], [Code], [OwnerOrganizationId], ROW_NUMBER() OVER (PARTITION BY LOWER([Code]) ORDER BY CASE WHEN [OwnerOrganizationId] = @libraryId THEN 0 ELSE 1 END, [Id]) AS [rank] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] IN (1, @libraryId)) SELECT [Id], [Code], [OwnerOrganizationId] FROM scoped WHERE [rank] = 1 ORDER BY [Id];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@libraryId", libraryId);
        using var reader = command.ExecuteReader();
        var rows = new List<(long Id, string Code, int OwnerId)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return rows;
    }

    private static string ResolveEffectiveSourceFormatCode(
        IReadOnlyList<SourceRow> sourceFormats,
        IReadOnlyDictionary<string, int> organizationIds,
        ValidatedMigrationPackage package,
        int libraryId,
        string targetCode)
    {
        var libraryRow = sourceFormats.FirstOrDefault(row =>
            string.Equals(row.String("scope"), "library", StringComparison.OrdinalIgnoreCase) &&
            ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package) == libraryId &&
            string.Equals(NormalizeFormatCode(row.RequiredString("code")), NormalizeFormatCode(targetCode), StringComparison.Ordinal));
        if (libraryRow is not null)
        {
            return libraryRow.RequiredString("code");
        }
        var systemRow = sourceFormats.FirstOrDefault(row =>
            string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeFormatCode(row.RequiredString("code")), NormalizeFormatCode(targetCode), StringComparison.Ordinal));
        return systemRow?.RequiredString("code") ?? NormalizeFormatCode(targetCode);
    }

    private static (string Mode, string? Label) ExpectedCustomFieldRule(
        JsonElement? rules,
        string formatCode,
        string fieldKey,
        string fieldType,
        bool enabled,
        int enabledOptionCount)
    {
        if (!enabled || rules is null)
        {
            return ("hidden", null);
        }
        JsonElement format;
        if (!TryExactProperty(rules.Value, formatCode, out format))
        {
            if (IsBuiltin(formatCode) || !TryExactProperty(rules.Value, "book", out format))
            {
                return ("hidden", null);
            }
        }
        if (format.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ("hidden", null);
        }
        if (format.ValueKind != JsonValueKind.Object)
        {
            return Fail<(string, string?)>("A source custom-field format rule is neither an object nor null.");
        }
        if (!TryExactProperty(format, "customFields", out var customFields) || customFields.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ("hidden", null);
        }
        if (customFields.ValueKind != JsonValueKind.Object)
        {
            return Fail<(string, string?)>("A source customFields value is neither an object nor null.");
        }
        if (!TryExactProperty(customFields, fieldKey, out var rule) || rule.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ("hidden", null);
        }
        if (rule.ValueKind != JsonValueKind.Object)
        {
            return Fail<(string, string?)>("A source custom-field rule is neither an object nor null.");
        }
        var mode = LegacyCustomFieldJsonString(rule, "mode") ?? string.Empty;
        if (mode is not ("required" or "optional" or "hidden"))
        {
            mode = "hidden";
        }
        if (mode == "required" && fieldType == "select" && enabledOptionCount == 0)
        {
            mode = "optional";
        }
        var label = LegacyCustomFieldJsonString(rule, "label") ?? string.Empty;
        return (mode, label.Length == 0 ? null : label);
    }

    private static bool TryExactProperty(JsonElement value, string name, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    result = property.Value;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    private static bool IsBuiltin(string code) =>
        code is "book" or "audiobook_cd" or "dvd" or "music_cd" or "ebook" or "eaudiobook";

    private static string? LegacyCustomFieldJsonString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return Fail<string?>($"A source custom-field {name} is not text.");
        }

        var text = TrimLegacyCustomFieldText(property.GetString()!);
        return text.Length > 0 ? text : null;
    }

    private static string TrimLegacyCustomFieldText(string value)
    {
        var start = 0;
        while (start < value.Length && IsLegacyCustomFieldWhitespace(value[start]))
        {
            start++;
        }
        var end = value.Length;
        while (end > start && IsLegacyCustomFieldWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsLegacyCustomFieldWhitespace(char value) => value is
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or
        '\u1680' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or
        '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028' or '\u2029' or
        '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static string RequiredJsonString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && TrimLegacyCustomFieldText(property.GetString()!).Length > 0
            ? TrimLegacyCustomFieldText(property.GetString()!)
            : Fail<string>($"A source custom-field {name} is missing.");

    private static string? OptionalJsonString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return Fail<string?>($"A source custom-field {name} is not text.");
        }
        var text = TrimLegacyCustomFieldText(property.GetString()!);
        return text.Length > 0 ? text : null;
    }

    private static void VerifyEmailTemplates(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var normalRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates");
        var rejectionRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates");
        var allRows = normalRows.Concat(rejectionRows).ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        foreach (var row in allRows.Values)
        {
            var sourceId = row.RequiredString("id");
            var rejection = rejectionRows.Any(candidate => string.Equals(candidate.RequiredString("id"), sourceId, StringComparison.Ordinal));
            var scope = row.RequiredString("scope").Trim().ToLowerInvariant();
            var organizationId = scope switch
            {
                "system" => 1,
                "library" => ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package),
                _ => Fail<int>("A source email template has an invalid scope.")
            };
            var templateKey = rejection
                ? "rejection:" + (scope == "library" ? row.String("sourceTemplateId") ?? sourceId : sourceId)
                : row.RequiredString("templateKey");
            Ensure(rejection || !templateKey.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase),
                "ordinary source email template retains its target template kind");
            long? sourceTemplateId = null;
            var explicitTemplateId = row.String("sourceTemplateId");
            if (scope == "system")
            {
                Ensure(explicitTemplateId is null, "system template does not declare library override lineage");
            }
            if (scope == "library")
            {
                if (explicitTemplateId is not null)
                {
                    Ensure(allRows.ContainsKey(explicitTemplateId), "email template source relationship exists");
                    var sourceTemplate = allRows[explicitTemplateId];
                    Ensure(string.Equals(sourceTemplate.RequiredString("scope").Trim(), "system", StringComparison.OrdinalIgnoreCase),
                        "library email template inherits only from system scope");
                    var parentIsRejection = rejectionRows.Any(candidate =>
                        string.Equals(candidate.RequiredString("id"), explicitTemplateId, StringComparison.Ordinal));
                    Ensure(rejection == parentIsRejection,
                        "email-template lineage remains within the same template collection");
                    if (!rejection)
                    {
                        Ensure(string.Equals(
                                row.RequiredString("templateKey"),
                                sourceTemplate.RequiredString("templateKey"),
                                StringComparison.Ordinal),
                            "ordinary email-template lineage retains the same system template key");
                    }
                    sourceTemplateId = ReadMapping(connection, transaction, "email_template", explicitTemplateId);
                }
                else if (!rejection)
                {
                    var sourceSystemTemplate = normalRows.SingleOrDefault(candidate =>
                        string.Equals(candidate.RequiredString("scope").Trim(), "system", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.RequiredString("templateKey"), templateKey, StringComparison.Ordinal));
                    if (sourceSystemTemplate is not null)
                    {
                        sourceTemplateId = ReadMapping(connection, transaction, "email_template", sourceSystemTemplate.RequiredString("id"));
                    }
                    else if (SeededSystemEmailTemplateKeys.Contains(templateKey, StringComparer.Ordinal))
                    {
                        using var systemTemplate = new SqlCommand(
                            "SELECT [Id] FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted';",
                            connection,
                            transaction);
                        var value = systemTemplate.ExecuteScalar();
                        sourceTemplateId = value is null or DBNull
                            ? null
                            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    }
                }
            }
            var isCustom = scope == "library" && sourceTemplateId is null;
            var targetId = ReadMapping(connection, transaction, "email_template", sourceId);
            using var command = new SqlCommand(
                "SELECT [OrganizationId], [TemplateKey], [SourceTemplateId], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder] FROM [asap].[EmailTemplate] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", targetId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() &&
                reader.GetInt32(0) == organizationId &&
                Same(reader, 1, templateKey) &&
                NullableLong(reader, 2) == sourceTemplateId &&
                Same(reader, 3, row.Text("name")) &&
                Same(reader, 4, NormalizeTemplateText(row.Text("subject"))) &&
                Same(reader, 5, NormalizeTemplateText(row.Text("body"))) &&
                reader.GetBoolean(6) == !row.Bool("enabled", true) &&
                reader.GetBoolean(7) == isCustom &&
                reader.GetInt32(8) == (row.Int32("sortOrder") ?? 0),
                "email template identity, content, enabled state and source relationship");
        }
    }

    private static string? NormalizeTemplateText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ResolveScopedOrganization(
        SourceRow row,
        IReadOnlyDictionary<string, int> organizationIds,
        ValidatedMigrationPackage package)
    {
        var scope = row.RequiredString("scope").Trim().ToLowerInvariant();
        return scope switch
        {
            "system" => 1,
            "library" => ResolveLibrary(row.RequiredString("libraryOrganization"), organizationIds, package),
            _ => Fail<int>("A source configuration row has an invalid scope.")
        };
    }

    private static int ResolveLibrary(
        string sourceReference,
        IReadOnlyDictionary<string, int> organizationIds,
        ValidatedMigrationPackage package)
    {
        var id = organizationIds.TryGetValue(sourceReference, out var mapped)
            ? mapped
            : int.TryParse(sourceReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nativeId)
                ? nativeId
                : Fail<int>("A source library reference does not resolve to a native organization.");
        Ensure(IsSourceLibrary(id, package), "source library organization type");
        return id;
    }

    private static bool IsSourceLibrary(int organizationId, ValidatedMigrationPackage package)
    {
        foreach (var row in MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations"))
        {
            if (row.Int32("organizationId") != organizationId)
            {
                continue;
            }
            var identities = new[] { "organizationCodeId", "organization_code_id" }
                .Where(row.HasValue)
                .Select(row.Int32)
                .Distinct()
                .ToArray();
            Ensure(identities.Length <= 1, "source organization classification aliases");
            return organizationId > 1 && identities.SingleOrDefault() == 2;
        }
        return false;
    }

    private static IReadOnlyList<string> Split(string? source) =>
        string.IsNullOrWhiteSpace(source)
            ? []
            : source.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => value.Length > 0)
                .ToArray();

    private static IReadOnlyList<string> SplitCommonCreatorLines(string? source) =>
        source is null
            ? []
            : source.Split('\n')
                .Select(TrimLegacyCustomFieldText)
                .Where(value => value.Length > 0)
                .ToArray();

    private static int[] ParseExpectedPatronCodeIds(string? source)
    {
        if (source is null)
        {
            return [];
        }
        var ids = new HashSet<int>();
        foreach (var raw in source.Split(','))
        {
            var token = TrimLegacyCustomFieldText(raw);
            if (token.Length == 0)
            {
                continue;
            }
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                !string.Equals(token, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return Fail<int[]>("A source patron-code ID is not representable as the same canonical target identity.");
            }
            ids.Add(id);
        }
        return ids.Order().ToArray();
    }

    private static bool HasLegacyCustomFieldText(string? value) =>
        value is not null && TrimLegacyCustomFieldText(value).Length > 0;

    private static string? ScopedText(SourceRow row, string field, bool isSystem) =>
        isSystem ? row.Text(field) : Meaningful(row.Text(field));

    private static string NormalizeFormatCode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0" => "book",
        "1" => "ebook",
        "2" => "audiobook_cd",
        "3" => "eaudiobook",
        "4" => "dvd",
        "5" => "music_cd",
        var code when code.Length > 0 => code,
        _ => Fail<string>("A source material-format code is blank.")
    };

    private static HashSet<int> TargetOrganizationIds(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        var ids = new HashSet<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    private static HashSet<long> TargetLongIds(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static IReadOnlyList<PublicationOption> ReadPublicationOptions(
        SqlConnection connection,
        SqlTransaction? transaction,
        int organizationId)
    {
        using var command = new SqlCommand(
            "SELECT [OptionKey], [Label], [IsEnabled], [SortOrder] FROM [asap].[PublicationOption] WHERE [OrganizationId] = @id ORDER BY [SortOrder], [Id];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        using var reader = command.ExecuteReader();
        var options = new List<PublicationOption>();
        while (reader.Read())
        {
            options.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3)));
        }
        return options;
    }

    private static int CountAll(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long ReadMapping(
        SqlConnection connection,
        SqlTransaction? transaction,
        string entityType,
        string sourceId)
    {
        using var command = new SqlCommand(
            "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = @entityType AND [PocketBaseId] = @sourceId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        using var reader = command.ExecuteReader();
        Ensure(reader.Read(), "source mapping exists");
        var targetId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        Ensure(!reader.Read(), "source mapping is unique");
        return targetId;
    }

    private static int Count(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        int organizationId)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool Same(SqlDataReader reader, int ordinal, string? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : string.Equals(reader.GetString(ordinal), expected, StringComparison.Ordinal);

    private static bool Same(SqlDataReader reader, int ordinal, DateTime? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : expected.HasValue && reader.GetDateTime(ordinal).Ticks == expected.Value.Ticks;

    private static int? NullableInt(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? NullableLong(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool? NullableBoolean(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static void Ensure(bool condition, string entity)
    {
        if (!condition)
        {
            throw new MigrationOperationException("reconciliation_failed", $"Imported {entity} differs from the immutable source package.");
        }
    }

    private static T Fail<T>(string message) => throw new MigrationOperationException("reconciliation_failed", message);

    private static void Fail(string message) => throw new MigrationOperationException("reconciliation_failed", message);

    private sealed record PublicationOption(string Key, string Label, bool Enabled, int SortOrder);

    private sealed record TemplateTarget(
        long Id,
        int OrganizationId,
        string TemplateKey,
        long? SourceTemplateId,
        bool IsHidden,
        string? Subject,
        string? Body);
}
