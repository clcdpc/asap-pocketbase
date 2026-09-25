using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Features.Administration;

namespace Asap.Web.Features.Staff.Compatibility;

// Presentation for the pinned /staff/ editor. The Administration service remains the
// only owner of scope, settings versions, inheritance, writes, and transactions.
public sealed class LegacyStaffSettingsService(AdministrationService administration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] WorkflowFields =
    [
        "suggestionLimit", "suggestionLimitMessage", "outstandingTimeoutEnabled",
        "outstandingTimeoutDays", "outstandingTimeoutSendEmail", "outstandingTimeoutRejectionTemplateId",
        "holdPickupTimeoutEnabled", "holdPickupTimeoutDays", "pendingHoldTimeoutEnabled",
        "pendingHoldTimeoutDays", "additionalCopyTimeoutEnabled", "additionalCopyTimeoutDays",
        "autoPromote", "commonAuthorsEnabled", "commonAuthorsLabel", "commonAuthorsHelp",
        "commonAuthorsMessage", "allowPatronAutoholdOptOut", "allowAnyRegisteredCardLogin",
        "patronCodeEligibilityEnabled", "patronCodeEligibilityMessage"
    ];
    private static readonly string[] PatronFields =
    [
        "pageTitle", "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "suggestionFormNote",
        "noEmailMessage", "successTitle", "successMessage", "alreadySubmittedMessage", "ebookMessage",
        "eaudiobookMessage", "systemNotEnabledMessage", "misconfiguredMessage"
    ];
    private static readonly string[] FormatFields =
    [
        "label", "sortOrder", "isEnabled", "messageBehavior", "message", "titleMode", "titleLabel",
        "authorMode", "authorLabel", "identifierMode", "identifierLabel", "publicationMode", "publicationLabel"
    ];
    private static readonly string[] StandardTemplateKeys =
    ["suggestion_submitted", "purchase_approved", "already_owned", "rejected", "hold_placed"];

    public async Task<AdministrationResult> LoadAsync(
        CurrentStaff actor,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var result = await administration.GetSettingsAsync(actor, organizationId, cancellationToken);
        return result.Code == "ok"
            ? new AdministrationResult("ok", Project(ToObject(result.Data)))
            : result;
    }

    public async Task<AdministrationResult> SaveAsync(
        CurrentStaff actor,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return new AdministrationResult("invalid_input", Message: "Settings must be an object.");
        }
        var submitted = JsonNode.Parse(input.GetRawText()) as JsonObject;
        if (submitted is null || Text(submitted["version"]) is not { Length: > 0 } version ||
            Text(submitted["orgId"]) is not { Length: > 0 } orgId)
        {
            return new AdministrationResult("invalid_input", Message: "A Settings scope and version are required.");
        }
        var current = await administration.GetSettingsAsync(actor, orgId, cancellationToken);
        if (current.Code != "ok")
        {
            return current;
        }
        var source = ToObject(current.Data);
        if (!string.Equals(version, Text(source["version"]), StringComparison.Ordinal))
        {
            return new AdministrationResult("stale_version", Message: "These settings changed. Reload before saving.");
        }

        var displayed = Project(source);
        JsonObject patch;
        IReadOnlyList<CustomFormatRemoval> removals;
        try
        {
            patch = BuildPatch(submitted, displayed, source);
            removals = ResolveFormatRemovals(submitted, source, orgId);
        }
        catch (InvalidOperationException error)
        {
            return new AdministrationResult("invalid_input", Message: error.Message);
        }
        patch["orgId"] = orgId;
        patch["version"] = version;
        using var document = JsonDocument.Parse(patch.ToJsonString());
        var operation = await administration.SaveSettingsWithFormatRemovalsAsync(
            actor, document.RootElement, removals, cancellationToken);
        if (!operation.SaveCommitted)
        {
            return operation.Settings;
        }
        if (operation.DeletionFailure is not null)
        {
            return new AdministrationResult("partial", new
            {
                deletedFormats = operation.DeletedFormatCodes,
                failedFormat = operation.FailedFormatCode,
                failureCode = operation.DeletionFailure.Code,
                failureMessage = operation.DeletionFailure.Message
            });
        }
        return new AdministrationResult("saved", new { deletedFormats = operation.DeletedFormatCodes });
    }

    private static IReadOnlyList<CustomFormatRemoval> ResolveFormatRemovals(
        JsonObject submitted, JsonObject source, string orgId)
    {
        if (!submitted.ContainsKey("deletedFormats"))
        {
            return [];
        }
        var formats = Array(Object(source["stored"])["formats"]).OfType<JsonObject>()
            .Where(row => Text(row["code"]) is not null)
            .ToDictionary(row => Text(row["code"])!, StringComparer.Ordinal);
        var removals = new List<CustomFormatRemoval>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in RequireArray(submitted["deletedFormats"], "deletedFormats"))
        {
            var removal = RequireObject(node, "deleted format");
            ValidateKeys(removal, ["code", "version"]);
            var code = Text(removal["code"]);
            var version = Text(removal["version"]);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(version) ||
                !seen.Add(code) || !formats.TryGetValue(code, out var row) ||
                Text(row["ownerOrganizationId"]) != orgId ||
                !long.TryParse(Text(row["id"]), out var id))
            {
                throw new InvalidOperationException("A custom format removal is incomplete or out of scope.");
            }
            removals.Add(new CustomFormatRemoval(id, code, version));
        }
        return removals;
    }

    private static JsonObject Project(JsonObject source)
    {
        var stored = Object(source["stored"]);
        var effective = Object(source["effective"]);
        var system = Object(stored["systemSettings"]);
        var polaris = Object(stored["polaris"]);
        var workflow = CopyObject(effective["workflow"]);
        var uiText = CopyObject(source["ui_text"]);
        var emails = CopyObject(source["emails"]);
        var formats = Array(stored["formats"]);
        var providers = Array(stored["providers"]);
        var templates = Array(source["templateEditor"]);
        var orgId = Text(source["orgId"]) ?? "system";

        var creatorValues = Array(effective["commonCreators"]).Select(Text).Where(value => value is not null);
        workflow["commonAuthorsList"] = string.Join('\n', creatorValues);
        workflow["allowedPatronCodeIds"] = string.Join(',', Array(effective["allowedPatronCodeIds"])
            .Select(Text).Where(value => value is not null));
        foreach (var provider in providers.OfType<JsonObject>())
        {
            var key = Text(provider["key"]);
            if (key is null || !key.StartsWith("external_search_", StringComparison.Ordinal) ||
                key.Length != "external_search_1".Length || key[^1] is < '1' or > '4')
            {
                continue;
            }
            var slot = key[^1];
            workflow[$"externalSearch{slot}Enabled"] = Bool(provider["isEnabled"]);
            workflow[$"externalSearch{slot}Label"] = Text(provider["label"]) ?? "";
            workflow[$"externalSearch{slot}UrlTemplate"] = Text(provider["urlTemplate"]) ?? "";
        }

        var labels = new JsonObject();
        var order = new JsonArray();
        var available = new JsonArray();
        var rules = new JsonObject();
        foreach (var format in formats.OfType<JsonObject>())
        {
            var code = Text(format["code"]);
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }
            labels[code] = Text(format["label"]) ?? code;
            order.Add(code);
            if (Bool(format["isEnabled"]))
            {
                available.Add(code);
            }
            rules[code] = new JsonObject
            {
                ["messageBehavior"] = Text(format["messageBehavior"]) ?? "none",
                ["message"] = Text(format["message"]) ?? "",
                ["fields"] = new JsonObject
                {
                    ["title"] = Field(format, "title", "required", "Title"),
                    ["author"] = Field(format, "author", "optional", "Author"),
                    ["identifier"] = Field(format, "identifier", "optional", "Identifier number"),
                    ["publication"] = Field(format, "publication", "optional", "Publication Timing")
                },
                ["customFields"] = new JsonObject()
            };
        }
        foreach (var row in Array(stored["customFieldRules"]).OfType<JsonObject>())
        {
            var code = Text(row["formatCode"]);
            var key = Text(row["fieldKey"]);
            if (code is null || key is null || rules[code] is not JsonObject formatRule ||
                formatRule["customFields"] is not JsonObject custom)
            {
                throw new InvalidOperationException("The custom field rule snapshot is incomplete.");
            }
            custom[key] = new JsonObject
            {
                ["mode"] = Text(row["mode"]),
                ["labelOverride"] = Copy(row["labelOverride"])
            };
        }
        uiText["formatLabels"] = labels;
        uiText["formatOrder"] = order;
        uiText["availableFormats"] = available;
        uiText["formatRules"] = rules;
        var fieldRows = new JsonArray();
        foreach (var field in Array(stored["customFields"]).OfType<JsonObject>())
        {
            var options = new JsonArray();
            foreach (var option in Array(field["options"]).OfType<JsonObject>())
            {
                options.Add(new JsonObject
                {
                    ["id"] = Text(option["id"]), ["label"] = Text(option["label"]),
                    ["enabled"] = Bool(option["enabled"]), ["sortOrder"] = Copy(option["sortOrder"])
                });
            }
            fieldRows.Add(new JsonObject
            {
                ["id"] = Text(field["id"]), ["key"] = Text(field["key"]),
                ["label"] = Text(field["label"]), ["type"] = Text(field["type"]),
                ["helpText"] = Copy(field["helpText"]), ["enabled"] = Bool(field["enabled"]),
                ["sortOrder"] = Copy(field["sortOrder"]), ["options"] = options
            });
        }
        uiText["additionalFieldDefinitions"] = fieldRows;
        uiText["publicationOptions"] = Copy(source["publicationOptionsEditor"]) ?? new JsonArray();
        uiText["logoAlt"] = Text(Object(stored["branding"])["altText"]) ??
            Text(effective["logoAltText"]) ?? "";
        uiText["logoUrl"] = Bool(effective["hasLogo"])
            ? $"/api/asap/config/logo?libraryOrgId={Text(effective["organizationId"]) ?? (orgId == "system" ? "1" : orgId)}"
            : "/jpl.png";
        uiText["brandingInherited"] = orgId != "system" &&
            Text(Object(stored["branding"])["version"]) is null;
        uiText["patronSettingsInherited"] = orgId != "system" &&
            Object(Object(stored["libraryOverride"])["patron"]).Count == 0;
        if (orgId == "system")
        {
            uiText["systemNotEnabledMessage"] = Copy(system["systemNotEnabledMessage"]);
            uiText["misconfiguredMessage"] = Copy(system["misconfiguredMessage"]);
        }

        var codeById = formats.OfType<JsonObject>().Where(row => Text(row["id"]) is not null)
            .ToDictionary(row => Text(row["id"])!, row => Text(row["code"]) ?? "", StringComparer.Ordinal);
        var claims = new JsonArray();
        foreach (var claim in Array(stored["autoClaimRules"]).OfType<JsonObject>())
        {
            if (!Bool(claim["active"]) && !Bool(claim["isActive"]))
            {
                continue;
            }
            var formatId = Text(claim["materialFormatId"]);
            if (formatId is null || !codeById.TryGetValue(formatId, out var code))
            {
                throw new InvalidOperationException("An auto-claim rule references an unknown format.");
            }
            claims.Add(new JsonObject
            {
                ["format"] = code,
                ["staffUserId"] = Text(claim["staffUserId"])
            });
        }
        var staffOptions = new JsonArray();
        foreach (var staff in Array(source["autoClaimStaff"]).OfType<JsonObject>())
        {
            staffOptions.Add(new JsonObject
            {
                ["id"] = Text(staff["id"]),
                ["displayName"] = Text(staff["label"]) ?? "Staff",
                ["libraryOrgId"] = orgId
            });
        }

        var templateRows = new JsonArray();
        foreach (var template in templates.OfType<JsonObject>())
        {
            var row = new JsonObject
            {
                ["id"] = Text(template["id"]),
                ["templateKey"] = Text(template["templateKey"]),
                ["name"] = Text(template["displayName"]) ?? "",
                ["subject"] = Text(template["subject"]) ?? "",
                ["body"] = Text(template["body"]) ?? "",
                ["enabled"] = Bool(template["enabled"]),
                ["canReset"] = orgId != "system" && (Bool(template["hasOverride"]) || Bool(template["isCustom"]))
            };
            templateRows.Add(row);
            var key = Text(template["templateKey"]);
            if (key is not null && StandardTemplateKeys.Contains(key, StringComparer.Ordinal))
            {
                emails[key] = new JsonObject { ["subject"] = Text(row["subject"]), ["body"] = Text(row["body"]) };
            }
        }

        return new JsonObject
        {
            ["orgId"] = orgId,
            ["version"] = Text(source["version"]),
            ["isOverride"] = Bool(source["isOverride"]),
            ["workflow"] = workflow,
            ["uiText"] = uiText,
            ["emails"] = emails,
            ["systemSettings"] = ProjectSystemSettings(system, source),
            ["polaris"] = ProjectPolaris(polaris),
            ["providers"] = ProjectProviders(providers),
            ["formats"] = ProjectFormats(formats, orgId),
            ["templates"] = templateRows,
            ["autoClaimRules"] = claims,
            ["autoClaimStaff"] = staffOptions
        };
    }

    private static JsonObject ProjectSystemSettings(JsonObject system, JsonObject source) => new()
    {
        ["staffUrl"] = Copy(system["staffUrl"]),
        ["leapBibUrlPattern"] = Copy(system["leapBibUrlPattern"]),
        ["leapPatronUrlPattern"] = Copy(system["leapPatronUrlPattern"]),
        ["formatIconUrlPattern"] = Copy(system["formatIconUrlPattern"]),
        ["systemNotEnabledMessage"] = Copy(system["systemNotEnabledMessage"]),
        ["misconfiguredMessage"] = Copy(system["misconfiguredMessage"]),
        ["patronEmbedAllowedOrigins"] = Copy(system["patronEmbedAllowedOrigins"]) ?? new JsonArray(),
        ["enabledLibraryOrgIds"] = Copy(source["enabledLibraryOrgIds"]) ?? new JsonArray()
    };

    private static JsonObject ProjectPolaris(JsonObject polaris) => new()
    {
        ["host"] = Copy(polaris["host"]),
        ["accessId"] = Copy(polaris["accessId"]),
        ["staffDomain"] = Copy(polaris["staffDomain"]),
        ["adminUser"] = Copy(polaris["adminUser"]),
        ["workstationId"] = Copy(polaris["workstationId"]),
        ["systemPolarisUserId"] = Copy(polaris["systemPolarisUserId"]),
        ["organizationIdForRequests"] = Copy(polaris["organizationIdForRequests"]),
        ["pickupOrganizationId"] = Copy(polaris["pickupOrganizationId"]),
        ["hasApiKey"] = Bool(polaris["hasApiKey"]),
        ["hasAdminPassword"] = Bool(polaris["hasAdminPassword"])
    };

    private static JsonArray ProjectProviders(JsonArray providers)
    {
        var rows = new JsonArray();
        foreach (var provider in providers.OfType<JsonObject>())
        {
            rows.Add(new JsonObject
            {
                ["key"] = Text(provider["key"]), ["isEnabled"] = Bool(provider["isEnabled"]),
                ["label"] = Text(provider["label"]) ?? "",
                ["urlTemplate"] = Text(provider["urlTemplate"]) ?? ""
            });
        }
        return rows;
    }

    private static JsonArray ProjectFormats(JsonArray formats, string orgId)
    {
        var rows = new JsonArray();
        foreach (var format in formats.OfType<JsonObject>())
        {
            rows.Add(new JsonObject
            {
                ["code"] = Text(format["code"]), ["label"] = Text(format["label"]),
                ["sortOrder"] = Copy(format["sortOrder"]),
                ["isEnabled"] = Bool(format["isEnabled"]),
                ["version"] = Text(format["version"]),
                ["canDelete"] = orgId != "system" && Text(format["ownerOrganizationId"]) == orgId
            });
        }
        return rows;
    }

    private static JsonObject Field(JsonObject format, string name, string defaultMode, string defaultLabel)
    {
        return new JsonObject
        {
            ["mode"] = Text(format[$"{name}Mode"]) ?? defaultMode,
            ["label"] = Text(format[$"{name}Label"]) ?? defaultLabel
        };
    }

    private static JsonObject BuildPatch(JsonObject submitted, JsonObject displayed, JsonObject source)
    {
        var isSystem = Text(displayed["orgId"]) == "system";
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "orgId", "version", "workflow", "ui_text", "emails", "providers", "formats",
            "formatRules", "customFields", "formatClaimRules", "polaris", "staffUrl",
            "leapBibUrlPattern", "leapPatronUrlPattern", "formatIconUrlPattern",
            "patronEmbedAllowedOrigins", "enabledLibraryOrgIds", "deletedFormats"
        };
        if (submitted.Any(property => !allowed.Contains(property.Key)))
        {
            throw new InvalidOperationException("The Settings form contains an unsupported section.");
        }
        var patch = new JsonObject();
        var currentWorkflow = Object(displayed["workflow"]);
        if (submitted.ContainsKey("workflow"))
        {
            var edited = RequireObject(submitted["workflow"], "workflow");
            ValidateKeys(edited, WorkflowFields.Concat(["commonAuthorsList", "allowedPatronCodeIds"]));
            var changed = ChangedProperties(edited, currentWorkflow, WorkflowFields);
            if (edited.ContainsKey("commonAuthorsList"))
            {
                AddIfChanged(changed, "commonAuthorsList", edited, currentWorkflow);
            }
            if (edited.ContainsKey("allowedPatronCodeIds"))
            {
                AddIfChanged(changed, "allowedPatronCodeIds", edited, currentWorkflow);
            }
            if (changed.Count > 0)
            {
                patch["workflow"] = changed;
            }
        }
        if (submitted.ContainsKey("ui_text"))
        {
            var edited = RequireObject(submitted["ui_text"], "ui_text");
            ValidateKeys(edited, PatronFields.Concat(["duplicateStatusLabels", "publicationOptions", "logoAlt"]));
            var canonical = Object(displayed["uiText"]);
            var changed = ChangedProperties(edited, canonical, PatronFields);
            if (edited.ContainsKey("duplicateStatusLabels"))
            {
                var labels = RequireObject(edited["duplicateStatusLabels"], "duplicateStatusLabels");
                var current = Object(canonical["duplicateStatusLabels"]);
                var changedLabels = ChangedProperties(labels, current,
                    ["suggestion", "outstanding_purchase", "pending_hold", "hold_placed", "closed",
                     "rejected", "hold_completed", "hold_not_picked_up", "manual", "silent"]);
                if (changedLabels.Count > 0)
                {
                    changed["duplicateStatusLabels"] = changedLabels;
                }
            }
            foreach (var name in new[] { "publicationOptions", "logoAlt" })
            {
                if (edited.ContainsKey(name))
                {
                    AddIfChanged(changed, name, edited, canonical);
                }
            }
            if (!isSystem && (edited.ContainsKey("systemNotEnabledMessage") ||
                edited.ContainsKey("misconfiguredMessage")))
            {
                throw new InvalidOperationException("System-only fields cannot be saved in library context.");
            }
            if (changed.Count > 0)
            {
                patch["ui_text"] = changed;
            }
        }

        if (submitted.ContainsKey("emails"))
        {
            var edited = RequireObject(submitted["emails"], "emails");
            ValidateKeys(edited, new[] { "fromAddress", "fromName", "postmarkToken", "clearPostmarkToken",
                "rejection_templates" }.Concat(StandardTemplateKeys));
            var canonical = Object(displayed["emails"]);
            var changed = ChangedProperties(edited, canonical, ["fromAddress", "fromName"]);
            var token = Text(edited["postmarkToken"]);
            if (!string.IsNullOrWhiteSpace(token))
            {
                changed["postmarkToken"] = token;
            }
            if (Bool(edited["clearPostmarkToken"]))
            {
                changed["clearPostmarkToken"] = true;
            }
            var templateChanges = BuildTemplateChanges(edited, displayed, source, isSystem);
            foreach (var (key, value) in templateChanges)
            {
                changed[key] = Copy(value);
            }
            if (changed.Count > 0)
            {
                patch["emails"] = changed;
            }
        }

        if (submitted.ContainsKey("providers"))
        {
            patch["providers"] = BuildProviderChanges(
                RequireArray(submitted["providers"], "providers"),
                Array(displayed["providers"]), Array(Object(source["stored"])["providers"]));
            if (Array(patch["providers"]).Count == 0)
            {
                patch.Remove("providers");
            }
        }
        if (submitted.ContainsKey("formats") || submitted.ContainsKey("formatRules"))
        {
            BuildFormatChanges(submitted, displayed, source, patch, isSystem);
        }
        if (submitted.ContainsKey("customFields"))
        {
            if (isSystem)
            {
                throw new InvalidOperationException("Custom field definitions belong to a library.");
            }
            var fields = RequireArray(submitted["customFields"], "customFields");
            ValidateDefinitions(fields);
            if (!Same(fields, Object(displayed["uiText"])["additionalFieldDefinitions"]))
            {
                patch["customFields"] = Copy(fields);
            }
        }
        if (submitted.ContainsKey("formatClaimRules"))
        {
            if (isSystem)
            {
                throw new InvalidOperationException("Auto-claim rules belong to a library.");
            }
            var claims = RequireArray(submitted["formatClaimRules"], "formatClaimRules");
            var canonical = Array(displayed["autoClaimRules"]);
            var desired = MapClaimRules(claims, Array(Object(source["stored"])["formats"]));
            if (!SameClaimRules(claims, canonical))
            {
                patch["formatClaimRules"] = desired;
            }
        }

        var systemKeys = new[]
        {
            "staffUrl", "leapBibUrlPattern", "leapPatronUrlPattern", "formatIconUrlPattern",
            "patronEmbedAllowedOrigins", "enabledLibraryOrgIds"
        };
        if (!isSystem && (submitted.ContainsKey("polaris") || systemKeys.Any(submitted.ContainsKey)))
        {
            throw new InvalidOperationException("System-only fields cannot be saved in library context.");
        }
        if (isSystem)
        {
            var currentSystem = Object(displayed["systemSettings"]);
            foreach (var name in systemKeys)
            {
                if (submitted.ContainsKey(name))
                {
                    if (name == "patronEmbedAllowedOrigins")
                    {
                        var origins = new JsonArray();
                        foreach (var origin in (Text(submitted[name]) ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            origins.Add(origin.TrimEnd('\r'));
                        }
                        if (!Same(origins, currentSystem[name]))
                        {
                            patch[name] = origins;
                        }
                    }
                    else
                    {
                        AddIfChanged(patch, name, submitted, currentSystem);
                    }
                }
            }
            if (submitted.ContainsKey("polaris"))
            {
                var edited = RequireObject(submitted["polaris"], "polaris");
                ValidateKeys(edited, ["host", "accessId", "staffDomain", "adminUser", "workstationId",
                    "systemPolarisUserId", "organizationIdForRequests", "pickupOrganizationId", "apiKey",
                    "adminPassword", "clearApiKey", "clearAdminPassword"]);
                var current = Object(displayed["polaris"]);
                var changed = ChangedProperties(edited, current,
                    ["host", "accessId", "staffDomain", "adminUser", "workstationId",
                     "systemPolarisUserId", "organizationIdForRequests", "pickupOrganizationId"]);
                foreach (var secret in new[] { "apiKey", "adminPassword" })
                {
                    var value = Text(edited[secret]);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        changed[secret] = value;
                    }
                }
                foreach (var clear in new[] { "clearApiKey", "clearAdminPassword" })
                {
                    if (Bool(edited[clear]))
                    {
                        changed[clear] = true;
                    }
                }
                if (changed.Count > 0)
                {
                    patch["polaris"] = changed;
                }
            }
        }
        return patch;
    }

    private static JsonObject ChangedProperties(JsonObject edited, JsonObject canonical, IReadOnlyCollection<string> names)
    {
        var changed = new JsonObject();
        foreach (var name in names)
        {
            if (edited.ContainsKey(name))
            {
                AddIfChanged(changed, name, edited, canonical);
            }
        }
        return changed;
    }

    private static void ValidateKeys(JsonObject value, IEnumerable<string> names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        if (value.Any(item => !allowed.Contains(item.Key)))
        {
            throw new InvalidOperationException("The Settings form contains an unsupported field.");
        }
    }

    private static void AddIfChanged(JsonObject patch, string name, JsonObject edited, JsonObject canonical)
    {
        if (!Same(edited[name], canonical[name]))
        {
            patch[name] = Copy(edited[name]);
        }
    }

    private static JsonObject RequireObject(JsonNode? node, string name) => node as JsonObject ??
        throw new InvalidOperationException($"{name} must be an object.");
    private static JsonArray RequireArray(JsonNode? node, string name) => node as JsonArray ??
        throw new InvalidOperationException($"{name} must be an array.");
    private static bool Same(JsonNode? left, JsonNode? right) =>
        JsonNode.DeepEquals(left, right);

    private static JsonObject BuildTemplateChanges(JsonObject edited, JsonObject displayed, JsonObject source, bool isSystem)
    {
        var result = new JsonObject();
        var rows = Array(displayed["templates"]).OfType<JsonObject>().ToArray();
        var sourceRows = Array(source["templateEditor"]).OfType<JsonObject>().ToArray();
        foreach (var key in StandardTemplateKeys)
        {
            if (!edited.ContainsKey(key))
            {
                continue;
            }
            var value = RequireObject(edited[key], key);
            ValidateKeys(value, ["subject", "body"]);
            var row = rows.SingleOrDefault(item => Text(item["templateKey"]) == key && !Bool(item["isCustom"]));
            var sourceRow = sourceRows.SingleOrDefault(item => Text(item["templateKey"]) == key);
            var content = BuildTemplateContent(value, row, sourceRow, key, isSystem);
            if (content is not null)
            {
                result[key] = content;
            }
        }

        if (edited.ContainsKey("rejection_templates"))
        {
            var changes = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in RequireArray(edited["rejection_templates"], "rejection_templates"))
            {
                var value = RequireObject(node, "rejection template");
                ValidateKeys(value, ["templateKey", "name", "subject", "body", "enabled", "reset"]);
                var key = Text(value["templateKey"]);
                if (key is null || !key.StartsWith("rejection:", StringComparison.Ordinal) || !seen.Add(key))
                {
                    throw new InvalidOperationException("Each rejection template needs a unique key.");
                }
                var row = rows.SingleOrDefault(item => Text(item["templateKey"]) == key);
                if (row is null && Bool(value["reset"]))
                {
                    throw new InvalidOperationException("An unknown rejection template cannot be reset.");
                }
                if (row is null && !isSystem && !key.StartsWith("rejection:custom_", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("An unknown system rejection template cannot be edited in library context.");
                }
                var sourceRow = sourceRows.SingleOrDefault(item => Text(item["templateKey"]) == key);
                var content = BuildTemplateContent(value, row, sourceRow, key, isSystem);
                if (content is not null)
                {
                    changes.Add(content);
                }
            }
            if (changes.Count > 0)
            {
                result["rejection_templates"] = changes;
            }
        }
        return result;
    }

    private static JsonObject? BuildTemplateContent(
        JsonObject edited, JsonObject? row, JsonObject? sourceRow, string key, bool isSystem)
    {
        if (row is null && !isSystem && !key.StartsWith("rejection:custom_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The system has no {key} template to inherit.");
        }
        var content = new JsonObject { ["templateKey"] = key };
        if (!isSystem && sourceRow is not null)
        {
            if (Bool(sourceRow["isCustom"]))
            {
                content["isCustom"] = true;
            }
            else
            {
                content["sourceTemplateId"] = Text(sourceRow["sourceTemplateId"]);
            }
        }
        if (!isSystem && row is null)
        {
            content["isCustom"] = true;
        }
        if (Bool(edited["reset"]))
        {
            if (isSystem)
            {
                throw new InvalidOperationException("System templates cannot be reset.");
            }
            content["reset"] = true;
            return content;
        }
        foreach (var name in new[] { "subject", "body", "enabled" })
        {
            if (edited.ContainsKey(name) && (row is null || !Same(edited[name], row[name])))
            {
                content[name] = Copy(edited[name]);
            }
        }
        if (edited.ContainsKey("name") && (row is null || !Same(edited["name"], row["name"])))
        {
            content["displayName"] = Copy(edited["name"]);
        }
        if (row is null && (!content.ContainsKey("subject") || !content.ContainsKey("body")))
        {
            throw new InvalidOperationException("A new template requires both subject and body.");
        }
        return content.Count > (isSystem ? 1 : 2) ? content : null;
    }

    private static JsonArray BuildProviderChanges(JsonArray edited, JsonArray canonical, JsonArray source)
    {
        var current = canonical.OfType<JsonObject>().Where(row => Text(row["key"]) is not null)
            .ToDictionary(row => Text(row["key"])!, StringComparer.Ordinal);
        var sourceByKey = source.OfType<JsonObject>().Where(row => Text(row["key"]) is not null)
            .ToDictionary(row => Text(row["key"])!, StringComparer.Ordinal);
        var changes = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in edited)
        {
            var provider = RequireObject(node, "provider");
            ValidateKeys(provider, ["key", "isEnabled", "label", "urlTemplate"]);
            var key = Text(provider["key"]);
            if (key is null || key.Length != "external_search_1".Length ||
                !key.StartsWith("external_search_", StringComparison.Ordinal) || key[^1] is < '1' or > '4' ||
                !seen.Add(key) ||
                !provider.ContainsKey("isEnabled") || !provider.ContainsKey("label") ||
                !provider.ContainsKey("urlTemplate"))
            {
                throw new InvalidOperationException("The provider editor is incomplete or contains an unknown provider.");
            }
            if (!current.TryGetValue(key, out var baseline))
            {
                if (!Bool(provider["isEnabled"]) && string.IsNullOrWhiteSpace(Text(provider["label"])) &&
                    string.IsNullOrWhiteSpace(Text(provider["urlTemplate"])))
                {
                    seen.Remove(key);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(Text(provider["label"])) ||
                    string.IsNullOrWhiteSpace(Text(provider["urlTemplate"])))
                {
                    throw new InvalidOperationException("A new provider requires a label and URL template.");
                }
                changes.Add(new JsonObject
                {
                    ["key"] = key,
                    ["isEnabled"] = Copy(provider["isEnabled"]),
                    ["label"] = Copy(provider["label"]),
                    ["urlTemplate"] = Copy(provider["urlTemplate"]),
                    ["sortOrder"] = (key[^1] - '0') * 10
                });
                continue;
            }
            if (Same(provider["isEnabled"], baseline["isEnabled"]) &&
                Same(provider["label"], baseline["label"]) &&
                Same(provider["urlTemplate"], baseline["urlTemplate"]))
            {
                continue;
            }
            changes.Add(new JsonObject
            {
                ["id"] = Text(sourceByKey[key]["id"]),
                ["key"] = key,
                ["isEnabled"] = Copy(provider["isEnabled"]),
                ["label"] = Copy(provider["label"]),
                ["urlTemplate"] = Copy(provider["urlTemplate"]),
                ["sortOrder"] = Copy(sourceByKey[key]["sortOrder"])
            });
        }
        if (!current.Keys.All(seen.Contains))
        {
            throw new InvalidOperationException("The provider editor did not include every configured provider.");
        }
        return changes;
    }

    private static void ValidateDefinitions(JsonArray edited)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in edited)
        {
            var field = RequireObject(node, "custom field");
            ValidateKeys(field, ["id", "key", "label", "type", "helpText", "enabled", "sortOrder", "options"]);
            var key = Text(field["key"]);
            if (key is null || !seen.Add(key) || field["options"] is not JsonArray)
            {
                throw new InvalidOperationException("The custom field editor is incomplete or contains duplicate keys.");
            }
        }
    }

    private static JsonArray MapClaimRules(JsonArray edited, JsonArray formats)
    {
        var byCode = formats.OfType<JsonObject>().Where(item => Text(item["code"]) is not null)
            .ToDictionary(item => Text(item["code"])!, StringComparer.Ordinal);
        var desired = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in edited)
        {
            var claim = RequireObject(node, "auto-claim rule");
            ValidateKeys(claim, ["format", "staffUserId"]);
            var code = Text(claim["format"]);
            var staff = Text(claim["staffUserId"]);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(staff) || !seen.Add(code))
            {
                throw new InvalidOperationException("Auto-claim rules need unique formats and staff users.");
            }
            if (byCode.TryGetValue(code, out var format))
            {
                desired.Add(new JsonObject
                {
                    ["materialFormatId"] = Text(format["id"]),
                    ["staffUserId"] = staff,
                    ["active"] = true
                });
            }
            else
            {
                // The owning settings transaction resolves a format created in this same save.
                desired.Add(new JsonObject
                {
                    ["formatCode"] = code,
                    ["staffUserId"] = staff,
                    ["active"] = true
                });
            }
        }
        return desired;
    }

    private static bool SameClaimRules(JsonArray mapped, JsonArray canonical)
    {
        static string Key(JsonObject value) =>
            $"{Text(value["format"])}:{Text(value["staffUserId"])}";
        return mapped.OfType<JsonObject>().Select(Key).Order(StringComparer.Ordinal)
            .SequenceEqual(canonical.OfType<JsonObject>().Select(Key).Order(StringComparer.Ordinal));
    }

    private static void BuildFormatChanges(
        JsonObject submitted,
        JsonObject displayed,
        JsonObject source,
        JsonObject patch,
        bool isSystem)
    {
        var canonicalFormats = Array(Object(source["stored"])["formats"]).OfType<JsonObject>()
            .Where(item => Text(item["code"]) is not null)
            .ToDictionary(item => Text(item["code"])!, StringComparer.Ordinal);
        var desiredFormats = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (submitted.ContainsKey("formats"))
        {
            foreach (var node in RequireArray(submitted["formats"], "formats"))
            {
                var row = RequireObject(node, "format");
                ValidateKeys(row, ["code", "label", "sortOrder", "isEnabled", "messageBehavior",
                    "message", "titleMode", "titleLabel", "authorMode", "authorLabel",
                    "identifierMode", "identifierLabel", "publicationMode", "publicationLabel"]);
                var code = Text(row["code"]);
                if (string.IsNullOrWhiteSpace(code) || !desiredFormats.TryAdd(code, row))
                {
                    throw new InvalidOperationException("Format codes must be unique and nonblank.");
                }
            }
        }
        var changes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (code, edited) in desiredFormats)
        {
            if (!canonicalFormats.TryGetValue(code, out var baseline))
            {
                if (isSystem)
                {
                    throw new InvalidOperationException("A new system format cannot be created from the legacy editor.");
                }
                if (!edited.ContainsKey("label") || !edited.ContainsKey("isEnabled"))
                {
                    throw new InvalidOperationException("A new custom format needs a label and enabled state.");
                }
                var created = CopyObject(edited);
                created["ownerOrganizationId"] = Text(displayed["orgId"]);
                changes[code] = created;
                continue;
            }
            var change = new JsonObject
            {
                ["id"] = Text(baseline["id"]),
                ["code"] = code
            };
            foreach (var name in FormatFields)
            {
                if (edited.ContainsKey(name) && !Same(edited[name], baseline[name]))
                {
                    change[name] = Copy(edited[name]);
                }
            }
            if (change.Count > 2)
            {
                changes[code] = change;
            }
        }

        if (submitted.ContainsKey("formatRules"))
        {
            var editedRules = RequireObject(submitted["formatRules"], "formatRules");
            var baselineRules = Object(Object(displayed["uiText"])["formatRules"]);
            var fields = Array(Object(source["stored"])["customFields"]);
            var editedDefinitions = submitted.ContainsKey("customFields")
                ? RequireArray(submitted["customFields"], "customFields") : fields;
            var enabledKeys = editedDefinitions.OfType<JsonObject>().Where(item => Bool(item["enabled"]))
                .Select(item => Text(item["key"])).Where(key => key is not null)
                .ToHashSet(StringComparer.Ordinal);
            var fullMatrix = CopyObject(baselineRules);
            foreach (var code in desiredFormats.Keys)
            {
                if (!fullMatrix.ContainsKey(code))
                {
                    fullMatrix[code] = new JsonObject { ["customFields"] = new JsonObject() };
                }
            }
            var matrixChanged = false;
            foreach (var (code, node) in editedRules)
            {
                if (!canonicalFormats.ContainsKey(code) && !desiredFormats.ContainsKey(code))
                {
                    throw new InvalidOperationException("A format rule references an unknown format.");
                }
                var edited = RequireObject(node, "format rule");
                ValidateKeys(edited, ["messageBehavior", "message", "fields", "customFields"]);
                var canonical = Object(baselineRules[code]);
                var change = changes.GetValueOrDefault(code) ?? new JsonObject
                {
                    ["id"] = Text(canonicalFormats.GetValueOrDefault(code)?["id"]),
                    ["code"] = code
                };
                foreach (var name in new[] { "messageBehavior", "message" })
                {
                    if (edited.ContainsKey(name) && !Same(edited[name], canonical[name]))
                    {
                        change[name] = Copy(edited[name]);
                    }
                }
                if (edited.ContainsKey("fields"))
                {
                    var editedFields = RequireObject(edited["fields"], "format fields");
                    ValidateKeys(editedFields, ["title", "author", "identifier", "publication"]);
                    foreach (var (field, fieldNode) in editedFields)
                    {
                        var value = RequireObject(fieldNode, "format field");
                        ValidateKeys(value, ["mode", "label"]);
                        var current = Object(Object(canonical["fields"])[field]);
                        foreach (var name in new[] { "mode", "label" })
                        {
                            if (value.ContainsKey(name) && !Same(value[name], current[name]))
                            {
                                change[$"{field}{char.ToUpperInvariant(name[0])}{name[1..]}"] = Copy(value[name]);
                            }
                        }
                    }
                }
                if (change.Count > 2)
                {
                    changes[code] = change;
                }
                if (!edited.ContainsKey("customFields"))
                {
                    continue;
                }
                var editedCustom = RequireObject(edited["customFields"], "custom field rules");
                var currentCustom = Object(canonical["customFields"]);
                var fullCustom = RequireObject(Object(fullMatrix[code])["customFields"], "custom field rules");
                foreach (var (key, ruleNode) in editedCustom)
                {
                    if (!enabledKeys.Contains(key))
                    {
                        // Disabled definitions are not editable; keep their authoritative raw rules.
                        continue;
                    }
                    var rule = RequireObject(ruleNode, "custom field rule");
                    ValidateKeys(rule, ["mode", "labelOverride"]);
                    if (!Same(rule, currentCustom[key]))
                    {
                        if (Text(rule["mode"]) == "hidden")
                        {
                            fullCustom.Remove(key);
                        }
                        else
                        {
                            fullCustom[key] = Copy(rule);
                        }
                        matrixChanged = true;
                    }
                }
            }
            if (matrixChanged)
            {
                var matrix = new JsonObject();
                foreach (var (code, node) in fullMatrix)
                {
                    matrix[code] = new JsonObject
                    {
                        ["customFields"] = Copy(Object(node)["customFields"])
                    };
                }
                patch["formatRules"] = matrix;
            }
        }
        if (changes.Count > 0)
        {
            var items = new JsonArray();
            foreach (var value in changes.Values)
            {
                items.Add(value);
            }
            patch["formats"] = items;
        }
    }

    private static JsonObject ToObject(object? value) =>
        JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject ??
        throw new InvalidOperationException("The authoritative settings snapshot is unavailable.");

    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? new JsonObject();
    private static JsonObject CopyObject(JsonNode? value) => Copy(value) as JsonObject ?? new JsonObject();
    private static JsonArray Array(JsonNode? value) => value as JsonArray ?? new JsonArray();
    private static JsonNode? Copy(JsonNode? value) => value?.DeepClone();
    private static string? Text(JsonNode? value) => value is null ? null :
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString().Trim('"');
    private static bool Bool(JsonNode? value) => value is JsonValue scalar &&
        scalar.TryGetValue<bool>(out var result) && result;
}
