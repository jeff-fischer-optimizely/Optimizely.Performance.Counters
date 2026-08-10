using System;
using System.Collections.Generic;
using EPiServer.Framework.Cache;
#if CMS13
using Microsoft.Extensions.Logging;
#endif

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// A cache that actually stores things, so hit and miss counting can be exercised.
    /// <see cref="RecordingProxy"/> always returns null from Get, which only ever produces misses.
    /// </summary>
    public sealed class StubCache : ISynchronizedObjectInstanceCache
    {
        /// <summary>Entries the cache will return. Populate before reading.</summary>
        public Dictionary<string, object> Stored { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public object Get(string key) => Stored.TryGetValue(key, out var value) ? value : null!;

        public void Insert(string key, object value, CacheEvictionPolicy evictionPolicy) => Stored[key] = value;

        public void Remove(string key) => Stored.Remove(key);

#if CMS13
        public ILogger<IObjectInstanceCache> Logger { get; } = Microsoft.Extensions.Logging.Abstractions
            .NullLogger<IObjectInstanceCache>.Instance;
#else
        [Obsolete("Mirrors the obsolete IObjectInstanceCache.Clear member.")]
        public void Clear() => Stored.Clear();
#endif

        [Obsolete("Mirrors the obsolete ISynchronizedObjectInstanceCache.SynchronizationFailedStrategy member.")]
        public FailureRecoveryAction SynchronizationFailedStrategy { get; set; }

        [Obsolete("Mirrors the obsolete ISynchronizedObjectInstanceCache.ObjectInstanceCache member.")]
        public IObjectInstanceCache ObjectInstanceCache => this;

        public void RemoveLocal(string key) => Stored.Remove(key);

        public void RemoveRemote(string key) => Stored.Remove(key);
    }
}
