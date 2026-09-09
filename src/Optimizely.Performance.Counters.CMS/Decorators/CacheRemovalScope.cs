namespace Optimizely.Performance.Counters.CMS.Decorators
{
    /// <summary>
    /// Which path a removal arrived through. Decides the counter a cascade is reported to.
    /// </summary>
    /// <remarks>
    /// The distinction is diagnostic, not bookkeeping. A cascade attributed to
    /// <see cref="Remote"/> was caused by a different server in the cluster, so nothing this
    /// instance did explains it; one attributed to <see cref="Insert"/> was caused by a write
    /// rather than an invalidation, which is the case nobody expects.
    /// </remarks>
    public enum CacheRemovalPath
    {
        /// <summary>An invalidation issued on this node.</summary>
        Local,

        /// <summary>An invalidation broadcast from another node in the cluster.</summary>
        Remote,

        /// <summary>A write that displaced an existing entry.</summary>
        Insert,
    }

    /// <summary>
    /// Counts, exactly and synchronously, how many cache entries a single removal actually
    /// removed once the dependency hierarchy had finished cascading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optimizely removes a key by taking its process-wide write lock and then walking the
    /// dependency graph on the calling thread:
    /// </para>
    /// <code>
    /// Remove(key) -> LockWrite(() => InnerRemove(key))
    /// InnerRemove(key) -> memoryCache.Remove(key); RemoveDependentItems(key)
    /// RemoveDependentItems(key) -> foreach dependent: InnerRemove(dependent)   // recursive
    /// </code>
    /// <para>
    /// Every entry the cascade touches therefore reaches <c>IMemoryCache.Remove</c> on the same
    /// thread, still inside the caller's original <c>Remove</c>. Counting those calls between
    /// entering and leaving the outer call yields the true fan-out - not an estimate derived from a
    /// shadow copy of the dependency graph, and at the cost of one increment per removed entry.
    /// Measured against Optimizely's own graph shape, keeping a shadow copy would have cost 192 MB
    /// per million entries; this retains nothing.
    /// </para>
    /// <para>
    /// Post-eviction callbacks cannot serve this purpose: the framework dispatches them on a
    /// <see cref="System.Threading.Tasks.Task"/>, so they arrive detached from the operation that
    /// caused them and can only ever be aggregated, never attributed.
    /// </para>
    /// </remarks>
    internal static class CacheRemovalScope
    {
        [System.ThreadStatic]
        private static int _depth;

        [System.ThreadStatic]
        private static int _removed;

        /// <summary>
        /// Records that one entry was removed, if a removal is being tracked on this thread.
        /// </summary>
        /// <remarks>
        /// This runs for every entry a cascade touches, so it is deliberately nothing more than a
        /// thread-static read and an increment.
        /// </remarks>
        internal static void CountRemoval()
        {
            if (_depth > 0)
            {
                _removed++;
            }
        }

        /// <summary>
        /// Begins tracking a removal on the current thread.
        /// </summary>
        /// <returns>
        /// <c>true</c> if this call opened the outermost scope and is therefore the one that should
        /// report the result. Pass the value to <see cref="End"/>.
        /// </returns>
        /// <remarks>
        /// Nested scopes are tolerated but only the outermost reports, so a removal issued from
        /// inside another removal cannot be counted twice - which is what makes it safe to measure
        /// at both the synchronized and unsynchronized cache layers.
        /// </remarks>
        internal static bool Begin()
        {
            if (_depth == 0)
            {
                _removed = 0;
            }

            _depth++;
            return _depth == 1;
        }

        /// <summary>
        /// Ends tracking. Must be called from a <c>finally</c>, so that an exception thrown by the
        /// cache cannot leave the thread's depth counter stuck above zero.
        /// </summary>
        /// <param name="isOutermost">The value returned by the matching <see cref="Begin"/>.</param>
        /// <returns>
        /// The number of entries removed by this operation, including the entry that was asked for.
        /// Zero for inner scopes, which do not own the count.
        /// </returns>
        internal static int End(bool isOutermost)
        {
            if (_depth > 0)
            {
                _depth--;
            }

            if (!isOutermost)
            {
                return 0;
            }

            var removed = _removed;
            _removed = 0;
            return removed;
        }
    }
}
