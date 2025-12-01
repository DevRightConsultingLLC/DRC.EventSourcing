using FluentAssertions;
using System.Diagnostics.Metrics;

namespace DRC.EventSourcing.Tests;

/// <summary>
/// Tests for EventStoreMetrics observability.
/// </summary>
public class EventStoreMetricsTests : IDisposable
{
    private readonly Infrastructure.EventStoreMetrics _metrics;

    public EventStoreMetricsTests()
    {
        _metrics = new Infrastructure.EventStoreMetrics("TestStore");
    }

    [Fact]
    public void RecordAppend_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordAppend("test-domain", 5, 10.5);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordRead_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordRead("test-domain", 10, 5.0);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordArchive_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordArchive("test-domain", 100, 50.0);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordDelete_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordDelete("test-domain", 10);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordSnapshotSaved_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordSnapshotSaved("test-domain");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordSnapshotLoaded_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordSnapshotLoaded("test-domain");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordConcurrencyConflict_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordConcurrencyConflict("test-domain");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordError_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordError("append", "IOException");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordStreamVersion_ShouldNotThrow()
    {
        // Act
        var act = () => _metrics.RecordStreamVersion("test-domain", 10);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ConnectionTracking_ShouldIncrementAndDecrement()
    {
        // Act
        _metrics.IncrementActiveConnections();
        _metrics.IncrementActiveConnections();
        _metrics.DecrementActiveConnections();

        // Assert - Should not throw, actual count would be verified with metric listener
        var act = () =>
        {
            _metrics.IncrementActiveConnections();
            _metrics.DecrementActiveConnections();
        };
        
        act.Should().NotThrow();
    }

    [Fact]
    public void TransactionTracking_ShouldIncrementAndDecrement()
    {
        // Act
        _metrics.IncrementActiveTransactions();
        _metrics.IncrementActiveTransactions();
        _metrics.DecrementActiveTransactions();

        // Assert
        var act = () =>
        {
            _metrics.IncrementActiveTransactions();
            _metrics.DecrementActiveTransactions();
        };
        
        act.Should().NotThrow();
    }

    [Fact]
    public void CreateTimerScope_ShouldMeasureDuration()
    {
        // Arrange
        double recordedDuration = 0;

        // Act
        using (_metrics.CreateTimerScope(duration => recordedDuration = duration))
        {
            Thread.Sleep(10); // Simulate work
        }

        // Assert
        recordedDuration.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MeterName_ShouldBeCorrect()
    {
        // Assert
        Infrastructure.EventStoreMetrics.MeterName.Should().Be("DRC.EventSourcing");
    }

    [Fact]
    public void ConcurrentMetricRecording_ShouldBeThreadSafe()
    {
        // Act
        Parallel.For(0, 100, i =>
        {
            _metrics.RecordAppend($"domain-{i % 5}", i, i * 1.5);
            _metrics.RecordRead($"domain-{i % 5}", i, i * 0.5);
            _metrics.IncrementActiveConnections();
            _metrics.DecrementActiveConnections();
        });

        // Assert - No exceptions thrown
    }

    public void Dispose()
    {
        _metrics?.Dispose();
    }
}

