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

public sealed class PolarisPatronProvider(
    IDbContextFactory<AsapDbContext> contextFactory,
    IntegrationCredentialProtector credentialProtector,
    IHttpClientFactory httpClientFactory) : IPatronProvider, IStaffPatronLookupProvider, IStaffCatalogSearchProvider, IStaffPolarisProvider, IPolarisReferenceProvider
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
            var (client, _) = await CreateClientAsync(cancellationToken);
            var rows = await LoadOrganizationsAsync(client, cancellationToken);
            return rows
                .Where(row => row.OrganizationID > 0)
                .Select(row => new PolarisOrganizationSnapshot(
                    row.OrganizationID,
                    Clean(row.DisplayName) ?? Clean(row.Name) ?? Clean(row.Abbreviation) ?? row.OrganizationID.ToString(),
                    Clean(row.Abbreviation),
                    row.OrganizationCodeID,
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
        catch (Exception exception)
        {
            throw Operational("polaris_organizations_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var response = await client.PatronCodesGetAsync(null, cancellationToken);
            var result = response.Data;
            if (response.Response?.IsSuccessStatusCode != true ||
                result is null || result.PAPIErrorCode < 0)
            {
                throw new PolarisOperationalException(
                    "polaris_patron_codes_failed",
                    "Polaris did not return its patron-code reference data.");
            }

            return result.PatronCodesRows
                .Where(row => row.PatronCodeID > 0)
                .GroupBy(row => row.PatronCodeID)
                .Select(group => group.First())
                .OrderBy(row => row.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.PatronCodeID)
                .Select(row => new PolarisPatronCodeSnapshot(
                    row.PatronCodeID.ToString(),
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
        catch (Exception exception)
        {
            throw Operational("polaris_patron_codes_failed", exception);
        }
    }

    public async Task<PatronSnapshot> AuthenticateAsync(
        string barcode,
        string pin,
        CancellationToken cancellationToken)
    {
        var (client, _) = await CreateClientAsync(cancellationToken);
        try
        {
            var response = await client.AuthenticatePatronAsync(barcode, pin, cancellationToken);
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

            if (papiErrorCode != 0)
            {
                throw new PatronAuthenticationException("Incorrect Login - Please try again");
            }

            if (authentication.PatronID <= 0 ||
                string.IsNullOrWhiteSpace(authentication.AccessToken) ||
                string.IsNullOrWhiteSpace(authentication.AccessSecret))
            {
                throw new PolarisOperationalException(
                    "polaris_authentication_protocol_failed",
                    "Polaris returned an incomplete authentication response.");
            }

            return await LoadPatronAsync(client, barcode, pin, cancellationToken);
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
        catch (Exception exception)
        {
            throw Operational("polaris_authentication_failed", exception);
        }
    }

    public async Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken)
    {
        var (client, _) = await CreateClientAsync(cancellationToken);
        try
        {
            return await LoadPatronAsync(client, barcode, string.Empty, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Operational("polaris_patron_refresh_failed", exception);
        }
    }

    public async Task<IReadOnlyList<StaffPatronSearchCandidate>> SearchAsync(
        string query,
        int? organizationId,
        CancellationToken cancellationToken)
    {
        var (client, _) = await CreateClientAsync(cancellationToken);
        try
        {
            var response = await client.PatronSearchAsync(
                query.Trim(),
                page: 1,
                pageSize: 10,
                sortBy: PatronSortKeys.PATN,
                orgId: organizationId,
                cancellationToken: cancellationToken);
            var result = response.Data;
            if (response.Response?.IsSuccessStatusCode != true ||
                result is null ||
                result.PAPIErrorCode < 0)
            {
                throw new PolarisOperationalException(
                    "polaris_patron_search_failed",
                    "Polaris did not return patron search results.");
            }

            return result.PatronSearchRows
                .Where(row => !string.IsNullOrWhiteSpace(row.Barcode) && row.PatronID > 0)
                .Take(10)
                .Select(row => new StaffPatronSearchCandidate(
                    row.Barcode!.Trim(),
                    Clean(row.PatronFirstLastName) ?? row.Barcode.Trim(),
                    row.OrganizationID))
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
        catch (Exception exception)
        {
            throw Operational("polaris_patron_search_failed", exception);
        }
    }

    public async Task<IReadOnlyList<StaffCatalogSearchCandidate>> SearchAsync(
        string query,
        string mode,
        CancellationToken cancellationToken)
    {
        var (client, settings) = await CreateClientAsync(cancellationToken);
        try
        {
            var qualifier = mode switch
            {
                "author" => SearchQualifiers.AU,
                "identifier" => SearchQualifiers.ISBN,
                _ => SearchQualifiers.TI
            };
            var branchId = settings.PickupOrganizationId is > 0
                ? settings.PickupOrganizationId.Value
                : settings.OrganizationIdForRequests is > 0
                    ? settings.OrganizationIdForRequests.Value
                    : 0;
            var response = await client.BibSearchAsync(
                new BibSearchOptions
                {
                    Term = query.Trim(),
                    SearchType = BibSearchTypes.keyword,
                    SortOption = SearchSortOptions.RELEVANCE,
                    Qualifier = qualifier,
                    Limit = "10",
                    Branch = branchId,
                    Page = 1,
                    PageSize = 10
                },
                cancellationToken);
            var result = response.Data;
            if (response.Response?.IsSuccessStatusCode != true ||
                result is null ||
                result.PAPIErrorCode < 0)
            {
                throw new PolarisOperationalException(
                    "polaris_catalog_search_failed",
                    "Polaris did not return catalog search results.");
            }

            return result.BibSearchRows
                .Where(row => row.ControlNumber > 0 && !string.IsNullOrWhiteSpace(row.Title))
                .Take(10)
                .Select(row => new StaffCatalogSearchCandidate(
                    row.ControlNumber,
                    Clean(row.Title) ?? row.ControlNumber.ToString(),
                    Clean(row.Author),
                    Clean(row.ISBN),
                    Clean(row.PublicationDate)))
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
        catch (Exception exception)
        {
            throw Operational("polaris_catalog_search_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        var (client, settings) = await CreateClientAsync(cancellationToken);
        try
        {
            var candidates = new[]
            {
                patron.PatronOrganizationId,
                settings.PickupOrganizationId is > 0 ? settings.PickupOrganizationId.Value : 0,
                settings.OrganizationIdForRequests is > 0 ? settings.OrganizationIdForRequests.Value : 0,
                patron.HomeLibraryOrganizationId
            }.Where(item => item > 0).Distinct();
            IReadOnlyList<PickupBranch> branches = [];
            foreach (var organizationId in candidates)
            {
                var response = await client.PickupBranchesGetAsync(organizationId, cancellationToken);
                if (response.Response?.IsSuccessStatusCode != true)
                {
                    continue;
                }

                branches = NormalizePickupBranches(response.Response.Content);
                if (branches.Count > 0)
                {
                    break;
                }
            }

            if (branches.Count == 0)
            {
                throw new InvalidOperationException("Polaris did not return pickup branches.");
            }

            return branches;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not PolarisOperationalException)
        {
            throw Operational("polaris_pickup_branches_failed", exception);
        }
    }

    public async Task UpdatePreferredPickupBranchAsync(
        string barcode,
        int pickupBranchId,
        CancellationToken cancellationToken)
    {
        var (client, settings) = await CreateClientAsync(cancellationToken);
        try
        {
            var organizationId = settings.OrganizationIdForRequests is > 0
                ? settings.OrganizationIdForRequests.Value
                : 1;
            var update = new PatronUpdateParams
            {
                LogonBranchId = organizationId,
                LogonUserId = settings.SystemPolarisUserId is > 0
                    ? settings.SystemPolarisUserId.Value
                    : 1,
                LogonWorkstationId = settings.WorkstationId is > 0
                    ? settings.WorkstationId.Value
                    : 1,
                RequestPickupBranchID = pickupBranchId
            };
            var request = PapiRestRequest.Put(
                $"/public/v1/1033/100/{organizationId}/patron/{WebUtility.UrlEncode(barcode)}",
                body: update);
            request.QueryParameters.Add("ignoresa", true);
            var response = await client.ExecutePapiAsync<PatronUpdateResult>(
                request,
                cancellationToken);
            var result = response.Data;
            if (response.Response?.IsSuccessStatusCode != true ||
                !TryReadPapiErrorCode(response.Response.Content, out var papiErrorCode) ||
                result is null || result.PAPIErrorCode != papiErrorCode || papiErrorCode != 0)
            {
                throw new InvalidOperationException("Polaris rejected the pickup preference update.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Operational("polaris_pickup_update_failed", exception);
        }
    }

    public async Task<IdentifierLookupResult> LookupIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, settings) = await CreateClientAsync(cancellationToken);
            var branch = settings.PickupOrganizationId is > 0
                ? settings.PickupOrganizationId.Value
                : settings.OrganizationIdForRequests is > 0
                    ? settings.OrganizationIdForRequests.Value
                    : 1;
            var attempts = new List<SearchAttempt>();

            var isbn = InspectSearchResponse(
                await client.BibSearchAsync(
                    SearchOptions(identifier, branch, SearchQualifiers.ISBN),
                    cancellationToken));
            if (isbn.Failure.HasValue)
            {
                return FailureResult(isbn.Failure.Value);
            }
            attempts.Add(isbn);

            var upcRequest = PapiRestRequest.Get(
                $"/public/v1/1033/100/{branch}/search/bibs/keyword/UPC");
            upcRequest.BlockStaffOverride = true;
            upcRequest.QueryParameters.Add("q", identifier.Trim());
            upcRequest.QueryParameters.Add("sort", SearchSortOptions.PDTI);
            upcRequest.QueryParameters.Add("page", 1);
            upcRequest.QueryParameters.Add("bibsperpage", 10);
            upcRequest.QueryParameters.Add("limit", string.Empty);
            var upc = InspectSearchResponse(
                await client.ExecutePapiAsync<BibSearchResult>(
                upcRequest,
                cancellationToken));
            if (upc.Failure.HasValue)
            {
                return FailureResult(upc.Failure.Value);
            }
            attempts.Add(upc);

            var lccn = InspectSearchResponse(
                await client.BibSearchAsync(
                    SearchOptions(identifier, branch, SearchQualifiers.LCCN),
                    cancellationToken));
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
                    var tom = attempts.SelectMany(item => item.PrimaryTomByControlNumber)
                        .Where(item => item.Key == row.ControlNumber)
                        .Select(item => item.Value)
                        .FirstOrDefault() ?? row.TypeOfMaterial;
                    var excluded = tom is "36" or "41";
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
                var detailResponse = await client.BibGetAsync(
                    selected.ControlNumber,
                    branch,
                    cancellationToken);
                if (detailResponse.Response?.IsSuccessStatusCode == true &&
                    detailResponse.Data is { PAPIErrorCode: >= 0 } detail)
                {
                    catalogTitle = Clean(detail.Title) ?? catalogTitle;
                    catalogAuthor = Clean(detail.Author.FirstOrDefault()) ?? catalogAuthor;
                }
            }
            catch (Exception)
            {
                // Search success remains authoritative when optional detail reconciliation fails.
            }

            return new IdentifierLookupResult(
                IdentifierLookupOutcome.Found,
                selected.ControlNumber.ToString(),
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
        catch (Exception)
        {
            return OperationalResult("polaris_search_operational_failure");
        }
    }

    public async Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken)
    {
        if (bibId <= 0)
        {
            return new BibValidationResult(false);
        }
        try
        {
            var (client, settings) = await CreateClientAsync(cancellationToken);
            var branchId = settings.PickupOrganizationId is > 0
                ? settings.PickupOrganizationId
                : settings.OrganizationIdForRequests;
            var response = await client.BibGetAsync(bibId, branchId, cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null || data.PAPIErrorCode != 0 ||
                data.BibGetRows.Count == 0)
            {
                return new BibValidationResult(false);
            }
            return new BibValidationResult(true, Clean(data.Title), Clean(data.Author.FirstOrDefault()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not PolarisOperationalException)
        {
            throw Operational("polaris_bib_validation_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var response = await client.PatronHoldRequestsGetAsync(
                barcode,
                PatronHoldStatus.all,
                password: string.Empty,
                cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null || data.PAPIErrorCode != 0)
            {
                throw new PolarisOperationalException("polaris_hold_read_failed", "Polaris hold data was unavailable.");
            }
            if (data.PatronHoldRequestsGetRows.Any(item => item.HoldRequestID <= 0 || item.BibID <= 0))
            {
                throw new PolarisOperationalException(
                    "polaris_hold_read_failed",
                    "Polaris returned an incomplete hold row.");
            }

            return data.PatronHoldRequestsGetRows
                .Select(item => new PolarisHoldSnapshot(
                    item.HoldRequestID,
                    item.BibID,
                    item.StatusID,
                    Clean(item.StatusDescription),
                    item.PickupBranchID,
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
        catch (Exception exception)
        {
            throw Operational("polaris_hold_read_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var response = await client.PatronItemsOutGetAsync(
                barcode,
                PatronItemsOutGetStatus.All,
                password: string.Empty,
                cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null || data.PAPIErrorCode != 0)
            {
                throw new PolarisOperationalException("polaris_checkout_read_failed", "Polaris checkout data was unavailable.");
            }
            if (data.PatronItemsOutGetRows.Any(item => item.BibID <= 0))
            {
                throw new PolarisOperationalException(
                    "polaris_checkout_read_failed",
                    "Polaris returned an incomplete checkout row.");
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
        catch (Exception exception)
        {
            throw Operational("polaris_checkout_read_failed", exception);
        }
    }

    public async Task<HoldProviderResult> CreateHoldAsync(
        HoldCreateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var response = await client.HoldRequestCreateAsync(new HoldRequestCreateParams
            {
                PatronID = command.PatronId,
                BibID = command.BibId,
                PickupOrgID = command.PickupBranchId,
                RequestingOrgID = command.RequestingOrganizationId,
                WorkstationID = command.WorkstationId,
                UserID = command.PolarisUserId
            }, cancellationToken);
            return NormalizeHoldResponse(
                response.Response?.IsSuccessStatusCode == true,
                response.Response?.Content,
                response.Data?.StatusType,
                response.Data?.StatusValue,
                response.Data?.RequestGuid,
                response.Data?.TxnGroupQualifier,
                response.Data?.TxnQualifier,
                isReply: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HoldProviderResult(
                HoldProviderOutcome.Ambiguous,
                null,
                null,
                null,
                null,
                null,
                null,
                "create_transport_ambiguous",
                exception is OperationCanceledException ? "provider_timeout" : "provider_transport_error");
        }
    }

    public async Task<HoldProviderResult> ReplyToHoldAsync(
        HoldReplyCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var createContext = new HoldRequestCreateResult
            {
                RequestGuid = command.RequestGuid,
                TxnGroupQualifier = command.TxnGroupQualifier,
                TxnQualifier = command.TxnQualifier
            };
            var response = await client.HoldRequestReplyAsync(
                createContext,
                command.RequestingOrganizationId,
                HoldRequestReplyAnswer.Yes,
                HoldRequestReplyState.AcceptEvenWithExistingHolds,
                cancellationToken);
            return NormalizeHoldResponse(
                response.Response?.IsSuccessStatusCode == true,
                response.Response?.Content,
                null,
                null,
                command.RequestGuid,
                command.TxnGroupQualifier,
                command.TxnQualifier,
                isReply: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HoldProviderResult(
                HoldProviderOutcome.Ambiguous,
                command.RequestGuid.ToString(),
                null,
                command.TxnGroupQualifier,
                command.TxnQualifier,
                null,
                null,
                "reply_transport_ambiguous",
                exception is OperationCanceledException ? "provider_timeout" : "provider_transport_error");
        }
    }

    internal static HoldProviderResult NormalizeHoldResponse(
        bool httpSucceeded,
        string? content,
        int? typedStatusType,
        int? typedStatusValue,
        Guid? typedRequestGuid,
        string? typedTxnGroupQualifier,
        string? typedTxnQualifier,
        bool isReply)
    {
        if (!httpSucceeded || string.IsNullOrWhiteSpace(content))
        {
            return Ambiguous(isReply, "provider_http_error");
        }
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!TryGetInt32(root, ["PAPIErrorCode"], out var papiErrorCode))
            {
                return Ambiguous(isReply, "provider_protocol_error");
            }
            var statusType = ReadInt(root, "StatusType") ?? typedStatusType;
            var statusValue = ReadInt(root, "StatusValue") ?? typedStatusValue;
            var requestGuid = ReadString(root, "RequestGUID", "RequestGuid") ?? typedRequestGuid?.ToString();
            var holdId = ReadString(root, "HoldRequestID", "HoldRequestId", "SysHoldRequestID");
            var group = ReadString(root, "TxnGroupQualifier", "TxnGroupQualifer") ?? typedTxnGroupQualifier;
            var qualifier = ReadString(root, "TxnQualifier") ?? typedTxnQualifier;
            var common = new
            {
                requestGuid,
                holdId,
                group,
                qualifier,
                statusType,
                statusValue
            };
            if (papiErrorCode != 0)
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.Ambiguous,
                    common.requestGuid,
                    common.holdId,
                    common.group,
                    common.qualifier,
                    common.statusType,
                    common.statusValue,
                    isReply ? "reply_papi_error" : "create_papi_error",
                    "provider_papi_error");
            }
            if (!isReply && statusType == 3 && statusValue == 5 &&
                Guid.TryParse(requestGuid, out _) && !string.IsNullOrWhiteSpace(group) && !string.IsNullOrWhiteSpace(qualifier))
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.ReplyRequired,
                    requestGuid,
                    holdId,
                    group,
                    qualifier,
                    statusType,
                    statusValue,
                    "create_status_5_reply_required");
            }
            if (statusType == 2 && statusValue.GetValueOrDefault() == 0)
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.FinalSuccess,
                    requestGuid,
                    holdId,
                    group,
                    qualifier,
                    statusType,
                    statusValue,
                    isReply ? "documented_reply_success" : "documented_create_success");
            }
            if (!isReply && statusType == 1 && statusValue.HasValue &&
                DocumentedCreateNoEffectStatuses.Contains(statusValue.Value))
            {
                return new HoldProviderResult(
                    HoldProviderOutcome.DefinitiveNoEffect,
                    requestGuid,
                    holdId,
                    group,
                    qualifier,
                    statusType,
                    statusValue,
                    "documented_create_rejection",
                    $"provider_status_{statusValue.Value}");
            }
            return new HoldProviderResult(
                HoldProviderOutcome.Ambiguous,
                requestGuid,
                holdId,
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

    private static int? ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        }
        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return Clean(value.GetString());
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private async Task<PatronSnapshot> LoadPatronAsync(
        PapiClient client,
        string barcode,
        string pin,
        CancellationToken cancellationToken)
    {
        var response = await client.PatronBasicDataGetAsync(
            barcode,
            pin,
            cancellationToken: cancellationToken);
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
                    response.Response?.StatusCode == HttpStatusCode.NotFound ||
                    LooksLikePatronNotFound(rawContent)
                        ? "polaris_patron_not_found"
                        : "polaris_patron_refresh_failed",
                    "Polaris did not return the patron.")
                : new PatronAuthenticationException("Incorrect Login - Please try again");
        }

        if (patron is null || patron.PatronID <= 0)
        {
            throw new PolarisOperationalException(
                "polaris_patron_protocol_failed",
                "Polaris returned an incomplete patron response.");
        }

        var organizations = await LoadOrganizationsAsync(client, cancellationToken);
        var home = ResolveHomeLibrary(organizations, patron.PatronOrgID)
            ?? throw new PolarisOperationalException(
                "polaris_home_library_missing",
                "The patron home library could not be resolved from Polaris.");
        return new PatronSnapshot(
            patron.PatronID,
            (patron.Barcode ?? barcode).Trim(),
            Clean(patron.EmailAddress),
            Clean(patron.NameFirst),
            Clean(patron.NameLast),
            patron.PatronCodeID <= 0 ? null : patron.PatronCodeID.ToString(),
            null,
            patron.PatronOrgID,
            home.OrganizationID,
            home.DisplayName ?? home.Name ?? home.Abbreviation ?? home.OrganizationID.ToString(),
            ResolvePreferredPickupId(patron.RequestPickupBranchID, patron.PatronOrgID, rawContent));
    }

    private static bool LooksLikePatronNotFound(string content) =>
        content.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        content.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
        content.Contains("no patron", StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<OrganizationsGetRow>> LoadOrganizationsAsync(
        PapiClient client,
        CancellationToken cancellationToken)
    {
        var response = await client.OrganizationsGetAsync(
            OrganizationType.All,
            cancellationToken);
        var result = response.Data;
        if (response.Response?.IsSuccessStatusCode != true ||
            result is null || result.PAPIErrorCode < 0 || result.OrganizationsGetRows.Count == 0)
        {
            throw new PolarisOperationalException(
                "polaris_organizations_failed",
                "Polaris did not return its organization hierarchy.");
        }

        return result.OrganizationsGetRows;
    }

    private async Task<(PapiClient Client, PolarisSettings Settings)> CreateClientAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await context.PolarisSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken)
            ?? throw new PolarisOperationalException(
                "polaris_configuration_missing",
                "Polaris configuration is missing.");

        if (string.IsNullOrWhiteSpace(settings.Host) ||
            string.IsNullOrWhiteSpace(settings.AccessId) ||
            string.IsNullOrWhiteSpace(settings.ProtectedApiKey) ||
            string.IsNullOrWhiteSpace(settings.StaffDomain) ||
            string.IsNullOrWhiteSpace(settings.AdminUser) ||
            string.IsNullOrWhiteSpace(settings.ProtectedAdminPassword))
        {
            throw new PolarisOperationalException(
                "polaris_configuration_incomplete",
                "Polaris configuration is incomplete.");
        }

        string accessKey;
        string password;
        try
        {
            accessKey = credentialProtector.Unprotect(settings.ProtectedApiKey);
            password = credentialProtector.Unprotect(settings.ProtectedAdminPassword);
        }
        catch (Exception exception)
        {
            throw Operational("polaris_credentials_unavailable", exception);
        }

        var client = new PapiClient(
            httpClientFactory.CreateClient("Polaris"),
            new PapiSettings
            {
                Hostname = settings.Host,
                AccessId = settings.AccessId,
                AccessKey = accessKey,
                OrganizationId = settings.OrganizationIdForRequests ?? 1,
                UserId = settings.SystemPolarisUserId ?? 1,
                WorkstationId = settings.WorkstationId ?? 1,
                PolarisOverrideAccount = new PolarisUser(
                    settings.StaffDomain,
                    settings.AdminUser,
                    password)
            });
        return (client, settings);
    }

    private static OrganizationsGetRow? ResolveHomeLibrary(
        IReadOnlyList<OrganizationsGetRow> organizations,
        int patronOrganizationId)
    {
        var byId = organizations.ToDictionary(item => item.OrganizationID);
        if (!byId.TryGetValue(patronOrganizationId, out var current))
        {
            return null;
        }

        var visited = new HashSet<int>();
        while (visited.Add(current.OrganizationID))
        {
            if (current.OrganizationCodeID == 2)
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

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<PickupBranch> NormalizePickupBranches(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                TryGetInt32(root, ["PAPIErrorCode"], out var papiErrorCode) &&
                papiErrorCode < 0)
            {
                return [];
            }

            var rows = FindPickupBranchRows(root);
            var byId = new Dictionary<int, PickupBranch>();
            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !TryGetInt32(
                        row,
                        ["OrganizationID", "OrganizationId", "OrgID", "OrgId", "PickupBranchID",
                         "PickupBranchId", "BranchID", "BranchId", "ID", "Id"],
                        out var id) ||
                    id <= 0 ||
                    byId.ContainsKey(id))
                {
                    continue;
                }

                var label = TryGetString(
                    row,
                    ["DisplayName", "OrganizationName", "Name", "BranchName", "Description", "Label"]);
                byId.Add(id, new PickupBranch(id, label ?? $"Branch {id}"));
            }

            return byId.Values
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<JsonElement> FindPickupBranchRows(JsonElement root)
    {
        var container = root;
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, ["PickupBranchesRows", "PickupBranchRows", "Branches"], out var found))
        {
            container = found;
        }

        if (container.ValueKind == JsonValueKind.Object &&
            TryGetProperty(container, ["PickupBranchRow"], out found))
        {
            container = found;
        }

        return container.ValueKind switch
        {
            JsonValueKind.Array => container.EnumerateArray().ToArray(),
            JsonValueKind.Object => [container],
            _ => []
        };
    }

    private static bool TryGetProperty(
        JsonElement element,
        IReadOnlyList<string> names,
        out JsonElement value)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetInt32(
        JsonElement element,
        IReadOnlyList<string> names,
        out int value)
    {
        if (TryGetProperty(element, names, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static string? TryGetString(JsonElement element, IReadOnlyList<string> names)
    {
        if (!TryGetProperty(element, names, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Clean(property.GetString());
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
        IRestResponse<BibSearchResult> response)
    {
        if (response.Response?.IsSuccessStatusCode != true)
        {
            return new SearchAttempt(
                IsTransient(response.Response?.StatusCode)
                    ? IdentifierLookupOutcome.TransientFailure
                    : IdentifierLookupOutcome.OperationalFailure,
                null,
                new Dictionary<int, string>());
        }

        var content = response.Response?.Content ?? string.Empty;
        Dictionary<int, string> primaryTomByControlNumber;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!TryGetProperty(root, "PAPIErrorCode", out var codeElement) ||
                !codeElement.TryGetInt32(out var code) ||
                code < -1)
            {
                return SearchAttempt.Operational;
            }

            if (code == -1)
            {
                return new SearchAttempt(
                    null,
                    response.Data ?? new BibSearchResult { PAPIErrorCode = -1 },
                    new Dictionary<int, string>());
            }

            if (!TryGetProperty(root, "BibSearchRows", out var rowsElement) ||
                rowsElement.ValueKind != JsonValueKind.Array ||
                response.Data is null)
            {
                return SearchAttempt.Operational;
            }

            primaryTomByControlNumber = new Dictionary<int, string>();
            foreach (var row in rowsElement.EnumerateArray())
            {
                if (TryGetProperty(row, "ControlNumber", out var control) &&
                    control.TryGetInt32(out var controlNumber) &&
                    TryGetProperty(row, "PrimaryTypeOfMaterial", out var tom))
                {
                    primaryTomByControlNumber[controlNumber] = tom.ToString().Trim();
                }
            }
        }
        catch (JsonException)
        {
            return SearchAttempt.Operational;
        }

        return new SearchAttempt(null, response.Data, primaryTomByControlNumber);
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
            return TryGetProperty(document.RootElement, "PAPIErrorCode", out var code) &&
                   code.TryGetInt32(out papiErrorCode);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ResolvePreferredPickupId(
        int requestPickupBranchId,
        int patronOrganizationId,
        string rawContent)
    {
        if (requestPickupBranchId > 0)
        {
            return requestPickupBranchId;
        }

        try
        {
            using var document = JsonDocument.Parse(rawContent);
            if (!TryGetProperty(document.RootElement, "PatronBasicData", out var patron))
            {
                return patronOrganizationId;
            }

            if (!TryGetProperty(patron, "RequestPickupBranchID", out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return patronOrganizationId;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericId))
            {
                return numericId;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return patronOrganizationId;
                }

                return int.TryParse(text, out var textId) ? textId : null;
            }
        }
        catch (JsonException)
        {
        }

        return patronOrganizationId;
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
        BibSearchResult? Data,
        IReadOnlyDictionary<int, string> PrimaryTomByControlNumber)
    {
        public static SearchAttempt Operational { get; } = new(
            IdentifierLookupOutcome.OperationalFailure,
            null,
            new Dictionary<int, string>());
    }
}
