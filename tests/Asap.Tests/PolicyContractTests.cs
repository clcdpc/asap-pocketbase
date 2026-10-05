using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;

namespace Asap.Tests;

[TestClass]
public sealed class PolicyContractTests
{
    [TestMethod]
    [DataRow(null, LibraryScopeKind.All, null)]
    [DataRow("", LibraryScopeKind.All, null)]
    [DataRow("ALL", LibraryScopeKind.All, null)]
    [DataRow("system", LibraryScopeKind.System, 1)]
    [DataRow("01", LibraryScopeKind.System, 1)]
    [DataRow(" 0003492 ", LibraryScopeKind.Library, 3492)]
    [DataRow("2147483647", LibraryScopeKind.Library, int.MaxValue)]
    public void ScopeBoundarySeparatesSystemFromAllAndCanonicalizesNativeIds(
        string? input, LibraryScopeKind kind, int? organizationId)
    {
        Assert.IsTrue(LibraryScope.TryParse(input, LibraryScope.All, out var scope));
        Assert.AreEqual(kind, scope.Kind);
        Assert.AreEqual(organizationId, scope.OrganizationId);
        Assert.AreNotEqual(LibraryScope.All, LibraryScope.System);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("2147483648")]
    [DataRow("3.0")]
    [DataRow("library")]
    [DataRow("+3")]
    public void ScopeBoundaryRejectsMalformedOrNonPositiveNativeIdentity(string input) =>
        Assert.IsFalse(LibraryScope.TryParse(input, LibraryScope.All, out _));

    [TestMethod]
    public void StaffPolicyUsesCurrentEmailTenantRoleRelationshipAndParticipationSnapshot()
    {
        var tenant = Guid.Parse("34900000-0000-0000-0000-000000000001");
        var configuration = new ExternalConfiguration();
        configuration.Authentication.Entra.AllowedTenantIds = [tenant.ToString()];
        var policy = new StaffEligibilityService(null!, configuration);
        var row = new StaffUser {
            Id = 7, UserPrincipalName = "staff@example.org",
            NormalizedUserPrincipalName = "STAFF@EXAMPLE.ORG", IsActive = true,
            Role = StaffRole.Admin, OrganizationId = 3492 };
        var evidence = new StaffIdentityEvidence(7, "STAFF@EXAMPLE.ORG", tenant);
        Assert.IsTrue(policy.IsCurrentAndEligible(evidence, row, 3492, StaffRoleRequirement.Admin));
        Assert.IsFalse(policy.IsCurrentAndEligible(evidence, row, 3493, StaffRoleRequirement.Admin));
        Assert.IsFalse(policy.IsCurrentAndEligible(evidence with { TenantId = Guid.Empty }, row, 3492, StaffRoleRequirement.Any));
        Assert.IsFalse(policy.IsCurrentAndEligible(evidence with { AuthenticationEmail = "OTHER@EXAMPLE.ORG" }, row, 3492, StaffRoleRequirement.Any));
        row.Role = StaffRole.Staff;
        Assert.IsFalse(policy.IsCurrentAndEligible(evidence, row, 3492, StaffRoleRequirement.Admin));
        row.OrganizationId = LibraryScope.SystemOrganizationId;
        Assert.IsFalse(StaffEligibilityService.IsAssignmentEligible(row, 3492));
        row.Role = StaffRole.SuperAdmin;
        Assert.IsTrue(StaffEligibilityService.IsAssignmentEligible(row, 3493));
        row.IsActive = false;
        Assert.IsFalse(policy.IsCurrentIdentity(evidence, row));
    }

    [TestMethod]
    public void WorkflowCapabilitiesAndMutationTargetsShareTransitionsAndProtectionGates()
    {
        var request = new TitleRequest { Barcode = "34900000000001", Title = "Policy fixture", Status = RequestStatus.Suggestion, BibId = 34901, AutoHold = false };
        var capabilities = TitleRequestWorkflowPolicy.Evaluate(request, false, false);
        Assert.IsTrue(capabilities.AllowedActions.Contains("purchase"));
        Assert.IsFalse(capabilities.AllowedActions.Contains("reopen"));
        var target = TitleRequestWorkflowPolicy.ResolveStatus("purchase", null, request.Status, request.BibId);
        Assert.AreEqual(RequestStatus.Closed,
            TitleRequestWorkflowPolicy.ResolveBibTargetStatus("purchase", request.Status, target, request.BibId, false, true));
        Assert.AreEqual(RequestStatus.Closed, TitleRequestWorkflowPolicy.AfterCatalogMatch(false));
        Assert.AreEqual(RequestStatus.PendingHold, TitleRequestWorkflowPolicy.AfterCatalogMatch(true));
        Assert.IsNull(TitleRequestWorkflowPolicy.ResolveStatus("purchase", RequestStatus.HoldPlaced, request.Status, request.BibId));
        request.Status = RequestStatus.PendingHold;
        request.AutoHold = true;
        request.BibIdStaffVerified = true;
        Assert.IsTrue(TitleRequestWorkflowPolicy.Evaluate(request, false, false).CanPlaceHold);
        request.LegacyHoldProtected = true;
        Assert.IsFalse(TitleRequestWorkflowPolicy.Evaluate(request, false, false).CanPlaceHold);
        request.Status = RequestStatus.Closed;
        request.LegacyHoldProtected = true;
        Assert.IsFalse(TitleRequestWorkflowPolicy.Evaluate(request, false, false).CanChangeWorkflowState);
        Assert.AreEqual(0, TitleRequestWorkflowPolicy.Evaluate(request, false, false).AllowedActions.Count);
        request.LegacyHoldProtected = false;
        Assert.IsTrue(TitleRequestWorkflowPolicy.Evaluate(request, false, false).AllowedActions.Contains("reopen"));
        Assert.AreEqual(0, TitleRequestWorkflowPolicy.Evaluate(request, false, false, true).AllowedActions.Count);
        Assert.AreEqual(0, TitleRequestWorkflowPolicy.Evaluate(request, true, false).AllowedActions.Count);
    }

