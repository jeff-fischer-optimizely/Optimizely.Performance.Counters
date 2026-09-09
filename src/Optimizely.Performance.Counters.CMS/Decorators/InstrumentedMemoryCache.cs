#if !CMS11
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CmsCache;

namespace Optimizely.Performance.Counters.CMS.Decorators
{
    /// <summary>
    /// Wraps the application's <see cref="IMemoryCache"/> so that dependency cascades can be counted
    /// and eviction reasons observed. Every other operation is passed straight through untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// V12 and V13 only. CMS 11 caches through the <c>System.Web</c> runtime cache, so there is no
    /// <see cref="IMemoryCache"/> underneath its object cache to count at - the same difference that
    /// leaves the cache lock counters empty on that version.
    /// </para>
    /// <para>
    /// Two things are observed here, both invisible from the <c>IObjectInstanceCache</c> layer above:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <see cref="Remove"/> is where every entry in a dependency cascade ends up, on the thread that
    /// started the cascade, so counting calls gives the exact fan-out.
    /// </description></item>
    /// <item><description>
    /// Eviction reasons only ever reach a per-entry callback. Rather than adding a second callback to
    /// every entry, the one Optimizely already registers is substituted, so the reason codes cost no
    /// retained memory. See <see cref="InstrumentedCacheEntry"/>.
    /// </description></item>
    /// </list>
    /// <para>
    /// The application's memory cache is shared, so entries belonging to other components pass
    /// through here as well. They are left strictly alone: the callback substitution applies only to
    /// entries whose callback belongs to Optimizely's cache, and a removal outside a tracked cascade
    /// costs one thread-static read.
    /// </para>
    /// </remarks>
    public sealed class InstrumentedMemoryCache : IMemoryCache
    {
        private readonly IMemoryCache _inner;
        private readonly CacheCascadeRecorder _recorder;

        // One delegate for the lifetime of the process, reused by every instrumented entry, so
        // substituting a callback allocates only the registration that replaces it.
        private readonly PostEvictionDelegate _evictionCallback;

        /// <summary>
        /// Initializes a new instance of the <see cref="InstrumentedMemoryCache"/> class.
        /// </summary>
        /// <param name="inner">The memory cache being decorated.</param>
        /// <param name="recorder">Where cascades and evictions are reported.</param>
        public InstrumentedMemoryCache(IMemoryCache inner, CacheCascadeRecorder recorder)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _evictionCallback = OnEvicted;
        }

        /// <summary>
        /// Gets the callback this cache substitutes onto Optimizely's entries.
        /// </summary>
        /// <remarks>
        /// Public because it is the only way to reach the eviction path deliberately. The framework
        /// dispatches post-eviction callbacks on a <see cref="System.Threading.Tasks.Task"/> at a
        /// time of its own choosing, so a test that waited for one to arrive would be waiting on the
        /// runtime's eviction scan rather than on this code.
        /// </remarks>
        public PostEvictionDelegate EvictionCallback => _evictionCallback;

        /// <inheritdoc />
        public ICacheEntry CreateEntry(object key)
        {
            var entry = _inner.CreateEntry(key);
            return _recorder.IsDisabled ? entry : new InstrumentedCacheEntry(entry, this);
        }

        /// <inheritdoc />
        public void Remove(object key)
        {
            CacheRemovalScope.CountRemoval();
            _inner.Remove(key);
        }

        /// <inheritdoc />
        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        /// <inheritdoc />
        public void Dispose() => _inner.Dispose();

        /// <summary>
        /// Runs in place of Optimizely's own post-eviction callback, records the reason, then hands
        /// over to theirs.
        /// </summary>
        /// <remarks>
        /// Optimizely's callback is what prunes the dependency dictionary and cascades the eviction,
        /// so it is correctness-critical: if it stopped running, dependent entries would survive
        /// invalidations meant to kill them and the dependency dictionary would grow without bound.
        /// It is therefore invoked from a <c>finally</c>, and the work here happens first and cannot
        /// escape.
        /// </remarks>
        private void OnEvicted(object key, object? value, EvictionReason reason, object? state)
        {
            var original = state as PostEvictionCallbackRegistration;

            try
            {
                // Removals are already counted exactly, and synchronously, by CacheRemovalScope.
                // Recording them again here would double-count and would put a counter write on
                // every entry of every cascade.
                var counter = CounterForReason(reason);

                if (counter != null)
                {
                    _recorder.RecordEviction(counter);
                }
            }
            catch
            {
                // The recorder already swallows and counts its own failures. This is the backstop
                // that guarantees nothing reaches the line below.
            }
            finally
            {
                original?.EvictionCallback?.Invoke(key, value, reason, original.State);
            }
        }

