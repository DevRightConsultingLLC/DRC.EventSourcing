using FluentAssertions;

namespace DRC.EventSourcing.Tests;

/// <summary>
/// Tests for ConcurrencyException and related concurrency scenarios.
/// </summary>
public class ConcurrencyExceptionTests
{
    [Fact]
    public void ConcurrencyException_WithDefaultMessage_ShouldFormatCorrectly()
    {
        // Arrange
        var streamId = "test-stream";
        var expected = new StreamVersion(5);
        var actual = new StreamVersion(7);

        // Act
        var exception = new ConcurrencyException(streamId, expected, actual);

        // Assert
        exception.StreamId.Should().Be(streamId);
        exception.Expected.Should().Be(expected);
        exception.Actual.Should().Be(actual);
        exception.Message.Should().Contain("test-stream");
        exception.Message.Should().Contain("5");
        exception.Message.Should().Contain("7");
    }

    [Fact]
    public void ConcurrencyException_WithCustomMessage_ShouldUseCustomMessage()
    {
        // Arrange
        var customMessage = "Custom concurrency message";

        // Act
        var exception = new ConcurrencyException(
            "stream", 
            new StreamVersion(1), 
            new StreamVersion(2), 
            customMessage);

        // Assert
        exception.Message.Should().Be(customMessage);
    }

    [Fact]
    public void ConcurrencyException_WithNullStreamId_ShouldThrowArgumentNullException()
    {
        // Act
        var act = () => new ConcurrencyException(
            null!, 
            new StreamVersion(1), 
            new StreamVersion(2));

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void StreamVersion_New_ShouldReturnZero()
    {
        // Act
        var version = StreamVersion.New();

        // Assert
        version.Value.Should().Be(0);
    }

    [Fact]
    public void StreamVersion_Any_ShouldReturnMinusOne()
    {
        // Act
        var version = StreamVersion.Any();

        // Assert
        version.Value.Should().Be(-1);
    }

    [Fact]
    public void StreamVersion_Equality_ShouldWorkCorrectly()
    {
        // Arrange
        var v1 = new StreamVersion(5);
        var v2 = new StreamVersion(5);
        var v3 = new StreamVersion(6);

        // Assert
        v1.Should().Be(v2);
        v1.Should().NotBe(v3);
    }

    [Fact]
    public void GlobalPosition_ShouldStoreValue()
    {
        // Act
        var position = new GlobalPosition(12345);

        // Assert
        position.Value.Should().Be(12345);
    }

    [Fact]
    public void GlobalPosition_Equality_ShouldWorkCorrectly()
    {
        // Arrange
        var p1 = new GlobalPosition(100);
        var p2 = new GlobalPosition(100);
        var p3 = new GlobalPosition(200);

        // Assert
        p1.Should().Be(p2);
        p1.Should().NotBe(p3);
    }
}

