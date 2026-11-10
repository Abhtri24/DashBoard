/* =========================================================
   1. Add DepartmentRole if it does not already exist
   ========================================================= */

IF COL_LENGTH(N'org.UserDepartment', N'DepartmentRole') IS NULL
BEGIN
    ALTER TABLE org.UserDepartment
    ADD DepartmentRole NVARCHAR(30) NOT NULL
        CONSTRAINT DF_UserDepartment_DepartmentRole
        DEFAULT (N'User');
END;
GO

/* =========================================================
   2. Create or update the two department admin demo users
   ========================================================= */

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @SalesAdminId INT;
    DECLARE @FinanceAdminId INT;

    /* Sales Admin */

    SELECT @SalesAdminId = UserId
    FROM org.DemoUser
    WHERE UserKey = N'sales_admin';

    IF @SalesAdminId IS NULL
    BEGIN
        INSERT INTO org.DemoUser (UserKey, DisplayName, IsActive)
        VALUES (N'sales_admin', N'Sales Admin', 1);

        SET @SalesAdminId = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE org.DemoUser
        SET DisplayName = N'Sales Admin',
            IsActive = 1
        WHERE UserId = @SalesAdminId;
    END;


    /* Finance Admin */

    SELECT @FinanceAdminId = UserId
    FROM org.DemoUser
    WHERE UserKey = N'finance_admin';

    IF @FinanceAdminId IS NULL
    BEGIN
        INSERT INTO org.DemoUser (UserKey, DisplayName, IsActive)
        VALUES (N'finance_admin', N'Finance Admin', 1);

        SET @FinanceAdminId = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE org.DemoUser
        SET DisplayName = N'Finance Admin',
            IsActive = 1
        WHERE UserId = @FinanceAdminId;
    END;


    /* =====================================================
       3. Assign Sales Admin to Sales department
       ===================================================== */

    IF NOT EXISTS (
        SELECT 1
        FROM org.UserDepartment
        WHERE UserId = @SalesAdminId
          AND DepartmentKey = N'sales'
    )
    BEGIN
        INSERT INTO org.UserDepartment
            (UserId, DepartmentKey, DepartmentRole)
        VALUES
            (@SalesAdminId, N'sales', N'Admin');
    END
    ELSE
    BEGIN
        UPDATE org.UserDepartment
        SET DepartmentRole = N'Admin'
        WHERE UserId = @SalesAdminId
          AND DepartmentKey = N'sales';
    END;


    /* =====================================================
       4. Assign Finance Admin to Finance department
       ===================================================== */

    IF NOT EXISTS (
        SELECT 1
        FROM org.UserDepartment
        WHERE UserId = @FinanceAdminId
          AND DepartmentKey = N'finance'
    )
    BEGIN
        INSERT INTO org.UserDepartment
            (UserId, DepartmentKey, DepartmentRole)
        VALUES
            (@FinanceAdminId, N'finance', N'Admin');
    END
    ELSE
    BEGIN
        UPDATE org.UserDepartment
        SET DepartmentRole = N'Admin'
        WHERE UserId = @FinanceAdminId
          AND DepartmentKey = N'finance';
    END;


    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;


/* =========================================================
   5. Verify the result
   ========================================================= */

SELECT
    u.UserId,
    u.UserKey,
    u.DisplayName,
    u.IsActive,
    ud.DepartmentKey,
    ud.DepartmentRole
FROM org.DemoUser u
INNER JOIN org.UserDepartment ud
    ON u.UserId = ud.UserId
WHERE u.UserKey IN (N'sales_admin', N'finance_admin')
ORDER BY u.UserKey;