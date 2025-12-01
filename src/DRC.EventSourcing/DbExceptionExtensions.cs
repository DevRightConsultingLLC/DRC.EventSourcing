﻿// ...existing code...

using System.Reflection;

namespace DRC.EventSourcing;

/// <summary>
/// Extension methods for detecting database-specific constraint violation exceptions.
/// </summary>
/// <remarks>
/// <para>This class provides database-agnostic detection of unique constraint violations using reflection
/// to avoid compile-time dependencies on specific ADO.NET providers.</para>
/// 
/// <para><b>Supported Database Providers:</b></para>
/// <list type="bullet">
///   <item><b>SQLite:</b> Microsoft.Data.Sqlite.SqliteException</item>
///   <item><b>SQL Server:</b> Microsoft.Data.SqlClient.SqlException and System.Data.SqlClient.SqlException</item>
///   <item><b>PostgreSQL:</b> Npgsql.PostgresException (via message parsing)</item>
///   <item><b>MySQL:</b> MySql.Data.MySqlClient.MySqlException (via message parsing)</item>
/// </list>
/// 
/// <para><b>Detection Methods:</b></para>
/// <list type="number">
///   <item>Reflection-based property access for error codes</item>
///   <item>Message string pattern matching as fallback</item>
///   <item>Recursive checking of inner exceptions</item>
/// </list>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>This extension method is thread-safe and can be called concurrently.</para>
/// </remarks>
public static class DbExceptionExtensions
{
    /// <summary>
    /// Detects whether the provided exception (or any inner exception) represents a unique constraint violation
    /// for common ADO.NET providers without requiring provider assemblies at compile time.
    /// </summary>
    /// <param name="ex">The exception to check</param>
    /// <returns>True if the exception represents a unique constraint violation; otherwise false</returns>
    /// <remarks>
    /// <para>This method uses reflection to inspect database-specific exception properties,
    /// making it provider-agnostic while still providing accurate detection.</para>
    /// 
    /// <para><b>SQLite Error Codes:</b></para>
    /// <list type="bullet">
    ///   <item>19: SQLITE_CONSTRAINT (generic constraint violation)</item>
    ///   <item>2067: SQLITE_CONSTRAINT_UNIQUE (specific to unique constraints)</item>
    ///   <item>1555: SQLITE_CONSTRAINT_PRIMARYKEY</item>
    /// </list>
    /// 
    /// <para><b>SQL Server Error Codes:</b></para>
    /// <list type="bullet">
    ///   <item>2627: Violation of PRIMARY KEY or UNIQUE constraint</item>
    ///   <item>2601: Cannot insert duplicate key row</item>
    /// </list>
    /// 
    /// <para><b>Performance:</b></para>
    /// <para>Reflection operations are cached by the .NET runtime, so this method has minimal overhead.
    /// Typically sub-millisecond execution time.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// try
    /// {
    ///     await connection.ExecuteAsync(insertQuery);
    /// }
    /// catch (Exception ex) when (ex.IsUniqueConstraintViolation())
    /// {
    ///     // Handle duplicate key scenario
    ///     logger.LogWarning("Duplicate key detected, handling gracefully");
    /// }
    /// </code>
    /// </example>
    public static bool IsUniqueConstraintViolation(this Exception? ex)
    {
        if (ex is null) return false;

        var t = ex.GetType();
        var typeName = t.FullName ?? string.Empty;

        try
        {
            // SQLite: Microsoft.Data.Sqlite.SqliteException
            if (typeName.Equals("Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal) || 
                typeName.EndsWith(".SqliteException", StringComparison.Ordinal))
            {
                // First, try to get the extended error code (more specific)
                var extProp = t.GetProperty("SqliteExtendedErrorCode", BindingFlags.Public | BindingFlags.Instance);
                if (extProp != null)
                {
                    var val = extProp.GetValue(ex);
                    if (val is int extCode)
                    {
                        // 2067 = SQLITE_CONSTRAINT_UNIQUE
                        // 1555 = SQLITE_CONSTRAINT_PRIMARYKEY
                        if (extCode == 2067 || extCode == 1555)
                            return true;
                    }
                }

                // Fallback: Check basic error code
                var prop = t.GetProperty("SqliteErrorCode", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(ex);
                    if (val is int code)
                    {
                        // 19 = SQLITE_CONSTRAINT (but check message to confirm it's UNIQUE)
                        if (code == 19)
                        {
                            var message = ex.Message;
                            return message.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   message.IndexOf("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                    }
                }
            }

            // SQL Server: Microsoft.Data.SqlClient.SqlException or System.Data.SqlClient.SqlException
            if (typeName.Equals("Microsoft.Data.SqlClient.SqlException", StringComparison.Ordinal)
                || typeName.Equals("System.Data.SqlClient.SqlException", StringComparison.Ordinal)
                || typeName.EndsWith(".SqlException", StringComparison.Ordinal))
            {
                var prop = t.GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(ex);
                    if (val is int num)
                    {
                        // 2627 = Violation of UNIQUE KEY or PRIMARY KEY constraint
                        // 2601 = Cannot insert duplicate key row in object
                        if (num == 2627 || num == 2601)
                            return true;
                    }
                }
            }

            // PostgreSQL: Npgsql.PostgresException
            if (typeName.Equals("Npgsql.PostgresException", StringComparison.Ordinal) ||
                typeName.EndsWith(".PostgresException", StringComparison.Ordinal))
            {
                // Check SqlState property for 23505 (unique_violation)
                var prop = t.GetProperty("SqlState", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(ex);
                    if (val is string sqlState && sqlState == "23505")
                        return true;
                }
            }

            // MySQL: MySql.Data.MySqlClient.MySqlException or MySqlConnector.MySqlException
            if (typeName.Contains("MySqlException", StringComparison.Ordinal))
            {
                // Check Number property for 1062 (duplicate entry)
                var prop = t.GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(ex);
                    if (val is int num && num == 1062)
                        return true;
                }
            }

            // Fallback: Message string pattern matching
            // This catches providers we don't explicitly support or when reflection fails
            var errorMessage = ex.Message;
            if (errorMessage.IndexOf("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errorMessage.IndexOf("unique constraint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errorMessage.IndexOf("duplicate key", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errorMessage.IndexOf("Violation of UNIQUE KEY constraint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errorMessage.IndexOf("Violation of PRIMARY KEY constraint", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        catch
        {
            // Reflection-based detection must not throw; fall back to checking inner exception
            // Swallow any reflection errors and continue to inner exception check
        }

        // Recursively check inner exception
        return IsUniqueConstraintViolation(ex.InnerException);
    }
}
// ...existing code...
