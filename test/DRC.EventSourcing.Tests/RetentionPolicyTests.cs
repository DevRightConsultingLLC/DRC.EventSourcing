using FluentAssertions;
using System.Collections.Concurrent;

namespace DRC.EventSourcing.Tests;

/// <summary>
/// Tests for retention policy provider.
/// </summary>
public class RetentionPolicyTests
{
    [Fact]
    public void DefaultRetentionPolicyProvider_WithNoPolicies_ShouldReturnDefault()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();

        // Act
        var policy = provider.GetPolicy("any-domain");

        // Assert
        policy.Should().NotBeNull();
        policy.RetentionMode.Should().Be(RetentionMode.ColdArchivable);
    }

    [Fact]
    public void DefaultRetentionPolicyProvider_WithPolicies_ShouldReturnMatchingPolicy()
    {
        // Arrange
        var policies = new[]
        {
            new DomainRetentionPolicy { Domain = "orders", RetentionMode = RetentionMode.FullHistory },
            new DomainRetentionPolicy { Domain = "audit", RetentionMode = RetentionMode.Default }
        };
        
        var provider = new DefaultDomainRetentionPolicyProvider(policies);

        // Act
        var ordersPolicy = provider.GetPolicy("orders");
        var auditPolicy = provider.GetPolicy("audit");

        // Assert
        ordersPolicy.RetentionMode.Should().Be(RetentionMode.FullHistory);
        auditPolicy.RetentionMode.Should().Be(RetentionMode.Default);
    }

    [Fact]
    public void DefaultRetentionPolicyProvider_IsCaseInsensitive()
    {
        // Arrange
        var policies = new[]
        {
            new DomainRetentionPolicy { Domain = "Orders", RetentionMode = RetentionMode.FullHistory }
        };
        
        var provider = new DefaultDomainRetentionPolicyProvider(policies);

        // Act
        var policy1 = provider.GetPolicy("orders");
        var policy2 = provider.GetPolicy("ORDERS");
        var policy3 = provider.GetPolicy("Orders");

        // Assert
        policy1.RetentionMode.Should().Be(RetentionMode.FullHistory);
        policy2.RetentionMode.Should().Be(RetentionMode.FullHistory);
        policy3.RetentionMode.Should().Be(RetentionMode.FullHistory);
    }

    [Fact]
    public void AddOrReplace_ShouldAddNewPolicy()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();
        var newPolicy = new DomainRetentionPolicy 
        { 
            Domain = "new-domain", 
            RetentionMode = RetentionMode.HardDeletable 
        };

        // Act
        provider.AddOrReplace(newPolicy);
        var result = provider.GetPolicy("new-domain");

        // Assert
        result.RetentionMode.Should().Be(RetentionMode.HardDeletable);
    }

    [Fact]
    public void AddOrReplace_ShouldReplaceExistingPolicy()
    {
        // Arrange
        var policies = new[]
        {
            new DomainRetentionPolicy { Domain = "orders", RetentionMode = RetentionMode.Default }
        };
        
        var provider = new DefaultDomainRetentionPolicyProvider(policies);
        
        var updatedPolicy = new DomainRetentionPolicy 
        { 
            Domain = "orders", 
            RetentionMode = RetentionMode.FullHistory 
        };

        // Act
        provider.AddOrReplace(updatedPolicy);
        var result = provider.GetPolicy("orders");

        // Assert
        result.RetentionMode.Should().Be(RetentionMode.FullHistory);
    }

    [Fact]
    public void AddOrReplace_WithNullPolicy_ShouldThrowArgumentNullException()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();

        // Act
        var act = () => provider.AddOrReplace(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddOrReplace_WithEmptyDomain_ShouldThrowArgumentException()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();
        var policy = new DomainRetentionPolicy 
        { 
            Domain = string.Empty, 
            RetentionMode = RetentionMode.Default 
        };

        // Act
        var act = () => provider.AddOrReplace(policy);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetPolicy_WithNullDomain_ShouldReturnDefaultPolicy()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();

        // Act
        var policy = provider.GetPolicy(null!);

        // Assert
        policy.RetentionMode.Should().Be(RetentionMode.ColdArchivable);
    }

    [Fact]
    public void ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();
        var errors = new ConcurrentBag<Exception>();

        // Act - Concurrent reads and writes
        Parallel.For(0, 100, i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    // Write
                    provider.AddOrReplace(new DomainRetentionPolicy 
                    { 
                        Domain = $"domain-{i}", 
                        RetentionMode = RetentionMode.FullHistory 
                    });
                }
                else
                {
                    // Read
                    var policy = provider.GetPolicy($"domain-{i}");
                    policy.Should().NotBeNull();
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        // Assert
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RetentionMode.Default)]
    [InlineData(RetentionMode.FullHistory)]
    [InlineData(RetentionMode.ColdArchivable)]
    [InlineData(RetentionMode.HardDeletable)]
    public void RetentionMode_AllModes_ShouldBeSupported(RetentionMode mode)
    {
        // Arrange
        var policy = new DomainRetentionPolicy 
        { 
            Domain = "test", 
            RetentionMode = mode 
        };
        
        var provider = new DefaultDomainRetentionPolicyProvider(new[] { policy });

        // Act
        var result = provider.GetPolicy("test");

        // Assert
        result.RetentionMode.Should().Be(mode);
    }

    [Fact]
    public void PolicyProvider_WithMultipleDomains_ShouldIsolatePolicies()
    {
        // Arrange
        var policies = new[]
        {
            new DomainRetentionPolicy { Domain = "domain1", RetentionMode = RetentionMode.Default },
            new DomainRetentionPolicy { Domain = "domain2", RetentionMode = RetentionMode.FullHistory },
            new DomainRetentionPolicy { Domain = "domain3", RetentionMode = RetentionMode.ColdArchivable },
            new DomainRetentionPolicy { Domain = "domain4", RetentionMode = RetentionMode.HardDeletable }
        };
        
        var provider = new DefaultDomainRetentionPolicyProvider(policies);

        // Act & Assert
        provider.GetPolicy("domain1").RetentionMode.Should().Be(RetentionMode.Default);
        provider.GetPolicy("domain2").RetentionMode.Should().Be(RetentionMode.FullHistory);
        provider.GetPolicy("domain3").RetentionMode.Should().Be(RetentionMode.ColdArchivable);
        provider.GetPolicy("domain4").RetentionMode.Should().Be(RetentionMode.HardDeletable);
    }

    [Fact]
    public void PolicyProvider_AddOrReplace_ShouldBeThreadSafe()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();
        var exceptions = new ConcurrentBag<Exception>();

        // Act - Multiple threads adding/updating policies concurrently
        Parallel.For(0, 50, i =>
        {
            try
            {
                var policy = new DomainRetentionPolicy
                {
                    Domain = $"domain-{i % 10}", // Reuse domain names to test concurrent updates
                    RetentionMode = (RetentionMode)(i % 4)
                };
                provider.AddOrReplace(policy);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert
        exceptions.Should().BeEmpty();
    }

    [Fact]
    public void PolicyProvider_GetPolicy_AfterDynamicUpdate_ShouldReturnNewPolicy()
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();
        
        // Initial state - should return default
        var initialPolicy = provider.GetPolicy("orders");
        initialPolicy.RetentionMode.Should().Be(RetentionMode.ColdArchivable);

        // Act - Add policy dynamically
        provider.AddOrReplace(new DomainRetentionPolicy 
        { 
            Domain = "orders", 
            RetentionMode = RetentionMode.FullHistory 
        });

        // Assert - Should now return updated policy
        var updatedPolicy = provider.GetPolicy("orders");
        updatedPolicy.RetentionMode.Should().Be(RetentionMode.FullHistory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PolicyProvider_AddOrReplace_WithWhitespaceDomain_ShouldThrowArgumentException(string domain)
    {
        // Arrange
        var provider = new DefaultDomainRetentionPolicyProvider();
        var policy = new DomainRetentionPolicy 
        { 
            Domain = domain, 
            RetentionMode = RetentionMode.Default 
        };

        // Act
        var act = () => provider.AddOrReplace(policy);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}

