IF SCHEMA_ID(N'dash') IS NULL
    EXEC(N'CREATE SCHEMA dash');
GO

-- Demo users are managed in org.DemoUser.
-- Department membership and roles are managed in org.UserDepartment.
GO

IF OBJECT_ID(N'dash.UserDashboardLayout', N'U') IS NULL
BEGIN
    CREATE TABLE dash.UserDashboardLayout
    (
        LayoutId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserDashboardLayout PRIMARY KEY,
        UserId INT NOT NULL,
        DashboardKey NVARCHAR(100) NOT NULL,
        DepartmentKey NVARCHAR(100) NOT NULL,
        LayoutJson NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_UserDashboardLayout_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_UserDashboardLayout_DemoUser
            FOREIGN KEY (UserId) REFERENCES org.DemoUser(UserId),
        CONSTRAINT UQ_UserDashboardLayout_User_Dashboard_Department
            UNIQUE (UserId, DashboardKey, DepartmentKey),
        CONSTRAINT CK_UserDashboardLayout_Json
            CHECK (ISJSON(LayoutJson) = 1)
    );
END;
GO
