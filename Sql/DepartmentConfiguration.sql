IF SCHEMA_ID(N'dash') IS NULL
    EXEC(N'CREATE SCHEMA dash');
GO

IF OBJECT_ID(N'dash.Department', N'U') IS NULL
BEGIN
    CREATE TABLE dash.Department
    (
        DepartmentKey NVARCHAR(100) NOT NULL
            CONSTRAINT PK_Department PRIMARY KEY,
        DisplayName NVARCHAR(100) NOT NULL,
        IsActive BIT NOT NULL
            CONSTRAINT DF_Department_IsActive DEFAULT (1),
        SortOrder INT NOT NULL
            CONSTRAINT DF_Department_SortOrder DEFAULT (0),
        CreatedAt DATETIME2 NOT NULL
            CONSTRAINT DF_Department_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- Seed the departments previously exposed by the application.
-- The table becomes the source of truth after this script is run.
MERGE dash.Department AS target
USING
(
    VALUES
        (N'sales', N'Sales', 10),
        (N'finance', N'Finance', 20),
        (N'hr', N'HR', 30),
        (N'operations', N'Operations', 40)
) AS source (DepartmentKey, DisplayName, SortOrder)
ON target.DepartmentKey = source.DepartmentKey
WHEN NOT MATCHED THEN
    INSERT (DepartmentKey, DisplayName, IsActive, SortOrder)
    VALUES (source.DepartmentKey, source.DisplayName, 1, source.SortOrder);
GO
