using System.Net;
using System.Text.Json;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Configuration;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Microsoft.EntityFrameworkCore;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Asap.Web.Features.Staff;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider(
    IDbContextFactory<AsapDbContext> contextFactory,
    IntegrationCredentialProtector credentialProtector,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) : IPatronProvider, IStaffPolarisProvider, IPolarisReferenceProvider
{
    private static readonly HashSet<int> DocumentedCreateNoEffectStatuses =
        [6, -4002, -4004, -4006, -4007, -4020, -4021, -4022];

    public async Task<PolarisConnectionTestResult> TestConnectionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var organizations = await GetOrganizationsAsync(cancellationToken);
            return new PolarisConnectionTestResult(true, organizations.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException exception)
        {
            return new PolarisConnectionTestResult(false, 0, exception.Code);
        }
    }

    public async Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(PolarisConfigurationValidation.SystemOrganizationId, cancellationToken);
            var rows = await LoadOrganizationsAsync(client, cancellationToken);
            return rows
                .Select(row => new PolarisOrganizationSnapshot(
                    row.Id,
                    Clean(row.DisplayName) ?? Clean(row.Name) ?? Clean(row.Abbreviation) ?? row.Id.ToString(),
                    Clean(row.Abbreviation),
                    row.OrganizationCodeId,
                    row.ParentOrganizationID))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_organizations_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(PolarisConfigurationValidation.SystemOrganizationId, cancellationToken);
            var response = await client.CallAsync(() => client.PatronCodesGetAsync(null, cancellationToken), cancellationToken);
            var result = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || result is null ||
                !TryValidatePatronCodesResponse(response.Response.Content, result))
            {
                throw new PolarisOperationalException(
                    "polaris_patron_codes_failed",
                    "Polaris did not return its patron-code reference data.");
            }

            return result.PatronCodesRows
                .OrderBy(row => row.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.PatronCodeID)
                .Select(row => new PolarisPatronCodeSnapshot(
                    row.PatronCodeID,
                    Clean(row.Description) ?? row.PatronCodeID.ToString()))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_patron_codes_failed", exception);
        }
    }

    public async Task<PatronSnapshot> AuthenticateAsync(
        string barcode,
        string pin,
        CancellationToken cancellationToken)
    {
        var (client, _) = await CreateClientAsync(PolarisConfigurationValidation.SystemOrganizationId, cancellationToken);
        try
        {
            var response = await client.CallAsync(() => client.AuthenticatePatronAsync(barcode, pin, cancellationToken), cancellationToken);
            var authentication = response.Data;
            if (response.Response?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PatronAuthenticationException("Incorrect Login - Please try again");
            }
            if (response.Response?.IsSuccessStatusCode != true)
            {
                throw new PolarisOperationalException(
                    "polaris_authentication_transport_failed",
                    "Polaris authentication was unavailable.");
            }

            if (!TryReadPapiErrorCode(response.Response.Content, out var papiErrorCode) ||
                authentication is null || authentication.PAPIErrorCode != papiErrorCode)
            {
                throw new PolarisOperationalException(
                    "polaris_authentication_protocol_failed",
                    "Polaris returned an invalid authentication response.");
            }

            var hasPatronId = HasTopLevelProperty(response.Response.Content, "PatronID");
            var authenticatedPatronId = 0;
            if (hasPatronId)
            {
                if (!TryReadPositiveTopLevelInt32(response.Response.Content, "PatronID", out authenticatedPatronId) ||
                    authentication.PatronID != authenticatedPatronId)
                {
                    throw new PolarisOperationalException(
                        "polaris_authentication_protocol_failed",
                        "Polaris returned an invalid authentication response.");
                }
            }
            else if (authentication.PatronID != 0)
            {
                throw new PolarisOperationalException(
                    "polaris_authentication_protocol_failed",
                    "Polaris returned an invalid authentication response.");
            }

            if (papiErrorCode != 0)
            {
                throw new PatronAuthenticationException("Incorrect Login - Please try again");
            }

            if (!hasPatronId || authenticatedPatronId <= 0 ||
                string.IsNullOrWhiteSpace(authentication.AccessToken) ||
                string.IsNullOrWhiteSpace(authentication.AccessSecret))
            {
                throw new PolarisOperationalException(
                    "polaris_authentication_protocol_failed",
                    "Polaris returned an incomplete authentication response.");
            }

            return await LoadPatronAsync(client, barcode, pin, authenticatedPatronId,
                requireBarcodeAlias: false, cancellationToken);
        }
        catch (PatronAuthenticationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_authentication_failed", exception);
        }
    }

    public async Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken)
    {
        var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
        try
        {
            return await LoadPatronAsync(client, barcode, string.Empty, expectedPatronId: null,
                requireBarcodeAlias: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_patron_refresh_failed", exception);
        }
    }

    public async Task<int?> GetPatronIdAsync(string barcode, int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
            var response = await client.CallAsync(() => client.PatronBasicDataGetAsync(
                barcode,
                string.Empty,
                cancellationToken: cancellationToken), cancellationToken);
            var rawContent = response.Response?.Content ?? string.Empty;
            var result = response.Data;
            if (response.Response?.IsSuccessStatusCode != true)
            {
                throw new PolarisOperationalException(
                    "polaris_patron_id_transport_failed",
                    "Polaris patron data was unavailable.");
            }

            if (!TryReadPapiErrorCode(rawContent, out var papiErrorCode) ||
                result is null || result.PAPIErrorCode != papiErrorCode || papiErrorCode != 0)
            {
                throw new PolarisOperationalException(
                    "polaris_patron_id_protocol_failed",
                    "Polaris returned an invalid patron response.");
            }

            using var document = JsonDocument.Parse(rawContent);
            var root = document.RootElement;
            if (!TryGetUniqueProperty(root, "PatronBasicData", out var rawPatron) ||
                rawPatron.ValueKind != JsonValueKind.Object ||
                HasDuplicateProperties(root) || HasDuplicateProperties(rawPatron) ||
                !TryGetUniqueProperty(rawPatron, "PatronID", out var rawPatronId) ||
                !TryReadPositiveInt32(rawPatronId, out var patronId) ||
                result.PatronBasicData is null || result.PatronBasicData.PatronID != patronId)
            {
                throw new PolarisOperationalException(
                    "polaris_patron_id_protocol_failed",
                    "Polaris returned an incomplete patron identity.");
            }

            var requestedBarcode = barcode.Trim();
            var hasCurrentBarcode = rawPatron.EnumerateObject().Any(property =>
                string.Equals(property.Name, "Barcode", StringComparison.OrdinalIgnoreCase));
            var hasFormerBarcode = rawPatron.EnumerateObject().Any(property =>
                string.Equals(property.Name, "FormerID", StringComparison.OrdinalIgnoreCase));
            string? currentBarcode = null;
            string? formerBarcode = null;
            if (hasCurrentBarcode)
            {
                if (!TryGetUniqueProperty(rawPatron, "Barcode", out var rawBarcode) ||
                    rawBarcode.ValueKind != JsonValueKind.String ||
                    Clean(rawBarcode.GetString()) is not { Length: > 0 and <= 50 } parsedBarcode ||
                    !string.Equals(parsedBarcode, Clean(result.PatronBasicData.Barcode), StringComparison.Ordinal))
                {
                    throw new PolarisOperationalException(
                        "polaris_patron_id_protocol_failed",
                        "Polaris returned an invalid patron barcode alias.");
                }
                currentBarcode = parsedBarcode;
            }
            if (hasFormerBarcode)
            {
                if (!TryGetUniqueProperty(rawPatron, "FormerID", out var rawFormerBarcode) ||
                    rawFormerBarcode.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new PolarisOperationalException(
                        "polaris_patron_id_protocol_failed",
                        "Polaris returned an invalid former patron barcode alias.");
                }
                formerBarcode = rawFormerBarcode.ValueKind == JsonValueKind.String
                    ? Clean(rawFormerBarcode.GetString())
                    : null;
                if (formerBarcode is { Length: > 50 } ||
                    !string.Equals(formerBarcode, Clean(result.PatronBasicData.FormerID), StringComparison.Ordinal))
                {
                    throw new PolarisOperationalException(
                        "polaris_patron_id_protocol_failed",
                        "Polaris returned an invalid former patron barcode alias.");
                }
            }
            if ((currentBarcode is not null || formerBarcode is not null) &&
                !string.Equals(requestedBarcode, currentBarcode, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(requestedBarcode, formerBarcode, StringComparison.OrdinalIgnoreCase))
            {
                throw new PolarisOperationalException(
                    "polaris_patron_id_protocol_failed",
                    "Polaris returned a different patron for the requested barcode.");
            }

            return patronId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_patron_id_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
        PatronSnapshot patron,
        int organizationId, CancellationToken cancellationToken)
    {
        var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
        try
        {
            if (patron.PatronOrganizationId <= 1)
            {
                throw new PolarisOperationalException("polaris_patron_organization_invalid",
                    "The patron's registered branch is missing.");
            }
            // Polaris defines pickup eligibility by the patron's live registered branch,
            // while the client carries the explicitly selected servicing library.
            var response = await client.CallAsync(() => client.PickupBranchesGetAsync(patron.PatronOrganizationId, cancellationToken), cancellationToken);
            if (response.Response?.IsSuccessStatusCode != true)
            {
                throw new PolarisOperationalException("polaris_pickup_branches_failed",
                    "Polaris pickup branches were unavailable.");
            }
            var branchIds = ReadPickupBranchIds(response);

            if (branchIds.Count == 0)
            {
                throw new PolarisOperationalException("polaris_pickup_branches_failed", "Polaris did not return pickup branches.");
            }

            var organizations = (await LoadOrganizationsAsync(client, cancellationToken))
                .ToDictionary(row => row.Id);
            return branchIds.Select(id =>
                {
                    organizations.TryGetValue(id, out var organization);
                    var label = Clean(organization?.DisplayName) ?? Clean(organization?.Name) ?? $"Branch {id}";
                    return new PickupBranch(id, label);
                })
                .OrderBy(branch => branch.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(branch => branch.Id)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_pickup_branches_failed", exception);
        }
    }

    public async Task UpdatePreferredPickupBranchAsync(
        string barcode,
        int pickupBranchId,
        int organizationId, CancellationToken cancellationToken)
    {
        DispatchAwarePapiClient? client = null;
        try
        {
            PolarisSettings settings;
            (client, settings) = await CreateMemberClientAsync(organizationId, cancellationToken);
            if (pickupBranchId <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pickupBranchId));
            }
            var update = new PatronUpdateParams
            {
                LogonBranchId = organizationId,
                LogonUserId = settings.SystemPolarisUserId!.Value,
                LogonWorkstationId = settings.WorkstationId!.Value,
                RequestPickupBranchID = pickupBranchId
            };
            var response = await client.CallAsync(() => client.PatronUpdateAsync(
                barcode, update, cancellationToken: cancellationToken), cancellationToken);
            var result = response.Data;
            if (response.Response?.IsSuccessStatusCode != true ||
                !TryReadPapiErrorCode(response.Response.Content, out var papiErrorCode) ||
                result is null || result.PAPIErrorCode != papiErrorCode)
            {
                throw new PolarisOperationalException("polaris_pickup_update_failed", "Polaris did not confirm the pickup preference update.");
            }
            // PatronRegistrationUpdate documents invalid patron/pickup identifiers
            // as validation rejections. This request changes only the pickup field.
            if (papiErrorCode is -3000 or -3622)
            {
                throw new PolarisPickupRejectedException(papiErrorCode);
            }
            if (papiErrorCode != 0)
            {
                throw new PolarisOperationalException("polaris_pickup_update_failed", "Polaris did not confirm the pickup preference update.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (client?.MutationDispatched == true &&
                                          exception is JsonException or Newtonsoft.Json.JsonException)
        {
            // A malformed response after the PUT may follow a successful write. Keep
            // the operation in the durable ambiguous-outcome path.
            throw Operational("polaris_pickup_update_protocol_failed", exception);
        }
        catch (Exception exception) when (client?.MutationDispatched != true)
        {
            throw new PolarisMutationNotDispatchedException(exception);
        }
    }

    public async Task<IdentifierLookupResult> LookupIdentifierAsync(
        string identifier,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
            var branch = organizationId;
            var attempts = new List<SearchAttempt>();

            var isbn = InspectSearchResponse(
                await client.CallAsync(() => client.BibSearchAsync(
                    SearchOptions(identifier, branch, SearchQualifiers.ISBN),
                    cancellationToken), cancellationToken), cancellationToken);
            if (isbn.Failure.HasValue)
            {
                return FailureResult(isbn.Failure.Value);
            }
            attempts.Add(isbn);

            var upcOptions = SearchOptions(identifier, branch, SearchQualifiers.KW);
            upcOptions.SearchType = BibSearchTypes.boolean;
            upcOptions.Term = "UPC=" + QuoteSearch(identifier.Trim());
            var upc = InspectSearchResponse(await client.CallAsync(() => client.BibSearchAsync(upcOptions, cancellationToken), cancellationToken), cancellationToken);
            if (upc.Failure.HasValue)
            {
                return FailureResult(upc.Failure.Value);
            }
            attempts.Add(upc);

            var lccn = InspectSearchResponse(
                await client.CallAsync(() => client.BibSearchAsync(
                    SearchOptions(identifier, branch, SearchQualifiers.LCCN),
                    cancellationToken), cancellationToken), cancellationToken);
            if (lccn.Failure.HasValue)
            {
                return FailureResult(lccn.Failure.Value);
            }
            attempts.Add(lccn);

            var allRows = attempts.SelectMany(item => item.Data!.BibSearchRows)
                .Where(item => item.ControlNumber > 0)
                .GroupBy(item => item.ControlNumber)
                .Select(group => group.First())
                .ToArray();
            var filteredByMaterialType = false;
            var rows = allRows.Where(row =>
                {
                    var excluded = row.PrimaryTypeOfMaterial is 36 or 41;
                    filteredByMaterialType |= excluded;
                    return !excluded;
                })
                .ToArray();
            if (rows.Length == 0)
            {
                return new IdentifierLookupResult(
                    IdentifierLookupOutcome.DefinitiveNotFound,
                    FilteredByMaterialType: filteredByMaterialType);
            }

            var selected = rows[0];
            string? catalogTitle = selected.Title;
            string? catalogAuthor = selected.Author;
            try
            {
                var detailResponse = await client.CallAsync(() => client.BibGetAsync(
                    selected.ControlNumber,
                    branch,
                    cancellationToken), cancellationToken);
                if (detailResponse.Response?.IsSuccessStatusCode == true &&
                    detailResponse.Data is { PAPIErrorCode: >= 0 } detail)
                {
                    catalogTitle = Clean(detail.Title) ?? catalogTitle;
                    catalogAuthor = Clean(detail.Author.FirstOrDefault()) ?? catalogAuthor;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedProviderFailure(exception))
            {
                // Search success remains authoritative when optional detail reconciliation fails.
            }

            return new IdentifierLookupResult(
                IdentifierLookupOutcome.Found,
                selected.ControlNumber,
                MultipleMatches: rows.Length > 1 || attempts.Any(item => item.Data!.TotalRecordsFound > 1),
                FilteredByMaterialType: filteredByMaterialType,
                CatalogTitle: catalogTitle,
                CatalogAuthor: catalogAuthor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception) when (IsTransient(exception.StatusCode))
        {
            return new IdentifierLookupResult(
                IdentifierLookupOutcome.TransientFailure,
                ErrorCode: "polaris_transport_transient");
        }
        catch (TimeoutException)
        {
            return new IdentifierLookupResult(
                IdentifierLookupOutcome.TransientFailure,
                ErrorCode: "polaris_timeout");
        }
        catch (Exception exception) when (exception is PolarisOperationalException || IsExpectedProviderFailure(exception))
        {
            return OperationalResult("polaris_search_operational_failure");
        }
    }

    public async Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bibId <= 0)
        {
            return new BibValidationResult(false);
        }
        try
        {
            var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
            var branchId = organizationId;
            var response = await client.CallAsync(() => client.BibGetAsync(bibId, branchId, cancellationToken), cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true)
            {
                throw new PolarisOperationalException(
                    "polaris_bib_validation_transport_failed",
                    "Polaris BIB validation was unavailable.");
            }

            var rawContent = response.Response.Content;
            if (data is null ||
                !TryReadPapiErrorCode(rawContent, out var papiErrorCode) ||
                data.PAPIErrorCode != papiErrorCode)
            {
                throw new PolarisOperationalException(
                    "polaris_bib_validation_protocol_failed",
                    "Polaris returned an invalid BIB validation response.");
            }

            if (papiErrorCode < 0)
            {
                if (papiErrorCode == -1 && IsDefinitiveInvalidBibResponse(rawContent))
                {
                    return new BibValidationResult(false);
                }

                throw new PolarisOperationalException(
                    "polaris_bib_validation_failed",
                    "Polaris could not complete BIB validation.");
            }

            if (!TryReadUsableBibGetRows(rawContent, out var rawRowCount) ||
                data.BibGetRows is null || data.BibGetRows.Count == 0 || data.BibGetRows.Count != rawRowCount ||
                data.BibGetRows.Any(row => row is null || row.ElementID <= 0 || row.Value is null))
            {
                throw new PolarisOperationalException(
                    "polaris_bib_validation_protocol_failed",
                    "Polaris returned an incomplete BIB validation response.");
            }

            var publication = data.BibGetRows
                .Where(row => string.Equals(row.Label, "Publication Date", StringComparison.OrdinalIgnoreCase))
                .Select(row => Clean(row.Value))
                .FirstOrDefault(value => value is not null);
            return new BibValidationResult(
                true,
                Clean(data.Title),
                Clean(data.Author?.FirstOrDefault()),
                publication,
                Clean(data.Format),
                Clean(data.ISBN) ?? Clean(data.ISSN) ?? Clean(data.UPC?.FirstOrDefault()),
                Clean(data.Publisher?.FirstOrDefault()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_bib_validation_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
        string barcode,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
            var response = await client.CallAsync(() => client.PatronHoldRequestsGetAsync(
                barcode,
                PatronHoldStatus.all,
                password: string.Empty,
                cancellationToken), cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null)
            {
                throw new PolarisOperationalException("polaris_hold_read_failed", "Polaris hold data was unavailable.");
            }

            using var document = JsonDocument.Parse(response.Response.Content ?? string.Empty);
            if (!TryValidatePatronHoldResponse(document.RootElement, data))
            {
                throw new PolarisOperationalException(
                    "polaris_hold_read_failed",
                    "Polaris returned an incomplete hold response.");
            }

            return data.PatronHoldRequestsGetRows
                .Select(item => new PolarisHoldSnapshot(
                    item.HoldRequestID,
                    item.BibID,
                    item.StatusID,
                    Clean(item.StatusDescription),
                    item.PickupBranchID > PolarisConfigurationValidation.SystemOrganizationId
                        ? item.PickupBranchID
                        : null,
                    barcode))
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_hold_read_failed", exception);
        }
    }

    private static bool TryValidatePatronHoldResponse(
        JsonElement root,
        PatronHoldRequestsGetResult data)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(root, "PAPIErrorCode", out var codeElement) ||
            codeElement.ValueKind != JsonValueKind.Number ||
            !codeElement.TryGetInt32(out var code) ||
            code != 0 || data.PAPIErrorCode != code ||
            !TryGetUniqueProperty(root, "PatronHoldRequestsGetRows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array ||
            data.PatronHoldRequestsGetRows is null ||
            data.PatronHoldRequestsGetRows.Count != rows.GetArrayLength())
        {
            return false;
        }

        var rowIndex = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var model = data.PatronHoldRequestsGetRows[rowIndex++];
            if (row.ValueKind != JsonValueKind.Object || model is null ||
                !TryGetUniqueProperty(row, "StatusID", out var statusIdElement) ||
                !TryReadPositiveInt32(statusIdElement, out var statusId) ||
                !IsPolarisHoldStatusId(statusId) ||
                statusId != model.StatusID ||
                !TryGetUniqueProperty(row, "StatusDescription", out var statusDescriptionElement) ||
                statusDescriptionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var statusDescription = Clean(statusDescriptionElement.GetString());
            var modelStatusDescription = Clean(model.StatusDescription);
            if (statusDescription is null || modelStatusDescription is null ||
                !string.Equals(statusDescription, modelStatusDescription, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!TryGetUniqueProperty(row, "HoldRequestID", out var holdRequestIdElement) ||
                !TryReadPositiveInt32(holdRequestIdElement, out var holdRequestId) ||
                holdRequestId != model.HoldRequestID ||
                !TryGetUniqueProperty(row, "BibID", out var bibIdElement) ||
                !TryReadPositiveInt32(bibIdElement, out var bibId) ||
                bibId != model.BibID)
            {
                return false;
            }

            var hasPickupBranchId = false;
            var pickupBranchIdElement = default(JsonElement);
            foreach (var property in row.EnumerateObject())
            {
                if (!string.Equals(property.Name, "PickupBranchID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (hasPickupBranchId)
                {
                    return false;
                }

                hasPickupBranchId = true;
                pickupBranchIdElement = property.Value;
            }

            if (!hasPickupBranchId)
            {
                if (model.PickupBranchID != 0)
                {
                    return false;
                }
            }
            else if (pickupBranchIdElement.ValueKind != JsonValueKind.Number ||
                     !pickupBranchIdElement.TryGetInt32(out var pickupBranchId) ||
                     pickupBranchId < 0 || pickupBranchId != model.PickupBranchID)
            {
                return false;
            }
        }

        return true;
    }

    // PatronHoldRequestsGet documents these status IDs; zero and all unlisted values are invalid.
    private static bool IsPolarisHoldStatusId(int statusId) =>
        statusId is 1 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 16;

    public async Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(
        string barcode,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
            var response = await client.CallAsync(() => client.PatronItemsOutGetAsync(
                barcode,
                PatronItemsOutGetStatus.All,
                password: string.Empty,
                cancellationToken), cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null ||
                !TryValidateCheckoutResponse(response.Response.Content, data))
            {
                throw new PolarisOperationalException("polaris_checkout_read_failed", "Polaris checkout data was unavailable.");
            }

            return data.PatronItemsOutGetRows
                .Select(item => new PolarisCheckoutSnapshot(item.BibID, PatronBarcode: barcode))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_checkout_read_failed", exception);
        }
    }

    public async Task<HoldProviderResult> CreateHoldAsync(
        HoldCreateCommand command,
        CancellationToken cancellationToken)
    {
        DispatchAwarePapiClient? client = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(command);
            if (command.PatronId <= 0 || command.BibId <= 0 || command.PickupBranchId <= 1 ||
                command.WorkstationId <= 0 || command.PolarisUserId <= 0)
            {
                throw new ArgumentException("Hold creation requires explicit native patron, BIB, pickup and integration identities.", nameof(command));
            }
            (client, _) = await CreateMemberClientAsync(command.RequestingOrganizationId, cancellationToken);
            var response = await client.CallAsync(() => client.HoldRequestCreateAsync(new HoldRequestCreateParams
            {
                PatronID = command.PatronId,
                BibID = command.BibId,
                PickupOrgID = command.PickupBranchId,
                RequestingOrgID = command.RequestingOrganizationId,
                WorkstationID = command.WorkstationId,
                UserID = command.PolarisUserId
            }, cancellationToken), cancellationToken);
            return NormalizeHoldResponse(
                response.Response?.IsSuccessStatusCode == true,
                response.Response?.Content,
                response.Data,
                isReply: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (client?.MutationDispatched != true)
        {
            throw new PolarisMutationNotDispatchedException(exception);
        }
    }

    public async Task<HoldProviderResult> ReplyToHoldAsync(
        HoldReplyCommand command,
        CancellationToken cancellationToken)
    {
        DispatchAwarePapiClient? client = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(command);
            if (command.RequestGuid == Guid.Empty || string.IsNullOrWhiteSpace(command.TxnGroupQualifier) ||
                string.IsNullOrWhiteSpace(command.TxnQualifier))
            {
                throw new ArgumentException("A hold reply requires the original conversation identity and qualifiers.", nameof(command));
            }
            (client, _) = await CreateMemberClientAsync(command.RequestingOrganizationId, cancellationToken);
            var createContext = new HoldRequestCreateResult
            {
                RequestGuid = command.RequestGuid,
                TxnGroupQualifer = command.TxnGroupQualifier,
                TxnQualifier = command.TxnQualifier
            };
            var response = await client.CallAsync(() => client.HoldRequestReplyAsync(
                createContext,
                command.RequestingOrganizationId,
                HoldRequestReplyAnswer.Yes,
                HoldRequestReplyState.AcceptEvenWithExistingHolds,
                cancellationToken), cancellationToken);
            return NormalizeHoldResponse(
                response.Response?.IsSuccessStatusCode == true,
                response.Response?.Content,
                response.Data,
                isReply: true,
                replyContext: command);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (client?.MutationDispatched != true)
        {
            throw new PolarisMutationNotDispatchedException(exception);
        }
    }

    internal static HoldProviderResult NormalizeHoldResponse(
        bool httpSucceeded,
        string? content,
        HoldRequestCreateResult? data,
        bool isReply,
        HoldReplyCommand? replyContext = null)
    {
        if (!httpSucceeded || string.IsNullOrWhiteSpace(content))
        {
            return Ambiguous(isReply, "provider_http_error");
        }
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            // The package owns deserialization. Raw validation detects absent/defaulted or
            // duplicate fields before an external write is classified as a definitive outcome.
            if (data is null || root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Select(property => property.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != root.EnumerateObject().Count() ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var errorCode) ||
                errorCode.ValueKind != JsonValueKind.Number || !errorCode.TryGetInt32(out var papiErrorCode) ||
                papiErrorCode != data.PAPIErrorCode ||
                !TryGetUniqueProperty(root, "StatusType", out var rawType) ||
                rawType.ValueKind != JsonValueKind.Number || !rawType.TryGetInt32(out var statusType) ||
                statusType != data.StatusType ||
                !TryGetUniqueProperty(root, "StatusValue", out var rawValue) ||
                rawValue.ValueKind != JsonValueKind.Number || !rawValue.TryGetInt32(out var statusValue) ||
                statusValue != data.StatusValue)
            {
                return Ambiguous(isReply, "provider_protocol_error");
            }
            if (papiErrorCode != 0)
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.Ambiguous,
                    null,
                    null,
                    null,
                    null,
                    statusType,
                    statusValue,
                    isReply ? "reply_papi_error" : "create_papi_error",
                    "provider_papi_error");
            }

            if (!TryReadHoldConversationIdentity(root, data, statusType, statusValue, isReply, replyContext,
                    out var requestGuid, out var group, out var qualifier))
            {
                return Ambiguous(isReply, "provider_protocol_error");
            }
            // Create/reply return a conversation GUID, not a SysHoldRequestID. Final hold
            // identity is established independently from the patron's typed hold listing.
            if (!isReply && statusType == 3 && statusValue == 5 &&
                requestGuid is { } guid && guid != Guid.Empty &&
                !string.IsNullOrWhiteSpace(group) && !string.IsNullOrWhiteSpace(qualifier))
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.ReplyRequired,
                    requestGuid,
                    null,
                    group,
                    qualifier,
                    statusType,
                    statusValue,
                    "create_status_5_reply_required");
            }
            if (statusType == 2 && statusValue == 1)
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.FinalSuccess,
                    requestGuid,
                    null,
                    group,
                    qualifier,
                    statusType,
                    statusValue,
                    isReply ? "documented_reply_success" : "documented_create_success");
            }
            if (statusType == 2 && statusValue == 0 ||
                !isReply && statusType == 1 && DocumentedCreateNoEffectStatuses.Contains(statusValue))
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.DefinitiveNoEffect,
                    requestGuid,
                    null,
                    group,
                    qualifier,
                    statusType,
                    statusValue,
                    isReply ? "documented_reply_rejection" : "documented_create_rejection",
                    $"provider_status_{statusValue}");
            }
            return new HoldProviderResult(
                HoldProviderOutcome.Ambiguous,
                requestGuid,
                null,
                group,
                qualifier,
                statusType,
                statusValue,
                isReply ? "reply_status_unclassified" : "create_status_unclassified",
                "provider_status_unclassified");
        }
        catch (JsonException)
        {
            return Ambiguous(isReply, "provider_protocol_error");
        }
    }

    private static HoldProviderResult Ambiguous(bool isReply, string code) => new(
        HoldProviderOutcome.Ambiguous,
        null,
        null,
        null,
        null,
        null,
        null,
        isReply ? "reply_response_ambiguous" : "create_response_ambiguous",
        code);

    private async Task<PatronSnapshot> LoadPatronAsync(
        DispatchAwarePapiClient client,
        string barcode,
        string pin,
        int? expectedPatronId,
        bool requireBarcodeAlias,
        CancellationToken cancellationToken)
    {
        var response = await client.CallAsync(() => client.PatronBasicDataGetAsync(
            barcode,
            pin,
            cancellationToken: cancellationToken), cancellationToken);
        var rawContent = response.Response?.Content ?? string.Empty;
        var result = response.Data;
        var patron = result?.PatronBasicData;
        if (response.Response?.IsSuccessStatusCode != true)
        {
            if (response.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                throw new PolarisOperationalException(
                    "polaris_patron_not_found",
                    "Polaris did not find the patron.");
            }

            throw new PolarisOperationalException(
                "polaris_patron_transport_failed",
                "Polaris patron data was unavailable.");
        }

        if (!TryReadPapiErrorCode(rawContent, out var papiErrorCode) ||
            result is null || result.PAPIErrorCode != papiErrorCode)
        {
            throw new PolarisOperationalException(
                "polaris_patron_protocol_failed",
                "Polaris returned an invalid patron response.");
        }

        if (papiErrorCode != 0)
        {
            throw string.IsNullOrEmpty(pin)
                ? new PolarisOperationalException(
                    papiErrorCode == -3000
                        ? "polaris_patron_not_found"
                        : "polaris_patron_refresh_failed",
                    "Polaris did not return the patron.")
                : new PatronAuthenticationException("Incorrect Login - Please try again");
        }

        if (patron is null || !TryReadPatronIdentity(rawContent, patron, out var patronId,
                out var patronOrganizationId, out var patronCodeId, out var currentBarcode, out var formerBarcode) ||
            expectedPatronId.HasValue && patronId != expectedPatronId.Value ||
            requireBarcodeAlias && !string.Equals(barcode.Trim(), currentBarcode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(barcode.Trim(), formerBarcode, StringComparison.OrdinalIgnoreCase))
        {
            throw new PolarisOperationalException(
                "polaris_patron_protocol_failed",
                "Polaris returned an incomplete or mismatched patron identity.");
        }

        var organizations = await LoadOrganizationsAsync(client, cancellationToken);
        var home = ResolveHomeLibrary(organizations, patronOrganizationId)
            ?? throw new PolarisOperationalException(
                "polaris_home_library_missing",
                "The patron home library could not be resolved from Polaris.");
        var preferredPickupBranchId = ResolvePreferredPickupId(
            patron.RequestPickupBranchID,
            patronOrganizationId,
            rawContent,
            out var pickupPreferenceState);
        return new PatronSnapshot(
            patronId,
            currentBarcode,
            Clean(patron.EmailAddress),
            Clean(patron.NameFirst),
            Clean(patron.NameLast),
            patronCodeId,
            null,
            patronOrganizationId,
            home.Id,
            home.DisplayName ?? home.Name ?? home.Abbreviation ?? home.Id.ToString(),
            preferredPickupBranchId,
            formerBarcode,
            requireBarcodeAlias ? Clean(barcode) : null,
            pickupPreferenceState);
    }

    private static async Task<IReadOnlyList<NativeOrganization>> LoadOrganizationsAsync(
        DispatchAwarePapiClient client,
        CancellationToken cancellationToken)
    {
        var response = await client.CallAsync(() => client.OrganizationsGetAsync(
            OrganizationType.All,
            cancellationToken), cancellationToken);
        var result = response.Data;
        if (response.Response?.IsSuccessStatusCode != true ||
            result is null || !TryReadOrganizationsResponse(response.Response.Content, result, out var organizations))
        {
            throw new PolarisOperationalException(
                "polaris_organizations_failed",
                "Polaris did not return its organization hierarchy.");
        }

        return organizations;
    }

    private Task<(DispatchAwarePapiClient Client, PolarisSettings Settings)> CreateMemberClientAsync(
        int organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (organizationId <= PolarisConfigurationValidation.SystemOrganizationId)
        {
            throw new PolarisOperationalException("polaris_operation_context_missing",
                "A member operation requires an explicit servicing library.");
        }
        return CreateClientAsync(organizationId, cancellationToken);
    }

    private async Task<(DispatchAwarePapiClient Client, PolarisSettings Settings)> CreateClientAsync(
        int organizationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await context.PolarisSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken)
            ?? throw new PolarisOperationalException(
                "polaris_configuration_missing",
                "Polaris configuration is missing.");

        if (!PolarisConfigurationValidation.TryReadCredentials(
                settings, credentialProtector, out var accessKey, out var password))
        {
            throw new PolarisOperationalException(
                "polaris_configuration_incomplete",
                "Polaris configuration is incomplete or its credentials are unavailable.");
        }

        var client = new DispatchAwarePapiClient(
            httpClientFactory.CreateClient("Polaris"),
            new PapiSettings
            {
                Hostname = settings.Host!,
                AccessId = settings.AccessId!,
                AccessKey = accessKey,
                OrganizationId = organizationId,
                UserId = settings.SystemPolarisUserId!.Value,
                WorkstationId = settings.WorkstationId!.Value,
                PolarisOverrideAccount = new PolarisUser(
                    settings.StaffDomain!,
                    settings.AdminUser!,
                    password)
            });
        return (client, settings);
    }

    private static NativeOrganization? ResolveHomeLibrary(
        IReadOnlyList<NativeOrganization> organizations,
        int patronOrganizationId)
    {
        var byId = organizations.ToDictionary(item => item.Id);
        if (!byId.TryGetValue(patronOrganizationId, out var current))
        {
            return null;
        }

        var visited = new HashSet<int>();
        while (visited.Add(current.Id))
        {
            if (current.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId)
            {
                return current;
            }

            if (!current.ParentOrganizationID.HasValue ||
                !byId.TryGetValue(current.ParentOrganizationID.Value, out current))
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryReadOrganizationsResponse(
        string? content,
        OrganizationsGetResult data,
        out IReadOnlyList<NativeOrganization> organizations)
    {
        organizations = [];
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var rawCode) ||
                rawCode.ValueKind != JsonValueKind.Number || !rawCode.TryGetInt32(out var papiCode) ||
                papiCode < 0 || data.PAPIErrorCode != papiCode ||
                !TryGetUniqueProperty(root, "OrganizationsGetRows", out var rawRows) ||
                rawRows.ValueKind != JsonValueKind.Array || data.OrganizationsGetRows is null ||
                data.OrganizationsGetRows.Count == 0 ||
                data.OrganizationsGetRows.Count != rawRows.GetArrayLength())
            {
                return false;
            }

            var parsed = new List<NativeOrganization>(data.OrganizationsGetRows.Count);
            var identities = new HashSet<int>();
            var index = 0;
            foreach (var rawRow in rawRows.EnumerateArray())
            {
                var model = data.OrganizationsGetRows[index++];
                if (rawRow.ValueKind != JsonValueKind.Object || model is null || HasDuplicateProperties(rawRow) ||
                    !TryGetUniqueProperty(rawRow, "OrganizationID", out var rawId) ||
                    rawId.ValueKind != JsonValueKind.Number || !rawId.TryGetInt32(out var id) || id <= 0 ||
                    id != model.OrganizationID || !identities.Add(id))
                {
                    return false;
                }

                int? codeId = null;
                if (TryGetUniqueProperty(rawRow, "OrganizationCodeID", out var rawOrganizationCode))
                {
                    if (rawOrganizationCode.ValueKind == JsonValueKind.Null)
                    {
                        if (model.OrganizationCodeID != 0)
                        {
                            return false;
                        }
                    }
                    else if (rawOrganizationCode.ValueKind != JsonValueKind.Number ||
                             !rawOrganizationCode.TryGetInt32(out var parsedCodeId) ||
                             model.OrganizationCodeID != parsedCodeId)
                    {
                        return false;
                    }
                    else
                    {
                        codeId = parsedCodeId;
                    }
                }
                else if (model.OrganizationCodeID != 0)
                {
                    return false;
                }

                int? parentId = null;
                if (TryGetUniqueProperty(rawRow, "ParentOrganizationID", out var rawParentId))
                {
                    if (rawParentId.ValueKind == JsonValueKind.Null)
                    {
                        if (model.ParentOrganizationID is not null)
                        {
                            return false;
                        }
                    }
                    else if (rawParentId.ValueKind != JsonValueKind.Number ||
                             !rawParentId.TryGetInt32(out var parsedParentId) ||
                             model.ParentOrganizationID != parsedParentId)
                    {
                        return false;
                    }
                    else
                    {
                        parentId = parsedParentId;
                    }
                }
                else if (model.ParentOrganizationID is not null)
                {
                    return false;
                }

                parsed.Add(new NativeOrganization(
                    id,
                    codeId,
                    parentId,
                    model.DisplayName,
                    model.Name,
                    model.Abbreviation));
            }

            organizations = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryValidatePatronCodesResponse(string? content, PatronCodesGetResult data)
    {
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var rawCode) ||
                rawCode.ValueKind != JsonValueKind.Number || !rawCode.TryGetInt32(out var papiCode) ||
                papiCode < 0 || data.PAPIErrorCode != papiCode ||
                !TryGetUniqueProperty(root, "PatronCodesRows", out var rawRows) ||
                rawRows.ValueKind != JsonValueKind.Array || data.PatronCodesRows is null ||
                data.PatronCodesRows.Count != rawRows.GetArrayLength())
            {
                return false;
            }

            var identities = new HashSet<int>();
            var index = 0;
            foreach (var rawRow in rawRows.EnumerateArray())
            {
                var model = data.PatronCodesRows[index++];
                if (rawRow.ValueKind != JsonValueKind.Object || model is null || HasDuplicateProperties(rawRow) ||
                    !TryGetUniqueProperty(rawRow, "PatronCodeID", out var rawId) ||
                    rawId.ValueKind != JsonValueKind.Number || !rawId.TryGetInt32(out var id) || id <= 0 ||
                    id != model.PatronCodeID || !identities.Add(id))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<int> ReadPickupBranchIds(IRestResponse<PickupBranchesGetResult> response)
    {
        var data = response.Data;
        using var document = JsonDocument.Parse(response.Response?.Content ?? string.Empty);
        var root = document.RootElement;
        // Validate presence, uniqueness and range before trusting a model whose Int32
        // fields default to zero when omitted. Mapping uses the package's typed IDs.
        if (data is null || !TryReadPapiErrorCode(response.Response?.Content, out var code) ||
            code < 0 || data.PAPIErrorCode != code ||
            !TryGetUniqueProperty(root, "PickupBranchesRows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || data.PickupBranchesRows is null ||
            data.PickupBranchesRows.Count != rows.GetArrayLength())
        {
            throw new PolarisOperationalException("polaris_pickup_protocol_failed",
                "Polaris returned an invalid pickup branch response.");
        }
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var model = data.PickupBranchesRows[index++];
            if (row.ValueKind != JsonValueKind.Object || model is null || HasDuplicateProperties(row) ||
                !TryGetUniqueProperty(row, "ID", out var rawId) ||
                rawId.ValueKind != JsonValueKind.Number || !rawId.TryGetInt32(out var id) ||
                id <= PolarisConfigurationValidation.SystemOrganizationId || id != model.ID)
            {
                throw new PolarisOperationalException("polaris_pickup_protocol_failed",
                    "Polaris returned an invalid pickup branch identity.");
            }
        }
        return data.PickupBranchesRows.Select(row => row.ID).Distinct().ToArray();
    }

    private static BibSearchOptions SearchOptions(
        string identifier,
        int branch,
        SearchQualifiers qualifier) =>
        new()
        {
            Term = identifier.Trim(),
            Branch = branch,
            Qualifier = qualifier,
            SortOption = SearchSortOptions.PDTI,
            Page = 1,
            PageSize = 10
        };

    private static SearchAttempt InspectSearchResponse(
        IRestResponse<BibSearchResult> response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Response?.IsSuccessStatusCode != true)
        {
            return new SearchAttempt(
                IsTransient(response.Response?.StatusCode)
                    ? IdentifierLookupOutcome.TransientFailure
                    : IdentifierLookupOutcome.OperationalFailure,
                null);
        }

        var content = response.Response?.Content ?? string.Empty;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var data = response.Data;
            if (root.ValueKind != JsonValueKind.Object || data is null ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.Number ||
                !codeElement.TryGetInt32(out var code) ||
                code < -1 || data.PAPIErrorCode != code ||
                !TryGetUniqueProperty(root, "TotalRecordsFound", out var totalElement) ||
                totalElement.ValueKind != JsonValueKind.Number ||
                !totalElement.TryGetInt32(out var totalRecordsFound) ||
                totalRecordsFound < 0 || data.TotalRecordsFound != totalRecordsFound ||
                !TryGetUniqueProperty(root, "BibSearchRows", out var rowsElement) ||
                rowsElement.ValueKind != JsonValueKind.Array ||
                data.BibSearchRows is null || data.BibSearchRows.Count != rowsElement.GetArrayLength())
            {
                return SearchAttempt.Operational;
            }

            if (rowsElement.GetArrayLength() == 0)
            {
                if (totalRecordsFound != 0 ||
                    code != 0 && (code != -1 || !HasNoSearchErrorMessage(root)))
                {
                    return SearchAttempt.Operational;
                }

                return new SearchAttempt(null, data);
            }

            if (code < 0)
            {
                return SearchAttempt.Operational;
            }

            if (totalRecordsFound < rowsElement.GetArrayLength() ||
                code > 0 && code != rowsElement.GetArrayLength())
            {
                return SearchAttempt.Operational;
            }

            var rowIndex = 0;
            foreach (var row in rowsElement.EnumerateArray())
            {
                var model = data.BibSearchRows[rowIndex++];
                if (row.ValueKind != JsonValueKind.Object || model is null ||
                    !TryGetUniqueProperty(row, "ControlNumber", out var control) ||
                    !TryReadPositiveInt32(control, out var controlNumber) ||
                    controlNumber != model.ControlNumber ||
                    HasUndocumentedBibIdentityAlias(row) || HasDuplicateProperties(row))
                {
                    return SearchAttempt.Operational;
                }

            }
        }
        catch (JsonException)
        {
            return SearchAttempt.Operational;
        }

        return new SearchAttempt(null, response.Data);
    }

    private static bool HasUndocumentedBibIdentityAlias(JsonElement row)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, "BibID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Name, "BibliographicRecordID", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadPapiErrorCode(string? content, out int papiErrorCode)
    {
        papiErrorCode = default;
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   TryGetUniqueProperty(document.RootElement, "PAPIErrorCode", out var code) &&
                   code.ValueKind == JsonValueKind.Number &&
                   code.TryGetInt32(out papiErrorCode);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadPositiveTopLevelInt32(string? content, string name, out int value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   TryGetUniqueProperty(document.RootElement, name, out var element) &&
                   element.ValueKind == JsonValueKind.Number &&
                   element.TryGetInt32(out value) && value > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasTopLevelProperty(string? content, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.EnumerateObject().Any(property =>
                       string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadPatronIdentity(
        string content,
        PatronData patron,
        out int patronId,
        out int patronOrganizationId,
        out int? patronCodeId,
        out string barcode,
        out string? formerBarcode)
    {
        patronId = default;
        patronOrganizationId = default;
        patronCodeId = null;
        barcode = string.Empty;
        formerBarcode = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                !TryGetUniqueProperty(root, "PatronBasicData", out var rawPatron) ||
                rawPatron.ValueKind != JsonValueKind.Object || HasDuplicateProperties(rawPatron) ||
                !TryGetUniqueProperty(rawPatron, "PatronID", out var rawId) ||
                rawId.ValueKind != JsonValueKind.Number || !rawId.TryGetInt32(out patronId) ||
                patronId <= 0 || patron.PatronID != patronId ||
                !TryGetUniqueProperty(rawPatron, "PatronOrgID", out var rawOrganizationId) ||
                rawOrganizationId.ValueKind != JsonValueKind.Number ||
                !rawOrganizationId.TryGetInt32(out patronOrganizationId) ||
                patronOrganizationId <= 0 || patron.PatronOrgID != patronOrganizationId ||
                !TryGetUniqueProperty(rawPatron, "Barcode", out var rawBarcode) ||
                rawBarcode.ValueKind != JsonValueKind.String ||
                Clean(rawBarcode.GetString()) is not { } currentBarcode ||
                currentBarcode.Length > 50 ||
                !string.Equals(currentBarcode, Clean(patron.Barcode), StringComparison.Ordinal))
            {
                return false;
            }

            if (TryGetUniqueProperty(rawPatron, "PatronCodeID", out var rawPatronCodeId))
            {
                if (rawPatronCodeId.ValueKind != JsonValueKind.Number ||
                    !rawPatronCodeId.TryGetInt32(out var parsedPatronCodeId) ||
                    patron.PatronCodeID != parsedPatronCodeId)
                {
                    return false;
                }

                // Polaris responses historically omit unknown or unclassified patron
                // codes. Preserve that policy for every nonpositive native integer.
                patronCodeId = parsedPatronCodeId > 0 ? parsedPatronCodeId : null;
            }
            else if (rawPatron.EnumerateObject().Any(property =>
                         string.Equals(property.Name, "PatronCodeID", StringComparison.OrdinalIgnoreCase)) ||
                     patron.PatronCodeID != 0)
            {
                // A missing source value is valid only when the pinned DTO kept its
                // default value; a coerced or pre-populated model must not supply it.
                return false;
            }

            if (TryGetUniqueProperty(rawPatron, "FormerID", out var rawFormerId))
            {
                if (rawFormerId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return false;
                }
                formerBarcode = rawFormerId.ValueKind == JsonValueKind.String
                    ? Clean(rawFormerId.GetString())
                    : null;
            }
            else if (rawPatron.EnumerateObject().Any(property =>
                         string.Equals(property.Name, "FormerID", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!string.Equals(formerBarcode, Clean(patron.FormerID), StringComparison.Ordinal))
            {
                return false;
            }
            if (formerBarcode is { Length: > 50 })
            {
                return false;
            }

            barcode = currentBarcode;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryValidateCheckoutResponse(string? content, PatronItemsOutGetResult data)
    {
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var errorCode) ||
                errorCode.ValueKind != JsonValueKind.Number || !errorCode.TryGetInt32(out var code) ||
                code != 0 || data.PAPIErrorCode != code ||
                !TryGetUniqueProperty(root, "PatronItemsOutGetRows", out var rawRows) ||
                rawRows.ValueKind != JsonValueKind.Array || data.PatronItemsOutGetRows is null ||
                data.PatronItemsOutGetRows.Count != rawRows.GetArrayLength())
            {
                return false;
            }

            var index = 0;
            foreach (var rawRow in rawRows.EnumerateArray())
            {
                var model = data.PatronItemsOutGetRows[index++];
                if (rawRow.ValueKind != JsonValueKind.Object || HasDuplicateProperties(rawRow) || model is null ||
                    !TryGetUniqueProperty(rawRow, "BibID", out var bibIdElement) ||
                    bibIdElement.ValueKind != JsonValueKind.Number ||
                    !bibIdElement.TryGetInt32(out var bibId) || bibId <= 0 || model.BibID != bibId)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadHoldConversationIdentity(
        JsonElement root,
        HoldRequestCreateResult data,
        int statusType,
        int statusValue,
        bool isReply,
        HoldReplyCommand? replyContext,
        out Guid? requestGuid,
        out string? groupQualifier,
        out string? qualifier)
    {
        requestGuid = null;
        groupQualifier = null;
        qualifier = null;
        var hasRawGuid = TryGetUniqueProperty(root, "RequestGUID", out var rawGuid);
        var hasGuidProperty = root.EnumerateObject().Any(property =>
            string.Equals(property.Name, "RequestGUID", StringComparison.OrdinalIgnoreCase));
        if (hasGuidProperty)
        {
            if (!hasRawGuid || rawGuid.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(rawGuid.GetString(), out var parsedGuid) || parsedGuid == Guid.Empty ||
                data.RequestGuid != parsedGuid ||
                isReply && replyContext is not null && parsedGuid != replyContext.RequestGuid)
            {
                return false;
            }
            requestGuid = parsedGuid;
        }
        else if (data.RequestGuid is not null)
        {
            return false;
        }
        else if (isReply && replyContext is not null)
        {
            requestGuid = replyContext.RequestGuid;
        }

        var rawCanonicalGroup = TryGetUniqueProperty(root, "TxnGroupQualifier", out var canonicalGroupElement);
        var rawPackageGroup = TryGetUniqueProperty(root, "TxnGroupQualifer", out var packageGroupElement);
        var hasGroupProperty = root.EnumerateObject().Any(property =>
            string.Equals(property.Name, "TxnGroupQualifer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(property.Name, "TxnGroupQualifier", StringComparison.OrdinalIgnoreCase));
        if (hasGroupProperty)
        {
            if (root.EnumerateObject().Count(property =>
                    string.Equals(property.Name, "TxnGroupQualifier", StringComparison.OrdinalIgnoreCase)) > 1 ||
                root.EnumerateObject().Count(property =>
                    string.Equals(property.Name, "TxnGroupQualifer", StringComparison.OrdinalIgnoreCase)) > 1)
            {
                return false;
            }
            var canonicalGroup = rawCanonicalGroup && canonicalGroupElement.ValueKind == JsonValueKind.String
                ? Clean(canonicalGroupElement.GetString())
                : null;
            var packageGroup = rawPackageGroup && packageGroupElement.ValueKind == JsonValueKind.String
                ? Clean(packageGroupElement.GetString())
                : null;
            if (rawCanonicalGroup && canonicalGroup is null || rawPackageGroup && packageGroup is null)
            {
                return false;
            }
            if (rawCanonicalGroup && rawPackageGroup &&
                !string.Equals(canonicalGroup, packageGroup, StringComparison.Ordinal))
            {
                return false;
            }

            // Beta.5's typed model owns the misspelled property. When it is present,
            // the durable/reply value must come from that exact raw property. The
            // correctly-spelled alias is accepted only when it is the sole raw field,
            // because that alias is not represented by this pinned DTO.
            var selectedGroup = rawPackageGroup ? packageGroupElement : rawCanonicalGroup ? canonicalGroupElement : default;
            if (selectedGroup.ValueKind != JsonValueKind.String ||
                Clean(selectedGroup.GetString()) is not { } rawGroup)
            {
                return false;
            }
            var modelGroup = Clean(data.TxnGroupQualifer);
            if (rawPackageGroup && !string.Equals(modelGroup, rawGroup, StringComparison.Ordinal) ||
                !rawPackageGroup && modelGroup is not null)
            {
                return false;
            }
            groupQualifier = rawGroup;
        }
        else if (data.TxnGroupQualifer is null && isReply && replyContext is not null)
        {
            groupQualifier = replyContext.TxnGroupQualifier;
        }
        else if (data.TxnGroupQualifer is null)
        {
            groupQualifier = null;
        }
        else
        {
            return false;
        }

        if (TryGetUniqueProperty(root, "TxnQualifier", out var rawQualifier))
        {
            if (rawQualifier.ValueKind != JsonValueKind.String ||
                Clean(rawQualifier.GetString()) is not { } rawValue ||
                !string.Equals(rawValue, Clean(data.TxnQualifier), StringComparison.Ordinal))
            {
                return false;
            }
            qualifier = rawValue;
        }
        else if (!root.EnumerateObject().Any(property =>
                     string.Equals(property.Name, "TxnQualifier", StringComparison.OrdinalIgnoreCase)) &&
                 data.TxnQualifier is null && isReply && replyContext is not null)
        {
            qualifier = replyContext.TxnQualifier;
        }
        else if (!root.EnumerateObject().Any(property =>
                     string.Equals(property.Name, "TxnQualifier", StringComparison.OrdinalIgnoreCase)) &&
                 data.TxnQualifier is null)
        {
            qualifier = null;
        }
        else
        {
            return false;
        }

        if (isReply && replyContext is not null &&
            (requestGuid != replyContext.RequestGuid ||
             groupQualifier is not null && !string.Equals(groupQualifier, replyContext.TxnGroupQualifier, StringComparison.Ordinal) ||
             qualifier is not null && !string.Equals(qualifier, replyContext.TxnQualifier, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!isReply && statusType == 3 && statusValue == 5 &&
            (requestGuid is null || groupQualifier is null || qualifier is null))
        {
            return false;
        }

        return !requestGuid.HasValue || requestGuid.Value != Guid.Empty;
    }

    private static bool IsDefinitiveInvalidBibResponse(string? content)
    {
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperty(root, "ErrorMessage", out var errorMessage) ||
                errorMessage.ValueKind != JsonValueKind.String ||
                !string.Equals(errorMessage.GetString()?.Trim(), "Invalid BibID", StringComparison.OrdinalIgnoreCase) ||
                !TryGetUniqueProperty(root, "BibGetRows", out var rows))
            {
                return false;
            }

            return rows.ValueKind == JsonValueKind.Null ||
                   rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadUsableBibGetRows(string? content, out int rowCount)
    {
        rowCount = 0;
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperty(root, "BibGetRows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            {
                return false;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !TryGetUniqueProperty(row, "ElementID", out var elementId) ||
                    elementId.ValueKind != JsonValueKind.Number ||
                    !elementId.TryGetInt32(out var numericElementId) || numericElementId <= 0 ||
                    !TryGetUniqueProperty(row, "Value", out var value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                rowCount++;
            }

            return rowCount > 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetUniqueProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found)
            {
                value = default;
                return false;
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static int? ResolvePreferredPickupId(
        int requestPickupBranchId,
        int patronOrganizationId,
        string rawContent,
        out PatronPickupPreferenceState pickupPreferenceState)
    {
        pickupPreferenceState = PatronPickupPreferenceState.Absent;
        using var document = JsonDocument.Parse(rawContent);
        if (!TryGetUniqueProperty(document.RootElement, "PatronBasicData", out var patron) ||
            patron.ValueKind != JsonValueKind.Object || HasDuplicateProperties(patron))
        {
            throw new PolarisOperationalException("polaris_pickup_preference_invalid", "Polaris returned an invalid pickup preference.");
        }
        if (!TryGetUniqueProperty(patron, "RequestPickupBranchID", out var value))
        {
            if (requestPickupBranchId != 0)
            {
                throw new PolarisOperationalException(
                    "polaris_pickup_preference_invalid",
                    "Polaris returned an inconsistent pickup preference.");
            }

            // Keep the established basic-data projection: an omitted field uses
            // the registered branch. The explicit state lets hold placement
            // distinguish this fallback from a supplied preference.
            return patronOrganizationId > PolarisConfigurationValidation.SystemOrganizationId
                ? patronOrganizationId
                : null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) ||
            id < 0 || id != requestPickupBranchId)
        {
            // No other no-preference sentinel is documented. Reject malformed or
            // negative values instead of inventing provider semantics for them.
            throw new PolarisOperationalException("polaris_pickup_preference_invalid", "Polaris returned an invalid pickup preference.");
        }

        pickupPreferenceState = id > PolarisConfigurationValidation.SystemOrganizationId
            ? PatronPickupPreferenceState.Current
            : PatronPickupPreferenceState.ExplicitInvalid;
        // Explicit zero and system scope are not absence. Preserve them as an
        // unusable preference state so hold placement cannot silently reroute
        // to the registered branch. Other patron flows retain their existing
        // nullable preference projection.
        return id > PolarisConfigurationValidation.SystemOrganizationId ? id : null;
    }

    private static bool IsTransient(HttpStatusCode? statusCode) =>
        statusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static PolarisOperationalException Operational(string code, Exception exception) =>
        new(code, "The Polaris operation could not be completed.", exception);

    private static IdentifierLookupResult OperationalResult(string code) =>
        new(IdentifierLookupOutcome.OperationalFailure, ErrorCode: code);

    private static IdentifierLookupResult FailureResult(IdentifierLookupOutcome outcome) =>
        outcome == IdentifierLookupOutcome.TransientFailure
            ? new IdentifierLookupResult(
                IdentifierLookupOutcome.TransientFailure,
                ErrorCode: "polaris_transport_transient")
            : OperationalResult("polaris_search_operational_failure");

    private sealed record SearchAttempt(
        IdentifierLookupOutcome? Failure,
        BibSearchResult? Data)
    {
        public static SearchAttempt Operational { get; } = new(
            IdentifierLookupOutcome.OperationalFailure,
            null);
    }

    private sealed record NativeOrganization(
        int Id,
        int? OrganizationCodeId,
        int? ParentOrganizationID,
        string? DisplayName,
        string? Name,
        string? Abbreviation);
}
