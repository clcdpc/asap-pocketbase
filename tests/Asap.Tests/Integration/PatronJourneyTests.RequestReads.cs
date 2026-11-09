using System.Data.Common;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(12)]
    public async Task RequestReadQueryEvidenceAndMixedRecoveryHistory(int requestCount)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var ids = new List<long> { fixture.Source.Id };
        try
        {
            await using (var context = await fixture.Contexts.CreateDbContextAsync())
            {
                for (var index = 1; index < requestCount; index++)
                {
                    var request = new TitleRequest
                    {
                        LibraryOrganizationId = 2, Barcode = $"read-evidence-{index}", Title = fixture.Source.Title,
                        MaterialFormatId = fixture.Source.MaterialFormatId, Status = "suggestion",
                        CreatedUtc = fixture.Source.CreatedUtc, UpdatedUtc = fixture.Source.UpdatedUtc
                    };
                    context.TitleRequests.Add(request);
                    await context.SaveChangesAsync();
                    ids.Add(request.Id);
                }
                foreach (var id in ids)
                {
                    for (var index = 0; index < 15; index++)
                    {
                        context.TitleRequestEvents.Add(new TitleRequestEvent
                        {
                            TitleRequestId = id, EventType = "staff_note", ActorType = "staff",
                            Message = new string('n', 300), CreatedUtc = fixture.Source.CreatedUtc.AddSeconds(index)
                        });
                    }
                    for (var attempt = 1; attempt <= 6; attempt++)
                    {
                        context.HoldPlacementOperations.Add(new HoldPlacementOperation
                        {
                            TitleRequestId = id, PatronBarcodeSnapshot = "123456789", BibIdSnapshot = 9001,
                            AttemptNumber = attempt, ExecutionEpoch = 1,
                            State = attempt == 1 ? HoldOperationState.Succeeded : attempt == 6
                                ? HoldOperationState.OperatorRequired : HoldOperationState.Failed,
                            Phase = HoldOperationPhase.ResultRecorded, RequestStartedUtc = fixture.Source.CreatedUtc,
                            CompletedUtc = attempt == 6 ? null : fixture.Source.CreatedUtc.AddMinutes(1),
                            DetailJson = "{\"evidence\":\"retained\"}"
                        });
                    }
                }
                await context.SaveChangesAsync();
            }
            var probe = new RequestReadProbe();
            var contexts = new QueueProbeContextFactory(databaseConnectionString, probe);
            var service = ActivatorUtilities.CreateInstance<TitleRequestViewService>(factory!.Services, contexts);
            var evidence = new Dictionary<string, object>();
            var list = await service.ListAsync(fixture.Actor, LibraryScope.ForLibrary(2), CancellationToken.None);
            Assert.IsNotNull(list);
            var listReads = probe.SnapshotAndReset();
            evidence["list"] = listReads;
            Assert.IsFalse(listReads.Reads.Any(item => item.Columns.Contains("Message") || item.Columns.Contains("MetadataJson")),
                "List phase entry must not load activity history.");
            var detail = await service.GetAsync(fixture.Actor, fixture.Source.Id.ToString(), CancellationToken.None);
            Assert.IsNotNull(detail);
            var detailReads = probe.SnapshotAndReset();
            evidence["detail"] = detailReads;
            var relatedReads = detailReads.Reads.Single(item => item.Columns.Contains("Title") &&
                item.Columns.Contains("Identifier") && !item.Columns.Contains("Barcode"));
            evidence["related"] = relatedReads;
            Assert.AreEqual(requestCount + 1, relatedReads.ReadCount, "EF counts the final unsuccessful Read call.");
            Assert.IsFalse(detailReads.Entities.ContainsKey(nameof(TitleRequestEvent)));
            Assert.IsFalse(detailReads.Entities.ContainsKey(nameof(HoldPlacementOperation)));
            Assert.AreEqual("hold_operation_incomplete", detail.Capabilities.BlockingReason);
            Assert.AreEqual(6, detail.HoldOperation!.AttemptNumber);
            Assert.AreEqual("*****6789", detail.HoldOperation.PatronBarcodeSnapshotMasked);
            Assert.AreEqual(15, detail.Activity.Count);
            Assert.AreEqual(requestCount - 1, detail.RelatedRequests!.Count);
            Assert.AreEqual(detail.PhaseEnteredAt, list.Items.Single(item => item.Id == detail.Id).PhaseEnteredAt);
            Assert.AreEqual(JsonSerializer.Serialize(detail with { Activity = [] }),
                JsonSerializer.Serialize(list.Items.Single(item => item.Id == detail.Id)), "List and detail share all non-activity DTO fields.");
            var templates = await service.GetRejectionTemplatesAsync(fixture.Actor, detail.Id, CancellationToken.None);
            Assert.IsNotNull(templates);
            var templateReads = probe.SnapshotAndReset();
            evidence["templates"] = templateReads;
            foreach (var unrelated in new[] { "TitleRequestEvent", "HoldPlacementOperation", "PickupPreferenceOperation", "EmailOutbox", "WorkflowSettings" })
            {
                Assert.IsFalse(templateReads.Reads.Any(item => item.Sql.Contains($"[asap].[{unrelated}]", StringComparison.Ordinal)),
                    $"Template lookup must not assemble {unrelated}.");
            }
            Assert.IsFalse(templateReads.Reads.Any(item => item.Columns.Contains("Barcode")));

            // The older success independently protects history after the newer attempt completes unsuccessfully.
            await using (var context = await fixture.Contexts.CreateDbContextAsync())
            {
                await context.HoldPlacementOperations.Where(item => item.TitleRequestId == fixture.Source.Id && item.AttemptNumber == 6)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.State, HoldOperationState.Failed)
                        .SetProperty(item => item.CompletedUtc, fixture.Source.CreatedUtc.AddMinutes(2)));
                await context.TitleRequests.Where(item => item.Id == fixture.Source.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, RequestStatus.Closed)
                        .SetProperty(item => item.CloseReason, "manual"));
            }
            var protectedDetail = await service.GetAsync(fixture.Actor, detail.Id, CancellationToken.None);
            Assert.IsNotNull(protectedDetail);
            Assert.AreEqual("hold_history_retained", protectedDetail.Capabilities.BlockingReason);
            Assert.AreEqual(1, protectedDetail.HoldOperation!.AttemptNumber);
            Assert.IsFalse(protectedDetail.Capabilities.AllowedActions.Contains("reopen"));
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[PickupPreferenceOperation]
                    ([Id], [Barcode], [PatronId], [LibraryOrganizationId], [TitleRequestId], [Origin],
                     [ToPickupBranchId], [ToPickupBranchName], [State], [DispatchStartedUtc], [ConfirmedByRead])
                VALUES (NEWID(), @barcode, 88001, 2, @id, N'request', 101, N'Main Library', 1, SYSUTCDATETIME(), 0);
                """, ("@barcode", fixture.Source.Barcode), ("@id", fixture.Source.Id));
            var pickup = await service.GetAsync(fixture.Actor, detail.Id, CancellationToken.None);
            Assert.AreEqual("pickup_reconciliation_required", pickup!.Capabilities.BlockingReason);
            Assert.AreEqual(101, pickup.PickupOperation!.TargetBranchId);
            Assert.AreEqual(1, pickup.HoldOperation!.AttemptNumber, "Pickup recovery must not hide successful hold history.");
            var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
            TestContext.WriteLine(json);
            var output = TestArtifactPaths.FindRepositoryFile(".artifacts", "request-reads");
            Directory.CreateDirectory(output);
            var label = Environment.GetEnvironmentVariable("ASAP_REQUEST_READ_EVIDENCE") ?? "current";
            await File.WriteAllTextAsync(Path.Combine(output, $"{label}-{requestCount}.json"), json);
        }
        finally
        {
            await using var context = await fixture.Contexts.CreateDbContextAsync();
            await ExecuteNonQueryAsync("DELETE FROM [asap].[PickupPreferenceOperation] WHERE [TitleRequestId] = @id;", ("@id", fixture.Source.Id));
            await context.HoldPlacementOperations.Where(item => ids.Contains(item.TitleRequestId)).ExecuteDeleteAsync();
            await context.TitleRequests.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed class RequestReadProbe : DbCommandInterceptor, IMaterializationInterceptor
    {
        private readonly List<RequestSqlRead> reads = [];
        private readonly Dictionary<string, int> entities = [];

        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            var name = entity.GetType().Name;
            entities[name] = entities.GetValueOrDefault(name) + 1;
            return entity;
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData data, InterceptionResult result)
        {
            reads.Add(new RequestSqlRead(command.CommandText, data.ReadCount,
                Enumerable.Range(0, data.DataReader.FieldCount).Select(data.DataReader.GetName).ToArray()));
            return ValueTask.FromResult(result);
        }

        public RequestReadSnapshot SnapshotAndReset()
        {
            var snapshot = new RequestReadSnapshot(reads.Count, reads.ToArray(), new Dictionary<string, int>(entities));
            reads.Clear();
            entities.Clear();
            return snapshot;
        }
    }

    private sealed record RequestSqlRead(string Sql, int ReadCount, string[] Columns);
    private sealed record RequestReadSnapshot(int Commands, RequestSqlRead[] Reads, Dictionary<string, int> Entities);

    [TestMethod]
    public async Task RelatedReadsPreserveEachDirectKeyDeduplicationAndEmptyNormalization()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var ids = new List<long>();
        try
        {
            await using (var context = await fixture.Contexts.CreateDbContextAsync())
            {
                var source = await context.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
                source.Title = "Réad 012";
                source.Identifier = "AB-12";
                source.BibId = 908765;
                foreach (var (title, identifier, bib) in new (string, string?, int?)[]
                    { ("RÉAD-012", "ab12", 908765), ("Identifier only", "ab 12", null),
                      ("BIB only", null, 908765), ("réad012", null, null), ("!!!", "---", null) })
                {
                    var candidate = new TitleRequest { LibraryOrganizationId = 2, Barcode = $"related-{ids.Count}",
                        Title = title, Identifier = identifier, BibId = bib, MaterialFormatId = source.MaterialFormatId,
                        Status = RequestStatus.Suggestion, CreatedUtc = source.CreatedUtc, UpdatedUtc = source.UpdatedUtc };
                    context.TitleRequests.Add(candidate);
                    await context.SaveChangesAsync();
                    ids.Add(candidate.Id);
                }
            }
            var views = factory!.Services.GetRequiredService<TitleRequestViewService>();
            var detail = await views.GetAsync(fixture.Actor, fixture.Source.Id.ToString(), CancellationToken.None);
            Assert.AreEqual(4, detail!.RelatedRequests!.Count, "Each key matches directly; a candidate matching all three counts once; self is excluded.");
            Assert.AreEqual(4, detail.RelatedRequests.StatusAndLibraryCounts.Single().Count);
            await using (var context = await fixture.Contexts.CreateDbContextAsync())
            {
                await context.TitleRequests.Where(item => item.Id == fixture.Source.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.Title, "???")
                        .SetProperty(item => item.Identifier, " ").SetProperty(item => item.BibId, (int?)null)
                        .SetProperty(item => item.CustomFieldsJson, "[]"));
            }
            var empty = await views.GetAsync(fixture.Actor, fixture.Source.Id.ToString(), CancellationToken.None);
            Assert.AreEqual(0, empty!.RelatedRequests!.Count, "Punctuation-only/blank normalized keys must not relate to one another.");
            Assert.IsFalse(empty.CustomFieldsValid);
            Assert.IsNull(empty.CustomFields);
        }
        finally
        {
            await using var context = await fixture.Contexts.CreateDbContextAsync();
            await context.TitleRequests.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    [DataRow("inactive")]
    [DataRow("role")]
    [DataRow("library")]
    [DataRow("request")]
    [DataRow("classification")]
    [DataRow("cancel")]
    public async Task TemplateReadsRevalidateAuthorityAfterOrganizationLockWait(string mutation)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        await using var blocker = new SqlConnection(databaseConnectionString);
        await blocker.OpenAsync();
        var sessionId = Convert.ToInt32(await ExecuteScalarAsync(blocker, "SELECT @@SPID;"));
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using var command = new SqlCommand("SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = 2;", blocker, transaction);
        await command.ExecuteScalarAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = factory!.Services.GetRequiredService<TitleRequestViewService>()
            .GetRejectionTemplatesAsync(fixture.Actor, fixture.Source.Id.ToString(), cancellation.Token);
        try
        {
            await WaitForBlockedSessionAsync(sessionId);
            var sql = mutation switch
            {
                "inactive" => "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;",
                "role" => "UPDATE [asap].[StaffUser] SET [Role] = N'admin' WHERE [Id] = @id;",
                "library" => "UPDATE [asap].[StaffUser] SET [OrganizationId] = 101 WHERE [Id] = @id;",
                "request" => "UPDATE [asap].[TitleRequest] SET [LibraryOrganizationId] = 101 WHERE [Id] = @id;",
                "classification" => "UPDATE [asap].[Organization] SET [OrganizationCodeId] = 3 WHERE [Id] = 2;",
                _ => null
            };
            if (sql is not null)
            {
                await using var change = new SqlCommand(sql, blocker, transaction);
                change.Parameters.AddWithValue("@id", mutation == "request" ? fixture.Source.Id : fixture.Actor.Id);
                await change.ExecuteNonQueryAsync();
            }
            if (mutation == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            }
            else
            {
                await transaction.CommitAsync();
                Assert.IsNull(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            }
        }
        finally
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync();
            }
            if (mutation == "classification")
            {
                await ExecuteNonQueryAsync("UPDATE [asap].[Organization] SET [OrganizationCodeId] = 2 WHERE [Id] = 2;");
            }
            cancellation.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // The cancellation case must also release its serializable reader.
            }
        }
    }
}
