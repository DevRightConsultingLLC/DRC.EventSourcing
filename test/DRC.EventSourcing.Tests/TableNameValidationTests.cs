using FluentAssertions;

namespace DRC.EventSourcing.Tests;

/// <summary>
/// Tests for table name validation in IEventStoreOptions.
/// </summary>
public class TableNameValidationTests
{
    [Theory]
    [InlineData("ValidName")]
    [InlineData("Valid_Name_123")]
    [InlineData("_ValidName")]
    [InlineData("UPPERCASE")]
    [InlineData("mixedCase")]
    public void ValidateAndBuildTableName_WithValidNames_ShouldSucceed(string storeName)
    {
        // Arrange
        var options = new TestOptions { StoreName = storeName };

        // Act
        var tableName = ((IEventStoreOptions)options).EventsTableName;

        // Assert
        tableName.Should().Be($"{storeName}_Events");
    }

    [Theory]
    [InlineData("Invalid-Name", "contains invalid characters")]
    [InlineData("Invalid Name", "contains invalid characters")]
    [InlineData("123Invalid", "Must start with a letter or underscore")]
    [InlineData("Invalid@Name", "contains invalid characters")]
    [InlineData("Invalid.Name", "contains invalid characters")]
    [InlineData("", "cannot be null or empty")]
    [InlineData(null, "cannot be null or empty")]
    public void ValidateAndBuildTableName_WithInvalidNames_ShouldThrowArgumentException(
        string storeName, 
        string expectedMessagePart)
    {
        // Arrange & Act
        var act = () =>
        {
            var options = new TestOptions { StoreName = storeName };
            var _ = ((IEventStoreOptions)options).EventsTableName;
        };

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateAndBuildTableName_WithTooLongName_ShouldThrowArgumentException()
    {
        // Arrange
        var longName = new string('a', 51); // > 50 chars

        // Act
        var act = () =>
        {
            var options = new TestOptions { StoreName = longName };
            var _ = ((IEventStoreOptions)options).EventsTableName;
        };

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*cannot exceed 50 characters*");
    }

    [Fact]
    public void AllTableNames_ShouldBeValidated()
    {
        // Arrange
        var options = new TestOptions { StoreName = "TestStore" };

        // Act & Assert - All should succeed
        var eventsTable = ((IEventStoreOptions)options).EventsTableName;
        var streamsTable = ((IEventStoreOptions)options).StreamsTableName;
        var snapshotsTable = ((IEventStoreOptions)options).SnapshotsTableName;
        var archiveSegmentsTable = ((IEventStoreOptions)options).ArchiveSegmentsTableName;

        eventsTable.Should().Be("TestStore_Events");
        streamsTable.Should().Be("TestStore_Streams");
        snapshotsTable.Should().Be("TestStore_Snapshots");
        archiveSegmentsTable.Should().Be("TestStore_ArchiveSegments");
    }

    [Theory]
    [InlineData("'; DROP TABLE Events; --")]
    [InlineData("Test'; DELETE FROM Events; --")]
    [InlineData("Test OR 1=1")]
    public void ValidateAndBuildTableName_WithSQLInjectionAttempts_ShouldThrowArgumentException(string maliciousInput)
    {
        // Act
        var act = () =>
        {
            var options = new TestOptions { StoreName = maliciousInput };
            var _ = ((IEventStoreOptions)options).EventsTableName;
        };

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    private class TestOptions : IEventStoreOptions
    {
        public string ConnectionString { get; set; } = "test";
        public string StoreName { get; set; } = "Test";
        public string? ArchiveDirectory { get; set; }
    }
}

