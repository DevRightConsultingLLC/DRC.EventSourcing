using System.Collections.Concurrent;

namespace DRC.EventSourcing
{
    /// <summary>
    /// Defines the retention mode for event streams, controlling archival and deletion behavior.
    /// </summary>
    /// <remarks>
    /// <para>Retention modes determine how events are managed over their lifecycle:</para>
    /// <list type="bullet">
    ///   <item><b>Default:</b> Keep all events in hot store indefinitely (no archival)</item>
    ///   <item><b>FullHistory:</b> Copy to cold archive but keep in hot store (backup)</item>
    ///   <item><b>ColdArchivable:</b> Move to cold archive and delete from hot store (typical for old events)</item>
    ///   <item><b>HardDeletable:</b> Permanently delete without archiving (for sensitive data removal)</item>
    /// </list>
    /// </remarks>
    public enum RetentionMode
    {
        /// <summary>
        /// Keep all events in the hot store forever. No archival is performed.
        /// </summary>
        /// <remarks>
        /// Use for streams that need immediate access to all historical events.
        /// Suitable for: audit logs, critical business streams, low-volume streams.
        /// </remarks>
        Default,
        
        /// <summary>
        /// Keep all events in the hot store forever AND copy to cold archive.
        /// </summary>
        /// <remarks>
        /// Provides disaster recovery and long-term backup while maintaining hot access.
        /// Suitable for: critical business data, compliance requirements, dual storage needs.
        /// </remarks>
        FullHistory,
        
        /// <summary>
        /// Archive events to cold storage and delete from hot store after cutoff version.
        /// </summary>
        /// <remarks>
        /// Balances storage costs with access patterns. Old events moved to cheaper cold storage.
        /// Suitable for: high-volume streams, aging data, cost optimization.
        /// Requires setting ArchiveCutoffVersion on the stream.
        /// </remarks>
        ColdArchivable,
        
        /// <summary>
        /// Permanently delete events without archiving after marking stream as deleted.
        /// </summary>
        /// <remarks>
        /// ⚠️ DESTRUCTIVE: Events are permanently lost. Use with extreme caution.
        /// Suitable for: GDPR right-to-deletion, sensitive data removal, test data cleanup.
        /// Requires setting IsDeleted flag on the stream.
        /// </remarks>
        HardDeletable
    }

    /// <summary>
    /// Represents the retention policy configuration for a specific domain.
    /// </summary>
    /// <remarks>
    /// Domain retention policies define how events for an entire domain should be managed.
    /// Policies are typically configured at application startup and remain static.
    /// </remarks>
    public sealed class DomainRetentionPolicy
    {
        /// <summary>
        /// Gets or initializes the domain name this policy applies to.
        /// </summary>
        /// <remarks>
        /// Domain names are case-insensitive. Examples: "orders", "inventory", "users"
        /// </remarks>
        public string Domain { get; init; } = default!;
        
        /// <summary>
        /// Gets or initializes the retention mode for this domain.
        /// </summary>
        public RetentionMode RetentionMode { get; init; }
    }

    /// <summary>
    /// Interface for providing domain-specific retention policies.
    /// </summary>
    /// <remarks>
    /// Implementations determine how events in each domain should be archived or deleted.
    /// Typically configured via dependency injection.
    /// </remarks>
    public interface IDomainRetentionPolicyProvider
    {
        /// <summary>
        /// Gets the retention policy for a specific domain.
        /// </summary>
        /// <param name="domain">The domain name to get the policy for</param>
        /// <returns>The retention policy, or a default policy if no specific policy exists</returns>
        DomainRetentionPolicy GetPolicy(string domain);
    }

