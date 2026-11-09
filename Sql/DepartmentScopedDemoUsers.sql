/*
    Department-scoped demo users and roles.
    Safe to run repeatedly. Uses the existing org.DemoUser,
    org.UserDepartment and dash.Department tables.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'org.UserDepartment', N'DepartmentRole') IS NULL
BEGIN
    ALTER TABLE org.UserDepartment
        ADD DepartmentRole NVARCHAR(30) NOT NULL
            CONSTRAINT DF_UserDepartment_DepartmentRole DEFAULT (N'User');
END;

DECLARE @SalesAdminId INT;
DECLARE @FinanceAdminId INT;

IF NOT EXISTS
(
    SELECT 1
    FROM org.DemoUser
    WHERE DisplayName = N'Sales Admin'
)
BEGIN
    INSERT INTO org.DemoUser (DisplayName, IsActive)
    VALUES (N'Sales Admin', 1);
END;

SELECT TOP (1) @SalesAdminId = UserId
FROM org.DemoUser
WHERE DisplayName = N'Sales Admin'
ORDER BY UserId;

IF NOT EXISTS
(
    SELECT 1
    FROM org.DemoUser
    WHERE DisplayName = N'Finance Admin'
)
BEGIN
    INSERT INTO org.DemoUser (DisplayName, IsActive)
    VALUES (N'Finance Admin', 1);
END;

SELECT TOP (1) @FinanceAdminId = UserId
FROM org.DemoUser
WHERE DisplayName = N'Finance Admin'
ORDER BY UserId;

IF NOT EXISTS
(
    SELECT 1
    FROM org.UserDepartment
    WHERE UserId = @SalesAdminId
      AND DepartmentKey = N'sales'
)
BEGIN
    INSERT INTO org.UserDepartment (UserId, DepartmentKey, DepartmentRole)
    VALUES (@SalesAdminId, N'sales', N'Admin');
END
ELSE
BEGIN
    UPDATE org.UserDepartment
    SET DepartmentRole = N'Admin'
    WHERE UserId = @SalesAdminId
      AND DepartmentKey = N'sales';
END;

IF NOT EXISTS
(
    SELECT 1
    FROM org.UserDepartment
    WHERE UserId = @FinanceAdminId
      AND DepartmentKey = N'finance'
)
BEGIN
    INSERT INTO org.UserDepartment (UserId, DepartmentKey, DepartmentRole)
    VALUES (@FinanceAdminId, N'finance', N'Admin');
END
ELSE
BEGIN
    UPDATE org.UserDepartment
    SET DepartmentRole = N'Admin'
    WHERE UserId = @FinanceAdminId
      AND DepartmentKey = N'finance';
END;

COMMIT TRANSACTION;

SELECT
    u.UserId,
    u.DisplayName,
    ud.DepartmentKey,
    ud.DepartmentRole
FROM org.DemoUser u
INNER JOIN org.UserDepartment ud ON ud.UserId = u.UserId
WHERE u.DisplayName IN (N'Sales Admin', N'Finance Admin')
ORDER BY u.DisplayName;
