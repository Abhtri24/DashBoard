IF OBJECT_ID(N'dash.DashboardLayoutPreset', N'U') IS NULL
BEGIN
    CREATE TABLE dash.DashboardLayoutPreset
    (
        PresetId INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_DashboardLayoutPreset PRIMARY KEY,
        DepartmentKey NVARCHAR(100) NOT NULL,
        DashboardKey NVARCHAR(100) NOT NULL,
        PresetName NVARCHAR(100) NOT NULL,
        Description NVARCHAR(300) NULL,
        LayoutJson NVARCHAR(MAX) NOT NULL,
        CreatedByUserId INT NOT NULL,
        IsActive BIT NOT NULL
            CONSTRAINT DF_DashboardLayoutPreset_IsActive DEFAULT (1),
        CreatedAt DATETIME2 NOT NULL
            CONSTRAINT DF_DashboardLayoutPreset_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NOT NULL
            CONSTRAINT DF_DashboardLayoutPreset_UpdatedAt DEFAULT SYSUTCDATETIME(),

        CONSTRAINT FK_DashboardLayoutPreset_Department
            FOREIGN KEY (DepartmentKey) REFERENCES dash.Department(DepartmentKey),
        CONSTRAINT FK_DashboardLayoutPreset_Creator
            FOREIGN KEY (CreatedByUserId) REFERENCES org.DemoUser(UserId),
        CONSTRAINT CK_DashboardLayoutPreset_Json
            CHECK (ISJSON(LayoutJson) = 1),
        CONSTRAINT UQ_DashboardLayoutPreset_Name
            UNIQUE (DepartmentKey, DashboardKey, PresetName)
    );
END;
GO

SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA = N'dash'
  AND TABLE_NAME = N'DashboardLayoutPreset';
GO
