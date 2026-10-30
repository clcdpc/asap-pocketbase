-- Only the Local review initializer executes this fixed synthetic identity seed.
-- StaffUser IDs are allocated by SQL and resolved by authentication email later.
IF NOT EXISTS (SELECT 1 FROM asap.StaffUser WHERE NormalizedUserPrincipalName = N'REVIEWER@EXAMPLE.ORG')
    INSERT INTO asap.StaffUser
        (UserPrincipalName, NormalizedUserPrincipalName, DisplayName, NotificationEmail, Role, OrganizationId, IsActive)
    VALUES (N'reviewer@example.org', N'REVIEWER@EXAMPLE.ORG', N'ASAP Reviewer', N'reviewer@example.org', N'super_admin', 1, 1);
IF (SELECT COUNT(*) FROM asap.StaffUser WHERE NormalizedUserPrincipalName = N'REVIEWER@EXAMPLE.ORG'
    AND UserPrincipalName = N'reviewer@example.org' AND Role = N'super_admin' AND OrganizationId = 1 AND IsActive = 1) <> 1
    THROW 51000, 'The synthetic review super-admin is missing or inconsistent.', 1;
