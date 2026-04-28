-- ASAP SQL Server initial schema for PocketBase migration.

CREATE TABLE dbo.patron_users (
    id NVARCHAR(50) NOT NULL PRIMARY KEY,
    patron_id NVARCHAR(100) NOT NULL,
    barcode NVARCHAR(64) NULL,
    email NVARCHAR(320) NULL,
    first_name NVARCHAR(120) NULL,
    last_name NVARCHAR(120) NULL,
    created_at DATETIME2(3) NOT NULL CONSTRAINT DF_patron_users_created_at DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2(3) NOT NULL CONSTRAINT DF_patron_users_updated_at DEFAULT SYSUTCDATETIME(),
    row_version ROWVERSION NOT NULL
);

CREATE TABLE dbo.staff_users (
    id NVARCHAR(50) NOT NULL PRIMARY KEY,
    username NVARCHAR(200) NOT NULL,
    role NVARCHAR(40) NOT NULL,
    library_org_id NVARCHAR(50) NULL,
    display_name NVARCHAR(200) NULL,
    email NVARCHAR(320) NULL,
    is_active BIT NOT NULL CONSTRAINT DF_staff_users_is_active DEFAULT (1),
    created_at DATETIME2(3) NOT NULL CONSTRAINT DF_staff_users_created_at DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2(3) NOT NULL CONSTRAINT DF_staff_users_updated_at DEFAULT SYSUTCDATETIME(),
    row_version ROWVERSION NOT NULL,
    CONSTRAINT CK_staff_users_role CHECK (role IN ('super_admin', 'admin', 'staff'))
);

CREATE TABLE dbo.polaris_organizations (
    id NVARCHAR(50) NOT NULL PRIMARY KEY,
    org_id INT NOT NULL,
    code NVARCHAR(50) NOT NULL,
    display_name NVARCHAR(200) NOT NULL,
    is_enabled BIT NOT NULL CONSTRAINT DF_polaris_organizations_is_enabled DEFAULT (1),
    created_at DATETIME2(3) NOT NULL CONSTRAINT DF_orgs_created_at DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2(3) NOT NULL CONSTRAINT DF_orgs_updated_at DEFAULT SYSUTCDATETIME(),
    row_version ROWVERSION NOT NULL
);

CREATE TABLE dbo.app_settings (
    id NVARCHAR(50) NOT NULL PRIMARY KEY,
    allowed_staff_users NVARCHAR(MAX) NULL,
    ui_text NVARCHAR(MAX) NULL,
    emergency_password_hash NVARCHAR(400) NULL,
    created_at DATETIME2(3) NOT NULL CONSTRAINT DF_app_settings_created_at DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2(3) NOT NULL CONSTRAINT DF_app_settings_updated_at DEFAULT SYSUTCDATETIME(),
    row_version ROWVERSION NOT NULL
);

CREATE TABLE dbo.library_settings (
    id NVARCHAR(50) NOT NULL PRIMARY KEY,
    library_org_id NVARCHAR(50) NOT NULL,
    settings_json NVARCHAR(MAX) NOT NULL,
    created_at DATETIME2(3) NOT NULL CONSTRAINT DF_library_settings_created_at DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2(3) NOT NULL CONSTRAINT DF_library_settings_updated_at DEFAULT SYSUTCDATETIME(),
    row_version ROWVERSION NOT NULL,
    CONSTRAINT UQ_library_settings_library_org_id UNIQUE (library_org_id)
);

CREATE TABLE dbo.title_requests (
    id NVARCHAR(50) NOT NULL PRIMARY KEY,
    patron_user_id NVARCHAR(50) NULL,
    staff_user_id NVARCHAR(50) NULL,
    closed_by_staff_id NVARCHAR(50) NULL,
    library_org_id NVARCHAR(50) NOT NULL,
    title NVARCHAR(500) NULL,
    author NVARCHAR(500) NULL,
    status NVARCHAR(50) NOT NULL,
    close_reason NVARCHAR(50) NULL,
    close_message NVARCHAR(MAX) NULL,
    patron_name NVARCHAR(250) NOT NULL,
    patron_email NVARCHAR(320) NULL,
    created_at DATETIME2(3) NOT NULL CONSTRAINT DF_title_requests_created_at DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2(3) NOT NULL CONSTRAINT DF_title_requests_updated_at DEFAULT SYSUTCDATETIME(),
    row_version ROWVERSION NOT NULL,
    CONSTRAINT FK_title_requests_patron_user FOREIGN KEY (patron_user_id) REFERENCES dbo.patron_users (id),
    CONSTRAINT FK_title_requests_staff_user FOREIGN KEY (staff_user_id) REFERENCES dbo.staff_users (id),
    CONSTRAINT FK_title_requests_closed_by_staff FOREIGN KEY (closed_by_staff_id) REFERENCES dbo.staff_users (id),
    CONSTRAINT CK_title_requests_status CHECK (status IN ('new','approved','rejected','outstanding','hold_placed','hold_completed','closed')),
    CONSTRAINT CK_title_requests_close_reason CHECK (close_reason IS NULL OR close_reason IN ('reject','silent_close','hold_completed','timeout','duplicate'))
);

CREATE TABLE dbo.audit_logs (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    actor_staff_user_id NVARCHAR(50) NULL,
    action NVARCHAR(120) NOT NULL,
    entity_type NVARCHAR(120) NOT NULL,
    entity_id NVARCHAR(50) NULL,
    metadata_json NVARCHAR(MAX) NULL,
    created_at DATETIME2(3) NOT NULL CONSTRAINT DF_audit_logs_created_at DEFAULT SYSUTCDATETIME()
);

CREATE UNIQUE INDEX UX_staff_users_username ON dbo.staff_users (username);
CREATE UNIQUE INDEX UX_patron_users_patron_id ON dbo.patron_users (patron_id);
CREATE UNIQUE INDEX UX_polaris_organizations_org_id ON dbo.polaris_organizations (org_id);
CREATE INDEX IX_title_requests_status_library_created ON dbo.title_requests (status, library_org_id, created_at DESC)
    INCLUDE (close_reason, patron_name, title);
CREATE INDEX IX_title_requests_library_created ON dbo.title_requests (library_org_id, created_at DESC)
    INCLUDE (status, close_reason, patron_name, title);
CREATE INDEX IX_title_requests_created_at ON dbo.title_requests (created_at DESC);