    [TestMethod]
    public void TitleBindingKeepsOmissionNullAndNativeDateSeparateAndRejectsAmbiguity()
    {
        var omitted = new TitleRequestActionInput().ToCommand();
        Assert.IsFalse(omitted.Identifier.IsSupplied);
        Assert.IsFalse(omitted.ExactPublicationDate.IsSupplied);
        var clear = new TitleRequestActionInput {
            Identifier = JsonSerializer.SerializeToElement<string?>(null),
            ExactPublicationDate = JsonSerializer.SerializeToElement<string?>(null),
            Bibid = null }.ToCommand();
        Assert.IsTrue(clear.Identifier.IsSupplied);
        Assert.IsNull(clear.Identifier.Value);
        Assert.IsTrue(clear.ExactPublicationDate.IsSupplied);
        Assert.IsTrue(clear.BibidSupplied);
        var date = new TitleRequestActionInput {
            ExactPublicationDate = JsonSerializer.SerializeToElement("2026-10-01") }.ToCommand();
        Assert.AreEqual(new DateOnly(2026, 10, 1), date.ExactPublicationDate.Value);
        Assert.AreEqual("invalid_identifier", new TitleRequestActionInput {
            Identifier = JsonSerializer.SerializeToElement(123) }.ToCommand().ValidationError);
        using var duplicate = JsonDocument.Parse("{\"custom\":\"a\",\"custom\":\"b\"}");
        Assert.AreEqual("invalid_custom_fields", new TitleRequestActionInput {
            CustomFields = duplicate.RootElement }.ToCommand().ValidationError);
    }

    [TestMethod]
    public void SettingsBindingKeepsTypedScalarInheritanceAndRejectsMalformedValues()
    {
        var staff = new CurrentStaff(7, "STAFF@EXAMPLE.ORG", Guid.NewGuid(), null, null, null,
            StaffRole.SuperAdmin, 1, "System", true, false, null, false, false, false, []);
        var omitted = AdministrationSettingsBinding.Bind(staff, JsonSerializer.SerializeToElement(new { }));
        Assert.AreEqual(LibraryScope.System, omitted.Scope);
        Assert.IsFalse(omitted.Workflow.AutoPromote.IsSupplied);
        var clear = AdministrationSettingsBinding.Bind(staff, JsonSerializer.SerializeToElement(
            new { orgId = "03492", workflow = new { autoPromote = (bool?)null, suggestionLimit = 6 } }));
        Assert.IsNull(clear.BindingError);
        Assert.AreEqual(LibraryScope.ForLibrary(3492), clear.Scope);
        Assert.IsTrue(clear.Workflow.AutoPromote.IsSupplied);
        Assert.IsNull(clear.Workflow.AutoPromote.Value);
        Assert.AreEqual(6, clear.Workflow.SuggestionLimit.Value);
        foreach (var payload in new[] { "{\"workflow\":{\"suggestionLimit\":0}}",
                     "{\"workflow\":{\"autoPromote\":\"false\"}}", "{\"patron\":{\"loginNote\":42}}" })
        {
            using var document = JsonDocument.Parse(payload);
            Assert.AreEqual("settings_payload_invalid", AdministrationSettingsBinding.Bind(staff, document.RootElement).BindingError);
        }
    }
    [TestMethod]
    public void SettingsBoundaryPreservesNativeIntegrationIdentityAndRejectsWrongScopeAndKinds()
    {
        var staff = new CurrentStaff(7, "STAFF@EXAMPLE.ORG", Guid.NewGuid(), null, null, null,
            StaffRole.SuperAdmin, 1, "System", true, false, null, false, false, false, []);
        var native = AdministrationSettingsBinding.Bind(staff, JsonSerializer.SerializeToElement(
            new { polaris = new { workstationId = 75000, systemPolarisUserId = 2147483647 } }));
        Assert.IsNull(native.BindingError);
        Assert.AreEqual(75000, native.Polaris.WorkstationId.Value);
        Assert.AreEqual(int.MaxValue, native.Polaris.SystemPolarisUserId.Value);
        foreach (var (json, code) in new[] {
            ("{\"orgId\":true}", "organization_invalid"),
            ("{\"orgId\":3492,\"systemSettings\":{\"staffUrl\":null}}", "settings_system_only"),
            ("{\"polaris\":{\"workstationId\":0}}", "polaris_identity_invalid"),
            ("{\"polaris\":{\"workstationId\":2147483648}}", "polaris_identity_invalid"),
            ("{\"polaris\":{\"host\":17}}", "polaris_host_invalid"),
            ("{\"orgId\":3492,\"polaris\":{\"workstationId\":null}}", "polaris_settings_system_only"),
            ("{\"orgId\":3492,\"emails\":{\"postmarkToken\":null}}", "postmark_token_system_only"),
            ("{\"emails\":{\"serverToken\":\"secret\",\"clearServerToken\":true}}", "postmark_token_intent_conflict"),
            ("{\"polaris\":[]}", "settings_payload_invalid") })
        {
            using var document = JsonDocument.Parse(json);
            Assert.AreEqual(code, AdministrationSettingsBinding.Bind(staff, document.RootElement).BindingError);
        }
    }

}
