using System.Data;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Analytics;

public sealed record AnalyticsLibrary(string OrgId, string Name);

public sealed record AnalyticsScope(
    string Mode,
    string LibraryOrgId,
    string Label,
    bool SuperAdmin);

public sealed record AnalyticsDateRange(
    string Key,
    DateTimeOffset Start,
    DateTimeOffset End);

public sealed record AnalyticsSummary(
    long NewSuggestions,
    long OpenRequests,
    long ClosedRequests,
    long HeldRequests,
    double AverageDaysToHold);

public sealed record AnalyticsClosedReason(string Reason, long Count);

public sealed record AnalyticsAgingStage(string Status, long Count, double AverageAgeDays);

public sealed record AnalyticsAging(
    int ThresholdDays,
    long OpenOlderThanThreshold,
    IReadOnlyList<AnalyticsAgingStage> AverageAgeByStage);

public sealed record AnalyticsExceptions(long HoldFailures, long IdentifierFailures);

public sealed record AnalyticsResponse(
    AnalyticsScope Scope,
    AnalyticsDateRange DateRange,
    IReadOnlyList<AnalyticsLibrary> AvailableLibraries,
    AnalyticsSummary Summary,
    IReadOnlyDictionary<string, long> StageCounts,
    IReadOnlyList<AnalyticsClosedReason> ClosedReasons,
    AnalyticsAging Aging,
    AnalyticsExceptions Exceptions);

public sealed record AnalyticsResult(string Code, AnalyticsResponse? Data = null)
{
    public static AnalyticsResult InvalidScope() => new("invalid_scope");
}

public sealed record AnalyticsResolvedDateRange(
    string Key,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTime StartUtc,
    DateTime EndExclusiveUtc);

public sealed record AnalyticsResolvedScope(
    bool IsValid,
    int? OrganizationId,
    string Mode,
    string LibraryOrgId,
    string Label);