    /// <summary>
    /// Thread-safe, in-memory provider of domain retention policies with configurable defaults.
    /// </summary>
    /// <remarks>
    /// <para>This implementation uses <see cref="ConcurrentDictionary{TKey,TValue}"/> for thread-safe policy management.</para>
    /// <para>Policies can be:</para>
    /// <list type="bullet">
    ///   <item>Pre-configured via constructor</item>
    ///   <item>Added/updated at runtime via <see cref="AddOrReplace"/></item>
    ///   <item>Retrieved by domain name (case-insensitive)</item>
    /// </list>
    /// <para>If no policy matches the requested domain, a default policy is returned (ColdArchivable).</para>
    /// 
    /// <para><b>Thread Safety:</b></para>
    /// <para>All operations are thread-safe and can be called concurrently from multiple threads.</para>
    /// 
    /// <para><b>Performance:</b></para>
    /// <para>Policy lookups are O(1) using hash-based dictionary. Suitable for hot-path usage.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var policies = new[]
    /// {
    ///     new DomainRetentionPolicy { Domain = "orders", RetentionMode = RetentionMode.ColdArchivable },
    ///     new DomainRetentionPolicy { Domain = "audit", RetentionMode = RetentionMode.FullHistory },
    ///     new DomainRetentionPolicy { Domain = "temp", RetentionMode = RetentionMode.HardDeletable }
    /// };
    /// 
    /// var provider = new DefaultDomainRetentionPolicyProvider(policies);
    /// 
    /// var orderPolicy = provider.GetPolicy("orders"); // ColdArchivable
    /// var unknownPolicy = provider.GetPolicy("unknown"); // Default (ColdArchivable)
    /// </code>
    /// </example>
    public sealed class DefaultDomainRetentionPolicyProvider : IDomainRetentionPolicyProvider
    {
        private readonly ConcurrentDictionary<string, DomainRetentionPolicy> _policies;
        
        private readonly DomainRetentionPolicy _default = new DomainRetentionPolicy
        {
            Domain = "<default>",
            RetentionMode = RetentionMode.ColdArchivable,
        };

        /// <summary>
        /// Initializes a new instance with no pre-configured policies (uses default for all domains).
        /// </summary>
        public DefaultDomainRetentionPolicyProvider()
        {
            _policies = new ConcurrentDictionary<string, DomainRetentionPolicy>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Initializes a new instance with the specified pre-configured policies.
        /// </summary>
        /// <param name="policies">Collection of domain policies to pre-populate</param>
        /// <remarks>
        /// Policies with null or whitespace domains are silently skipped.
        /// Domain names are treated as case-insensitive.
        /// </remarks>
        public DefaultDomainRetentionPolicyProvider(IEnumerable<DomainRetentionPolicy> policies)
        {
            _policies = new ConcurrentDictionary<string, DomainRetentionPolicy>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var p in policies)
            {
                if (string.IsNullOrWhiteSpace(p.Domain)) continue;
                _policies[p.Domain] = p;
            }
        }

        /// <summary>
        /// Gets the retention policy for the specified domain.
        /// </summary>
        /// <param name="domain">The domain name (case-insensitive)</param>
        /// <returns>The configured policy for the domain, or the default policy if not found</returns>
        /// <remarks>
        /// This method is thread-safe and optimized for high-frequency calls.
        /// Returns default policy (ColdArchivable) for null, empty, or unknown domains.
        /// </remarks>
        public DomainRetentionPolicy GetPolicy(string domain)
        {
            return string.IsNullOrWhiteSpace(domain) ? _default : _policies.GetValueOrDefault(domain, _default);
        }

        /// <summary>
        /// Adds a new policy or replaces an existing policy for a domain.
        /// </summary>
        /// <param name="policy">The policy to add or update</param>
        /// <exception cref="ArgumentNullException">Thrown if policy is null</exception>
        /// <exception cref="ArgumentException">Thrown if policy domain is null or whitespace</exception>
        /// <remarks>
        /// This method is thread-safe. If a policy already exists for the domain, it is atomically replaced.
        /// Domain names are case-insensitive.
        /// </remarks>
        public void AddOrReplace(DomainRetentionPolicy policy)
        {
            if (policy is null) 
                throw new ArgumentNullException(nameof(policy));
            
            if (string.IsNullOrWhiteSpace(policy.Domain)) 
                throw new ArgumentException("Domain is required", nameof(policy));
            
            _policies[policy.Domain] = policy;
        }
    }
}

