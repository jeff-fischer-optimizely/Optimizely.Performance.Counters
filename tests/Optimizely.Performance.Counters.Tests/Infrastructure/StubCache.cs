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

        /// <summary>
        /// Stands in for the memory cache Optimizely's own implementation removes entries from.
        /// Called once for the key itself and then <see cref="DependentsPerKey"/> more times, on the
        /// caller's thread, which is the shape of the real dependency cascade.
        /// </summary>
        /// <remarks>
        /// A delegate rather than an <c>IMemoryCache</c> so this type stays compilable on CMS 11,
        /// where there is no memory cache beneath the object cache at all.
        /// </remarks>
        public Action<string>? Cascade { get; set; }

        /// <summary>How many further entries each mutation discards. Zero unless set.</summary>
        public int DependentsPerKey { get; set; }

        public object Get(string key) => Stored.TryGetValue(key, out var value) ? value : null!;

        public void Insert(string key, object value, CacheEvictionPolicy evictionPolicy)
        {
            // Optimizely removes the existing entry before writing the new one, so an insert
            // cascades exactly as a removal does.
            CascadeFrom(key);
            Stored[key] = value;
        }

        public void Remove(string key)
        {
            CascadeFrom(key);
            Stored.Remove(key);
        }

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

        public void RemoveLocal(string key)
        {
            CascadeFrom(key);
            Stored.Remove(key);
        }

        public void RemoveRemote(string key)
        {
            CascadeFrom(key);
            Stored.Remove(key);
        }

        private void CascadeFrom(string key)
        {
            var cascade = Cascade;

            if (cascade == null)
            {
                return;
            }

            cascade(key);

            for (var dependent = 0; dependent < DependentsPerKey; dependent++)
            {
                cascade(key + ":dependent:" + dependent);
            }
        }
    }
}