public sealed class AnalyticsService(
    IDbContextFactory<AsapDbContext> contextFactory,
    ExternalConfiguration configuration,
    TimeProvider timeProvider)
{
    private static readonly string[] StageOrder =
    [
        "suggestion",
        "outstanding_purchase",
        "pending_hold",
        "hold_placed",
        "closed",
        "additional_copies"
    ];

    private static readonly string[] OpenStageOrder =
    [
        "suggestion",
        "outstanding_purchase",
        "pending_hold",
        "hold_placed",
        "additional_copies"
    ];

    private const string AnalyticsSql = """
        ;WITH ScopedTitleRequests AS
        (
            SELECT
                r.[Id],
                r.[LibraryOrganizationId],
                r.[Status],
                r.[CloseReason],
                r.[IsbnCheckStatus],
                r.[CreatedUtc],
                r.[UpdatedUtc]
            FROM [asap].[TitleRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1
                AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL
               OR r.[LibraryOrganizationId] = @scopeOrganizationId
        ),
        ScopedRequests AS
        (
            SELECT
                N'title' AS [RequestType],
                r.[Id],
                r.[LibraryOrganizationId],
                r.[Status],
                r.[CloseReason],
                r.[CreatedUtc],
                r.[UpdatedUtc]
            FROM ScopedTitleRequests AS r
            UNION ALL
            SELECT
                N'additional_copy' AS [RequestType],
                r.[Id],
                r.[LibraryOrganizationId],
                CASE WHEN r.[Status] = N'closed' THEN N'closed' ELSE N'additional_copies' END,
                CAST(NULL AS nvarchar(64)),
                r.[CreatedUtc],
                r.[UpdatedUtc]
            FROM [asap].[AdditionalCopyRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1
                AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL
               OR r.[LibraryOrganizationId] = @scopeOrganizationId
        ),
        FirstHolds AS
        (
            SELECT
                r.[Id],
                MIN(e.[CreatedUtc]) AS [FirstHoldUtc]
            FROM ScopedTitleRequests AS r
            INNER JOIN [asap].[TitleRequestEvent] AS e ON e.[TitleRequestId] = r.[Id]
                AND e.[EventType] = N'hold_placed'
            GROUP BY r.[Id]
        )
        SELECT
            COALESCE(SUM(CASE WHEN r.[CreatedUtc] >= @rangeStartUtc AND r.[CreatedUtc] < @rangeEndExclusiveUtc THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), CONVERT(bigint, 0)) AS [NewSuggestions],
            COALESCE(SUM(CASE WHEN r.[Status] <> N'closed' THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), CONVERT(bigint, 0)) AS [OpenRequests],
            COALESCE(SUM(CASE WHEN r.[Status] = N'closed' AND r.[UpdatedUtc] >= @rangeStartUtc AND r.[UpdatedUtc] < @rangeEndExclusiveUtc THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), CONVERT(bigint, 0)) AS [ClosedRequests],
            COALESCE(SUM(CASE WHEN h.[FirstHoldUtc] >= @rangeStartUtc AND h.[FirstHoldUtc] < @rangeEndExclusiveUtc THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), CONVERT(bigint, 0)) AS [HeldRequests],
            COALESCE(AVG(CASE
                WHEN h.[FirstHoldUtc] >= @rangeStartUtc
                 AND h.[FirstHoldUtc] < @rangeEndExclusiveUtc
                THEN CASE
                    WHEN h.[FirstHoldUtc] >= r.[CreatedUtc]
                    THEN CONVERT(float, DATEDIFF_BIG(NANOSECOND, r.[CreatedUtc], h.[FirstHoldUtc])) / 86400000000000.0
                    ELSE CONVERT(float, 0.0)
                END
                ELSE NULL
            END), 0.0) AS [AverageDaysToHold]
        FROM ScopedRequests AS r
        LEFT JOIN FirstHolds AS h ON h.[Id] = r.[Id] AND r.[RequestType] = N'title';

        ;WITH ScopedTitleRequests AS
        (
            SELECT r.[Id], r.[LibraryOrganizationId], r.[Status], r.[CreatedUtc], r.[UpdatedUtc]
            FROM [asap].[TitleRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
        ),
        ScopedRequests AS
        (
            SELECT r.[Status]
            FROM ScopedTitleRequests AS r
            UNION ALL
            SELECT CASE WHEN r.[Status] = N'closed' THEN N'closed' ELSE N'additional_copies' END
            FROM [asap].[AdditionalCopyRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
        )
        SELECT stages.[Stage], COALESCE(COUNT_BIG(r.[Status]), CONVERT(bigint, 0)) AS [Count]
        FROM (VALUES
            (N'suggestion'),
            (N'outstanding_purchase'),
            (N'pending_hold'),
            (N'hold_placed'),
            (N'closed'),
            (N'additional_copies')) AS stages([Stage])
        LEFT JOIN ScopedRequests AS r ON r.[Status] = stages.[Stage]
        GROUP BY stages.[Stage];

        ;WITH ScopedRequests AS
        (
            SELECT r.[Status], r.[CloseReason], r.[UpdatedUtc]
            FROM [asap].[TitleRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
            UNION ALL
            SELECT CASE WHEN r.[Status] = N'closed' THEN N'closed' ELSE N'additional_copies' END,
                   CAST(NULL AS nvarchar(64)), r.[UpdatedUtc]
            FROM [asap].[AdditionalCopyRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
        )
        SELECT
            COALESCE(NULLIF(LTRIM(RTRIM(r.[CloseReason])), N''), N'unrecorded') AS [Reason],
            COUNT_BIG(*) AS [Count]
        FROM ScopedRequests AS r
        WHERE r.[Status] = N'closed'
          AND r.[UpdatedUtc] >= @rangeStartUtc
          AND r.[UpdatedUtc] < @rangeEndExclusiveUtc
        GROUP BY COALESCE(NULLIF(LTRIM(RTRIM(r.[CloseReason])), N''), N'unrecorded')
        ORDER BY [Reason];

        ;WITH ScopedRequests AS
        (
            SELECT r.[Status], r.[CreatedUtc]
            FROM [asap].[TitleRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
            UNION ALL
            SELECT CASE WHEN r.[Status] = N'closed' THEN N'closed' ELSE N'additional_copies' END,
                   r.[CreatedUtc]
            FROM [asap].[AdditionalCopyRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
        )
        SELECT
            stages.[Stage],
            COALESCE(COUNT_BIG(r.[Status]), CONVERT(bigint, 0)) AS [Count],
            COALESCE(AVG(CONVERT(float, CASE
                WHEN @nowUtc >= r.[CreatedUtc]
                THEN DATEDIFF_BIG(NANOSECOND, r.[CreatedUtc], @nowUtc) / 86400000000000.0
                ELSE 0.0
            END)), 0.0) AS [AverageAgeDays]
        FROM (VALUES
            (N'suggestion'),
            (N'outstanding_purchase'),
            (N'pending_hold'),
            (N'hold_placed'),
            (N'additional_copies')) AS stages([Stage])
        LEFT JOIN ScopedRequests AS r ON r.[Status] = stages.[Stage]
        GROUP BY stages.[Stage];

        ;WITH ScopedRequests AS
        (
            SELECT r.[Status], r.[CreatedUtc]
            FROM [asap].[TitleRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
            UNION ALL
            SELECT CASE WHEN r.[Status] = N'closed' THEN N'closed' ELSE N'additional_copies' END,
                   r.[CreatedUtc]
            FROM [asap].[AdditionalCopyRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
        )
        SELECT COUNT_BIG(*)
        FROM ScopedRequests AS r
        WHERE r.[Status] <> N'closed'
          AND r.[CreatedUtc] < DATEADD(day, -30, @nowUtc);

        ;WITH ScopedTitleRequests AS
        (
            SELECT r.[Id], r.[IsbnCheckStatus]
            FROM [asap].[TitleRequest] AS r
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = r.[LibraryOrganizationId]
                AND o.[Id] <> 1 AND o.[IsActive] = 1
            WHERE @scopeOrganizationId IS NULL OR r.[LibraryOrganizationId] = @scopeOrganizationId
        )
        SELECT
            COALESCE((
                SELECT COUNT_BIG(*)
                FROM ScopedTitleRequests AS r
                WHERE EXISTS
                (
                    SELECT 1
                    FROM [asap].[TitleRequestWorkflowTag] AS link
                    INNER JOIN [asap].[WorkflowTag] AS tag ON tag.[Id] = link.[WorkflowTagId]
                    WHERE link.[TitleRequestId] = r.[Id]
                      AND LTRIM(RTRIM(tag.[Label])) COLLATE Latin1_General_100_CI_AS LIKE N'hold failed%'
                )
            ), CONVERT(bigint, 0)) AS [HoldFailures],
            COALESCE((
                SELECT COUNT_BIG(*)
                FROM ScopedTitleRequests AS r
                WHERE r.[IsbnCheckStatus] = N'error_max_retries'
            ), CONVERT(bigint, 0)) AS [IdentifierFailures];
        """;

    public async Task<AnalyticsResult> GetAsync(
        CurrentStaff staff,
        string? scope,
        string? orgId,
        string? range,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => item.Id != 1 && item.IsActive)
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .Select(item => new AnalyticsLibrary(item.Id.ToString(), item.DisplayName))
            .ToListAsync(cancellationToken);

        var selectedScope = string.IsNullOrWhiteSpace(scope) ? orgId : scope;
        var resolvedScope = ResolveScope(staff, selectedScope, organizations);
        if (!resolvedScope.IsValid)
        {
            return AnalyticsResult.InvalidScope();
        }

        var resolvedRange = ResolveDateRange(range, timeProvider.GetUtcNow(), configuration.Application.BusinessTimeZone!);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqlConnection)context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = AnalyticsSql;
        AddParameter(command, "@scopeOrganizationId", SqlDbType.Int, (object?)resolvedScope.OrganizationId ?? DBNull.Value);
        AddParameter(command, "@rangeStartUtc", SqlDbType.DateTime2, resolvedRange.StartUtc);
        AddParameter(command, "@rangeEndExclusiveUtc", SqlDbType.DateTime2, resolvedRange.EndExclusiveUtc);
        AddParameter(command, "@nowUtc", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Analytics aggregate query returned no summary row.");
        }

        var summary = new AnalyticsSummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? 0 : reader.GetDouble(4));

        var stageCounts = StageOrder.ToDictionary(stage => stage, _ => 0L, StringComparer.Ordinal);
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stageCounts[reader.GetString(0)] = reader.GetInt64(1);
        }

        var closedReasons = new List<AnalyticsClosedReason>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            closedReasons.Add(new AnalyticsClosedReason(reader.GetString(0), reader.GetInt64(1)));
        }

        var agingStages = new List<AnalyticsAgingStage>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            agingStages.Add(new AnalyticsAgingStage(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2)));
        }
        agingStages = OpenStageOrder
            .Select(stage => agingStages.FirstOrDefault(item => item.Status == stage) ?? new AnalyticsAgingStage(stage, 0, 0))
            .ToList();

        await reader.NextResultAsync(cancellationToken);
        var openOlderThanThreshold = await reader.ReadAsync(cancellationToken) ? reader.GetInt64(0) : 0L;

        await reader.NextResultAsync(cancellationToken);
        var holdFailures = 0L;
        var identifierFailures = 0L;
        if (await reader.ReadAsync(cancellationToken))
        {
            holdFailures = reader.GetInt64(0);
            identifierFailures = reader.GetInt64(1);
        }

        var availableLibraries = staff.Role == "super_admin" ? organizations : [];
        var response = new AnalyticsResponse(
            new AnalyticsScope(
                resolvedScope.Mode,
                resolvedScope.LibraryOrgId,
                resolvedScope.Label,
                staff.Role == "super_admin"),
            new AnalyticsDateRange(resolvedRange.Key, resolvedRange.Start, resolvedRange.End),
            availableLibraries,
            summary,
            stageCounts,
            closedReasons,
            new AnalyticsAging(30, openOlderThanThreshold, agingStages),
            new AnalyticsExceptions(holdFailures, identifierFailures));
        return new AnalyticsResult("ok", response);
    }

    public static AnalyticsResolvedScope ResolveScope(
        CurrentStaff staff,
        string? selectedScope,
        IReadOnlyCollection<AnalyticsLibrary> organizations)
    {
        var cleanSelected = selectedScope?.Trim() ?? string.Empty;
        if (staff.Role == "super_admin")
        {
            if (string.IsNullOrEmpty(cleanSelected) ||
                cleanSelected.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                cleanSelected.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                return new AnalyticsResolvedScope(true, null, "all", string.Empty, "All libraries");
            }

            if (int.TryParse(cleanSelected, out var selectedId) &&
                organizations.Any(item => item.OrgId == selectedId.ToString()))
            {
                var organization = organizations.Single(item => item.OrgId == selectedId.ToString());
                return new AnalyticsResolvedScope(true, selectedId, "library", organization.OrgId, organization.Name);
            }

            return new AnalyticsResolvedScope(false, null, "library", string.Empty, "");
        }

        var ownOrganization = organizations.FirstOrDefault(item => item.OrgId == staff.OrganizationId.ToString());
        return ownOrganization is null
            ? new AnalyticsResolvedScope(false, null, "library", string.Empty, "")
            : new AnalyticsResolvedScope(true, staff.OrganizationId, "library", ownOrganization.OrgId, ownOrganization.Name);
    }

    public static AnalyticsResolvedDateRange ResolveDateRange(
        string? requestedKey,
        DateTimeOffset nowUtc,
        string timeZoneId)
    {
        var key = requestedKey?.Trim() switch
        {
            "thisMonth" => "thisMonth",
            "lastMonth" => "lastMonth",
            "last90" => "last90",
            _ => "last30"
        };
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var localDate = TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date;
        var startLocalDate = key switch
        {
            "thisMonth" => new DateTime(localDate.Year, localDate.Month, 1),
            "lastMonth" => new DateTime(localDate.Year, localDate.Month, 1).AddMonths(-1),
            "last90" => localDate.AddDays(-90),
            _ => localDate.AddDays(-30)
        };
        var endExclusiveLocalDate = key switch
        {
            "thisMonth" => localDate.AddDays(1),
            "lastMonth" => new DateTime(localDate.Year, localDate.Month, 1),
            _ => localDate.AddDays(1)
        };
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(startLocalDate, DateTimeKind.Unspecified), timeZone);
        var endExclusiveUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(endExclusiveLocalDate, DateTimeKind.Unspecified), timeZone);
        return new AnalyticsResolvedDateRange(
            key,
            new DateTimeOffset(startUtc, TimeSpan.Zero),
            new DateTimeOffset(endExclusiveUtc.AddTicks(-1), TimeSpan.Zero),
            startUtc,
            endExclusiveUtc);
    }

    private static void AddParameter(SqlCommand command, string name, SqlDbType type, object value)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value;
    }
}