        /// <summary>
        /// Maps an eviction reason to the counter that reports it, or null for reasons that are not
        /// reported.
        /// </summary>
        /// <remarks>
        /// A switch rather than <see cref="Enum.ToString()"/>, which allocates, and this runs once
        /// per evicted entry. <c>Removed</c> and <c>None</c> return null: the first is counted
        /// exactly by the cascade scope, and the second is what the runtime passes when nothing was
        /// actually evicted.
        /// </remarks>
        internal static string? CounterForReason(EvictionReason reason)
        {
            switch (reason)
            {
                case EvictionReason.Expired:
                    return Names.EvictionsExpired;
                case EvictionReason.Capacity:
                    return Names.EvictionsCapacity;
                case EvictionReason.Replaced:
                    return Names.EvictionsReplaced;
                case EvictionReason.TokenExpired:
                    return Names.EvictionsTokenExpired;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// A cache entry that swaps Optimizely's post-eviction callback for ours on the way in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework builds an entry by calling <c>IMemoryCache.CreateEntry</c>, copying the
    /// caller's options onto it - which appends each <see cref="PostEvictionCallbackRegistration"/>
    /// to <see cref="ICacheEntry.PostEvictionCallbacks"/> one at a time - and committing it on
    /// <see cref="Dispose"/>. Rewriting the list just before that commit is therefore enough to
    /// intercept eviction, and it replaces the existing registration rather than adding one, so the
    /// entry carries no extra retained state.
    /// </para>
    /// <para>
    /// Only entries carrying exactly the callback shape Optimizely's cache produces are touched.
    /// Anything else - a different component using the shared memory cache, or a future Optimizely
    /// release that registers callbacks differently - is committed exactly as it was built, and
    /// simply goes unmeasured.
    /// </para>
    /// </remarks>
    internal sealed class InstrumentedCacheEntry : ICacheEntry
    {
        private const string OptimizelyCacheTypeName =
            "EPiServer.Framework.Cache.Internal.MemoryObjectInstanceCache";

        // Cached after the first entry so the common path is a reference comparison rather than a
        // string comparison. Not synchronised: a race only costs a repeated string compare, and
        // every thread would store the same value.
        private static Type? _knownOwnerType;

        private readonly ICacheEntry _inner;
        private readonly InstrumentedMemoryCache _owner;

        internal InstrumentedCacheEntry(ICacheEntry inner, InstrumentedMemoryCache owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public object Key => _inner.Key;

        public object? Value
        {
            get => _inner.Value;
            set => _inner.Value = value;
        }

        public DateTimeOffset? AbsoluteExpiration
        {
            get => _inner.AbsoluteExpiration;
            set => _inner.AbsoluteExpiration = value;
        }

        public TimeSpan? AbsoluteExpirationRelativeToNow
        {
            get => _inner.AbsoluteExpirationRelativeToNow;
            set => _inner.AbsoluteExpirationRelativeToNow = value;
        }

        public TimeSpan? SlidingExpiration
        {
            get => _inner.SlidingExpiration;
            set => _inner.SlidingExpiration = value;
        }

        public IList<IChangeToken> ExpirationTokens => _inner.ExpirationTokens;

        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => _inner.PostEvictionCallbacks;

        public CacheItemPriority Priority
        {
            get => _inner.Priority;
            set => _inner.Priority = value;
        }

        public long? Size
        {
            get => _inner.Size;
            set => _inner.Size = value;
        }

        public void Dispose()
        {
            TrySubstituteCallback();
            _inner.Dispose();
        }

        private void TrySubstituteCallback()
        {
            try
            {
                var callbacks = _inner.PostEvictionCallbacks;

                // Optimizely registers exactly one callback per entry. Anything else is a shape we
                // do not recognise, and we leave it alone rather than guess.
                if (callbacks == null || callbacks.Count != 1)
                {
                    return;
                }

                var registration = callbacks[0];
                var target = registration?.EvictionCallback?.Target;
                if (target == null || !IsOptimizelyCache(target.GetType()))
                {
                    return;
                }

                callbacks[0] = new PostEvictionCallbackRegistration
                {
                    EvictionCallback = _owner.EvictionCallback,
                    State = registration,
                };
            }
            catch
            {
                // Instrumentation must never stop an entry being cached. Leaving the callbacks
                // untouched means this entry is simply not measured.
            }
        }

        private static bool IsOptimizelyCache(Type type)
        {
            if (ReferenceEquals(type, _knownOwnerType))
            {
                return true;
            }

            if (!string.Equals(type.FullName, OptimizelyCacheTypeName, StringComparison.Ordinal))
            {
                return false;
            }

            _knownOwnerType = type;
            return true;
        }
    }
}
#endif
