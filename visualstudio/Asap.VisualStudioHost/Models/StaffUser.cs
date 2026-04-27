namespace Asap.VisualStudioHost.Models;

public sealed class StaffUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Role { get; set; } = "super_admin";

    public string Scope { get; set; } = "system";

    public string LibraryOrgId { get; set; } = "system";

    public string LibraryOrgName { get; set; } = "System";

    public bool Active { get; set; } = true;

    public string IdentityKey => Username;
}
