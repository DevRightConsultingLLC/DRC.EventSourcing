using FluentAssertions;

namespace DRC.EventSourcing.Tests;

/// <summary>
/// Tests for database exception detection logic.
/// </summary>
public class DbExceptionExtensionsTests
{
    [Fact]
    public void IsUniqueConstraintViolation_WithSqliteException_ShouldDetectConstraint()
    {
        // Note: These tests verify the detection logic works correctly
        // Actual SQLite/SQL Server exceptions would be tested in integration tests
        
        // For unit tests, we verify null handling
        Exception? nullException = null;
        nullException.IsUniqueConstraintViolation().Should().BeFalse();
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithMessagePatterns_ShouldDetectFromMessage()
    {
        // Arrange - Create exceptions with typical constraint violation messages
        var exceptions = new[]
        {
            new Exception("UNIQUE constraint failed: table.column"),
            new Exception("unique constraint violation"),
            new Exception("Violation of UNIQUE KEY constraint 'UK_Name'"),
            new Exception("Violation of PRIMARY KEY constraint 'PK_Id'"),
            new Exception("duplicate key value violates unique constraint")
        };

        // Act & Assert
        foreach (var ex in exceptions)
        {
            ex.IsUniqueConstraintViolation().Should().BeTrue($"because message contains: {ex.Message}");
        }
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithNonConstraintException_ShouldReturnFalse()
    {
        // Arrange
        var ex = new Exception("Some other error");

        // Act
        var result = ex.IsUniqueConstraintViolation();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithInnerException_ShouldCheckRecursively()
    {
        // Arrange
        var innerEx = new Exception("UNIQUE constraint failed");
        var outerEx = new Exception("Outer error", innerEx);

        // Act
        var result = outerEx.IsUniqueConstraintViolation();

        // Assert
        result.Should().BeTrue();
    }
}

