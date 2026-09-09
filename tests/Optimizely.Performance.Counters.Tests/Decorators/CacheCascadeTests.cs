#if !CMS11
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EPiServer.Framework.Cache;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.CMS.Diagnostics;
using Optimizely.Performance.Counters.Core.Configuration;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CmsCache;

namespace Optimizely.Performance.Counters.Tests.Decorators
{
    /// <summary>
    /// Covers the dependency cascade measurement, which spans two layers: the object cache decorator
    /// delimits and names an operation, and the memory cache decorator underneath it counts the
    /// entries the operation discarded. Neither can be checked on its own.
    /// </summary>
    /// <remarks>
    /// V12 and V13 only. CMS 11 caches through the <c>System.Web</c> runtime cache, so there is no
    /// <c>IMemoryCache</c> beneath the object cache to count at and nothing here has a subject.
    /// </remarks>
    public class CacheCascadeTests
    {
        // -----------------------------------------------------------------------------------
        // What a cascade costs
        // -----------------------------------------------------------------------------------

        [Fact]
        public void A_removal_reports_every_entry_the_cascade_discarded()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker, dependentsPerKey: 4);

            harness.Cache.Remove("root");

            // The caller asked for one key and the cache discarded five. That gap is the entire
            // point of the counter: it is invisible from the caller's side.
            Assert.Equal(5.0, ValueOf(tracker, Names.RemovalFanOut));
            Assert.Single(AllOf(tracker, Names.RemovalDurationMs));
        }

        public static TheoryData<string, string> RemovalPaths => new TheoryData<string, string>
        {
            { nameof(ISynchronizedObjectInstanceCache.Remove), Names.RemovalFanOut },
            { nameof(ISynchronizedObjectInstanceCache.RemoveLocal), Names.RemovalFanOut },
            { nameof(ISynchronizedObjectInstanceCache.RemoveRemote), Names.RemoteRemovalFanOut },
            { nameof(ISynchronizedObjectInstanceCache.Insert), Names.InsertFanOut },
        };

        [Theory]
        [MemberData(nameof(RemovalPaths))]
        public void Each_removal_path_reports_under_its_own_counter(string operation, string counter)
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker, dependentsPerKey: 2);

            Invoke(harness.Cache, operation);

            // A local invalidation, a replicated one and a write cost the same to the cache but mean
            // very different things, so they must not land in one series.
            Assert.Equal(3.0, ValueOf(tracker, counter));
        }

        [Fact]
        public void An_inserts_elapsed_time_is_not_reported_as_lock_time()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker, dependentsPerKey: 2);

            harness.Cache.Insert("root", new object(), Policy());

            // Most of an insert is the write, and the duration counter exists to say how long the
            // cache was closed to readers. Including writes would make it unreadable.
            Assert.Empty(AllOf(tracker, Names.RemovalDurationMs));
        }

        [Fact]
        public void An_insert_that_only_replaced_itself_reports_no_fan_out()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker, dependentsPerKey: 0);

            harness.Cache.Insert("root", new object(), Policy());

            // Every insert removes its predecessor, so a fan-out of one says nothing at all - and
            // saying it would put a counter write on the hottest path in the cache.
            Assert.Empty(AllOf(tracker, Names.InsertFanOut));
        }

        [Fact]
        public void An_insert_reports_the_lifetime_it_asked_for()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            harness.Cache.Insert("root", new object(), Policy(minutes: 5));

            Assert.Equal(300.0, ValueOf(tracker, Names.InsertTtlSeconds));
        }

        [Fact]
        public void A_nested_cascade_is_reported_once_by_the_operation_that_started_it()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker, dependentsPerKey: 1);

            var reentered = false;
            harness.Inner.Cascade = key =>
            {
                harness.InstrumentedMemory.Remove(key);

                if (reentered)
                {
                    return;
                }

                // What RemoveDependentItems does for real: it comes back through the cache API on
                // the same thread, part-way through the removal that called it.
                reentered = true;
                harness.Cache.RemoveLocal("dependent");
            };

            harness.Cache.Remove("root");

            // One reading, covering everything the caller's single Remove actually cost. Reporting
            // the inner removal separately would double-count it and understate the outer one.
            var reported = Assert.Single(AllOf(tracker, Names.RemovalFanOut));
            Assert.Equal(4.0, reported.Value);
        }

        [Fact]
        public void An_exception_from_the_cache_does_not_leave_the_cascade_count_open()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker, dependentsPerKey: 0);

            harness.Inner.Cascade = key =>
            {
                harness.InstrumentedMemory.Remove(key);
                throw new InvalidOperationException("The cache failed part-way through a removal.");
            };

            Assert.Throws<InvalidOperationException>(() => harness.Cache.Remove("first"));
            Assert.Empty(AllOf(tracker, Names.RemovalFanOut));

            harness.Inner.Cascade = key => harness.InstrumentedMemory.Remove(key);
            harness.Inner.DependentsPerKey = 2;
            harness.Cache.Remove("second");

            // Three, not four. The entry counted before the failure did not carry into the next
            // operation on this thread, which is what the finally in Measure is there for.
            Assert.Equal(3.0, ValueOf(tracker, Names.RemovalFanOut));
        }

        [Fact]
        public void Without_a_recorder_the_cache_reports_its_rates_and_nothing_else()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = new InstrumentedSynchronizedObjectInstanceCache(
                new StubCache(), tracker, NullLogger<InstrumentedSynchronizedObjectInstanceCache>.Instance);

            decorator.Remove("root");
            decorator.Insert("root", new object(), Policy());
            MetricFlush.Run(decorator);

            // The state of a site where the memory cache could not be decorated. Nothing is counting
            // entries, so every cascade would measure as zero - better to report none at all.
            Assert.DoesNotContain(Names.RemovalFanOut, tracker.Names);
            Assert.DoesNotContain(Names.InsertFanOut, tracker.Names);
            Assert.DoesNotContain(Names.InsertTtlSeconds, tracker.Names);
            Assert.Contains(Names.Operations, tracker.Names);
        }

        // -----------------------------------------------------------------------------------
        // Eviction reasons
        // -----------------------------------------------------------------------------------

        public static TheoryData<EvictionReason, string> ReportedReasons =>
            new TheoryData<EvictionReason, string>
            {
                { EvictionReason.Expired, Names.EvictionsExpired },
                { EvictionReason.Capacity, Names.EvictionsCapacity },
                { EvictionReason.Replaced, Names.EvictionsReplaced },
                { EvictionReason.TokenExpired, Names.EvictionsTokenExpired },
            };

        [Theory]
        [MemberData(nameof(ReportedReasons))]
        public void Each_reported_eviction_reason_has_a_counter_of_its_own(
            EvictionReason reason, string counter)
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            harness.InstrumentedMemory.EvictionCallback("key", value: null, reason, state: null);

            // EventCounters carry no dimensions, so the reason has to be in the name. A mapping to a
            // name the registry does not know would chart as permanently empty.
            Assert.Equal(1.0, ValueOf(tracker, counter));
        }

        [Theory]
        [InlineData(EvictionReason.Removed)]
        [InlineData(EvictionReason.None)]
        public void Reasons_that_are_counted_elsewhere_emit_nothing(EvictionReason reason)
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            harness.InstrumentedMemory.EvictionCallback("key", value: null, reason, state: null);

            // Removed is already counted exactly, and synchronously, by the cascade scope; None is
            // what the runtime passes when nothing was evicted.
            Assert.Empty(tracker.Metrics);
        }

        [Fact]
        public void The_callback_that_was_replaced_still_runs_with_its_own_state()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            object? seenState = null;
            var state = new object();
            var original = new PostEvictionCallbackRegistration
            {
                EvictionCallback = (key, value, reason, s) => seenState = s,
                State = state,
            };

            harness.InstrumentedMemory.EvictionCallback(
                "key", value: null, EvictionReason.Expired, original);

            // Optimizely's callback is what prunes the dependency dictionary. Dropping it would leak
            // memory and leave dependent entries alive through invalidations meant to kill them.
            Assert.Same(state, seenState);
        }

        [Fact]
        public void A_failing_recorder_does_not_stop_the_callback_it_replaced()
        {
            var ran = false;
            using var harness = new Harness(new FaultyMetricTracker());

            harness.InstrumentedMemory.EvictionCallback(
                "key",
                value: null,
                EvictionReason.Expired,
                new PostEvictionCallbackRegistration
                {
                    EvictionCallback = (key, value, reason, state) => ran = true,
                });

            Assert.True(ran);
        }

        // -----------------------------------------------------------------------------------
        // Callback substitution, which matches an Optimizely type by name
        // -----------------------------------------------------------------------------------

        [Fact]
        public void An_entry_belonging_to_Optimizelys_cache_has_its_callback_substituted()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            var original = new PostEvictionCallbackRegistration
            {
                EvictionCallback = CallbackOwnedByOptimizelysCache(),
                State = "their state",
            };

            var entry = CommitEntry(harness, original);

            // Substituted rather than appended, so an instrumented entry retains nothing more than
            // an uninstrumented one, and the original is carried as state so it can still be run.
            var substituted = Assert.Single(entry.PostEvictionCallbacks);
            Assert.Same(harness.InstrumentedMemory.EvictionCallback, substituted.EvictionCallback);
            Assert.Same(original, substituted.State);
        }

        [Fact]
        public void An_entry_belonging_to_something_else_is_left_exactly_as_it_was()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            // Captured so the delegate has a target at all - a lambda that captures nothing compiles
            // to a static method, which is the separate case below.
            var owner = new object();
            var original = new PostEvictionCallbackRegistration
            {
                EvictionCallback = (key, value, reason, state) => GC.KeepAlive(owner),
            };

            var entry = CommitEntry(harness, original);

            // The memory cache is shared with the rest of the application. Entries that are not
            // Optimizely's go unmeasured rather than being rewritten on a guess.
            Assert.Same(original, Assert.Single(entry.PostEvictionCallbacks));
        }

        [Fact]
        public void An_entry_whose_callback_has_no_target_is_left_alone()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            var original = new PostEvictionCallbackRegistration
            {
                EvictionCallback = StaticCallback,
            };

            var entry = CommitEntry(harness, original);

            Assert.Same(original, Assert.Single(entry.PostEvictionCallbacks));
        }

        [Fact]
        public void An_entry_carrying_more_than_one_callback_is_left_alone()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            var first = new PostEvictionCallbackRegistration
            {
                EvictionCallback = CallbackOwnedByOptimizelysCache(),
            };
            var second = new PostEvictionCallbackRegistration
            {
                EvictionCallback = StaticCallback,
            };

            var entry = CommitEntry(harness, first, second);

            // Optimizely registers exactly one. Two is a shape we do not recognise, and guessing
            // which to replace risks dropping somebody else's callback.
            Assert.Equal(
                new[] { first, second },
                entry.PostEvictionCallbacks.ToArray());
        }

        [Fact]
        public void An_entry_with_no_callbacks_is_committed_untouched()
        {
            var tracker = new RecordingMetricTracker();
            using var harness = new Harness(tracker);

            var entry = CommitEntry(harness);

            Assert.Empty(entry.PostEvictionCallbacks);
        }

        // -----------------------------------------------------------------------------------
        // Failing safe
        // -----------------------------------------------------------------------------------

        [Fact]
        public void The_recorder_switches_itself_off_after_repeated_failures()
        {
            var recorder = new CacheCascadeRecorder(
                new FaultyMetricTracker(),
                new CacheCascadeOptions { FailureThreshold = 3 },
                NullLogger<CacheCascadeRecorder>.Instance);

            Assert.False(recorder.IsDisabled);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                recorder.RecordEviction(Names.EvictionsExpired);
            }

            // Instrumentation that has started failing is usually failing during an incident. It
            // stops rather than burning CPU on an exception path at the worst possible moment.
            Assert.True(recorder.IsDisabled);
        }

        [Fact]
        public void A_success_in_between_keeps_the_recorder_alive()
        {
            var tracker = new FaultyMetricTracker();
            var recorder = new CacheCascadeRecorder(
                tracker,
                new CacheCascadeOptions { FailureThreshold = 3 },
                NullLogger<CacheCascadeRecorder>.Instance);

            recorder.RecordEviction(Names.EvictionsExpired);
            recorder.RecordEviction(Names.EvictionsExpired);

            tracker.Throw = false;
            recorder.RecordEviction(Names.EvictionsExpired);
            tracker.Throw = true;

            recorder.RecordEviction(Names.EvictionsExpired);
            recorder.RecordEviction(Names.EvictionsExpired);

            // Consecutive, not cumulative. An occasional failure over a long-running process should
            // not eventually silence a sink that mostly works.
            Assert.False(recorder.IsDisabled);
        }

        [Fact]
        public void A_disabled_recorder_leaves_the_cache_working_and_stops_measuring()
        {
            var tracker = new FaultyMetricTracker();
            using var harness = new Harness(
                tracker,
                dependentsPerKey: 2,
                new CacheCascadeOptions { FailureThreshold = 1 });

            harness.Cache.Remove("first");
            Assert.True(harness.Recorder.IsDisabled);

            tracker.Throw = false;
            harness.Inner.Stored["second"] = new object();
            harness.Cache.Remove("second");

            // The cache still does its job, and no cascade counter reappears once the recorder has
            // given up - a series that stopped is easier to read than one that comes and goes.
            Assert.False(harness.Inner.Stored.ContainsKey("second"));
            Assert.DoesNotContain(Names.RemovalFanOut, tracker.Names);
        }

        [Fact]
        public void The_recorder_ignores_a_lifetime_that_was_never_set()
        {
            var tracker = new RecordingMetricTracker();
            var recorder = new CacheCascadeRecorder(
                tracker, options: null, NullLogger<CacheCascadeRecorder>.Instance);

            recorder.RecordInsertTimeToLive(TimeSpan.Zero);
            recorder.RecordInsertTimeToLive(TimeSpan.FromSeconds(-1));

            // Entries cached without an expiry would otherwise drag the average lifetime to zero.
            Assert.Empty(tracker.Metrics);
        }

        [Fact]
        public void The_recorder_will_not_take_a_null_sink()
        {
            Assert.Throws<ArgumentNullException>(() => new CacheCascadeRecorder(metrics: null!));
        }

        [Fact]
        public void The_memory_cache_will_not_take_a_null_inner_cache_or_recorder()
        {
            var recorder = new CacheCascadeRecorder(new RecordingMetricTracker());

            Assert.Throws<ArgumentNullException>(
                () => new InstrumentedMemoryCache(inner: null!, recorder));
            Assert.Throws<ArgumentNullException>(
                () => new InstrumentedMemoryCache(new StubMemoryCache(), recorder: null!));
        }

        // -----------------------------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Both instrumented layers wired together over stubs, in the arrangement the module builds
        /// at runtime: object cache on top, memory cache underneath, one recorder between them.
        /// </summary>
        private sealed class Harness : IDisposable
        {
            internal Harness(
                IMetricTracker sink,
                int dependentsPerKey = 2,
                CacheCascadeOptions? options = null)
            {
                Inner = new StubCache { DependentsPerKey = dependentsPerKey };
                Memory = new StubMemoryCache();

                Recorder = new CacheCascadeRecorder(
                    sink, options, NullLogger<CacheCascadeRecorder>.Instance);

                InstrumentedMemory = new InstrumentedMemoryCache(Memory, Recorder);
                Inner.Cascade = key => InstrumentedMemory.Remove(key);

                Cache = new InstrumentedSynchronizedObjectInstanceCache(
                    Inner,
                    sink,
                    NullLogger<InstrumentedSynchronizedObjectInstanceCache>.Instance,
                    Recorder);
            }

            internal StubCache Inner { get; }

            internal StubMemoryCache Memory { get; }

            internal CacheCascadeRecorder Recorder { get; }

            internal InstrumentedMemoryCache InstrumentedMemory { get; }

            internal InstrumentedSynchronizedObjectInstanceCache Cache { get; }

            public void Dispose() => Cache.Dispose();
        }

        /// <summary>
        /// A sink that fails on demand, for the paths that have to survive a broken telemetry stack.
        /// </summary>
        private sealed class FaultyMetricTracker : IMetricTracker
        {
            private readonly List<string> _names = new List<string>();

            internal bool Throw { get; set; } = true;

            internal IReadOnlyList<string> Names => _names;

            public bool IsEnabled => true;

            public void TrackMetric(string name, double value) => Record(name);

            public void TrackMetric(string name, double value, string d1, string v1) => Record(name);

            public void TrackMetric(string name, double value, string d1, string v1, string d2, string v2) =>
                Record(name);

            public void TrackMetric(
                string name, double value, string d1, string v1, string d2, string v2, string d3, string v3) =>
                Record(name);

            private void Record(string name)
            {
                if (Throw)
                {
                    throw new InvalidOperationException("The metric sink is broken.");
                }

                _names.Add(name);
            }
        }

        private static CacheEvictionPolicy Policy(int minutes = 5) =>
            new CacheEvictionPolicy(TimeSpan.FromMinutes(minutes), CacheTimeoutType.Absolute);

        private static void Invoke(ISynchronizedObjectInstanceCache cache, string operation)
        {
            switch (operation)
            {
                case nameof(ISynchronizedObjectInstanceCache.Remove):
                    cache.Remove("root");
                    break;
                case nameof(ISynchronizedObjectInstanceCache.RemoveLocal):
                    cache.RemoveLocal("root");
                    break;
                case nameof(ISynchronizedObjectInstanceCache.RemoveRemote):
                    cache.RemoveRemote("root");
                    break;
                case nameof(ISynchronizedObjectInstanceCache.Insert):
                    cache.Insert("root", new object(), Policy());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(operation), operation, "No cache operation goes by that name.");
            }
        }

        /// <summary>
        /// Builds an entry through the instrumented cache and commits it, which is the point at which
        /// callbacks are rewritten. Returns the entry so the committed list can be inspected.
        /// </summary>
        private static ICacheEntry CommitEntry(
            Harness harness, params PostEvictionCallbackRegistration[] callbacks)
        {
            var entry = harness.InstrumentedMemory.CreateEntry("key");

            // Appended one at a time, the way MemoryCacheEntryOptions is copied onto an entry.
            foreach (var callback in callbacks)
            {
                entry.PostEvictionCallbacks.Add(callback);
            }

            entry.Value = new object();
            entry.Dispose();

            return entry;
        }

        /// <summary>
        /// A callback whose target really is Optimizely's memory cache, which is what the
        /// substitution matches on.
        /// </summary>
        /// <remarks>
        /// The cache is never constructed - its constructor wants the whole framework - and never
        /// called. It only has to exist and be of the right type, so an uninitialized instance is
        /// enough, and binding a static method to it as the first argument makes it the delegate's
        /// target without needing a method of the right shape on the type itself.
        /// </remarks>
        private static PostEvictionDelegate CallbackOwnedByOptimizelysCache()
        {
            var cacheType = CacheLockLocator.FindCacheType();
            Assert.True(
                cacheType != null,
                $"'{CacheLockLocator.CacheTypeName}' was not found, so the type this substitution " +
                "keys on no longer exists under that name.");

            var owner = RuntimeHelpers.GetUninitializedObject(cacheType!);
            var method = typeof(CacheCascadeTests).GetMethod(
                nameof(BoundCallback), BindingFlags.Static | BindingFlags.NonPublic)!;

            return (PostEvictionDelegate)Delegate.CreateDelegate(
                typeof(PostEvictionDelegate), owner, method);
        }

        private static void BoundCallback(
            object owner, object key, object? value, EvictionReason reason, object? state)
        {
        }

        private static void StaticCallback(object key, object? value, EvictionReason reason, object? state)
        {
        }

        private static IEnumerable<TrackedMetric> AllOf(RecordingMetricTracker tracker, string name) =>
            tracker.Metrics.Where(m => m.Name == name);

        private static double ValueOf(RecordingMetricTracker tracker, string name) =>
            Assert.Single(AllOf(tracker, name)).Value;
    }
}
#endif
