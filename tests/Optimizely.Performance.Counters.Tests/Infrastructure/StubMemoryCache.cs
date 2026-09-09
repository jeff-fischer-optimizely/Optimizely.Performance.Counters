#if !CMS11
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// A memory cache that actually stores things, so the layer beneath Optimizely's object cache
    /// can be driven.
    /// </summary>
    /// <remarks>
    /// A stub rather than the real <c>MemoryCache</c>, which would mean taking a package reference
    /// for the sake of a dictionary. Nothing under test depends on the real one's behaviour: the
    /// decorator counts calls to <see cref="Remove"/> and rewrites callbacks on the way in, and
    /// neither cares what the cache does with them afterwards.
    /// </remarks>
    public sealed class StubMemoryCache : IMemoryCache
    {
        private readonly Dictionary<object, object?> _stored = new Dictionary<object, object?>();

        /// <summary>Keys passed to <see cref="Remove"/>, in order.</summary>
        public List<object> Removed { get; } = new List<object>();

        public ICacheEntry CreateEntry(object key) => new StubCacheEntry(key, _stored);

        public void Remove(object key)
        {
            Removed.Add(key);
            _stored.Remove(key);
        }

        public bool TryGetValue(object key, out object? value) => _stored.TryGetValue(key, out value);

        public void Dispose()
        {
        }

        private sealed class StubCacheEntry : ICacheEntry
        {
            private readonly Dictionary<object, object?> _stored;

            internal StubCacheEntry(object key, Dictionary<object, object?> stored)
            {
                Key = key;
                _stored = stored;
            }

            public object Key { get; }

            public object? Value { get; set; }

            public DateTimeOffset? AbsoluteExpiration { get; set; }

            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }

            public TimeSpan? SlidingExpiration { get; set; }

            public IList<IChangeToken> ExpirationTokens { get; } = new List<IChangeToken>();

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } =
                new List<PostEvictionCallbackRegistration>();

            public CacheItemPriority Priority { get; set; }

            public long? Size { get; set; }

            // Committing on disposal is the contract the real cache follows, and the point at which
            // the decorator substitutes its callback.
            public void Dispose() => _stored[Key] = Value;
        }
    }
}
#endif
