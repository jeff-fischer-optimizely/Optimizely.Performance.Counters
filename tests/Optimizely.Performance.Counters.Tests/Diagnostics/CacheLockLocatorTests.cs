using System;
using System.Threading;
using Optimizely.Performance.Counters.CMS.Diagnostics;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Diagnostics
{
    /// <summary>
    /// Tests for the only reflection in the CMS package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The locator reads a private static field on a type this package does not own, in an
    /// assembly that has already moved once between Optimizely versions. It is going to break
    /// eventually. What these tests establish is that when it does, it breaks by returning
    /// <c>false</c> with an explanation, and never by throwing into the caller's startup path.
    /// </para>
    /// <para>
    /// Each failure mode gets its own fixture type, because the point is not that the locator
    /// returns false in general - it is that it returns false for the specific shape changes
    /// Optimizely could plausibly make, and says which one happened.
    /// </para>
    /// </remarks>
    public class CacheLockLocatorTests
    {
        // ---------------------------------------------------------------------------------
        // Fixture types, each standing in for one way the cache could be shaped.
        // ---------------------------------------------------------------------------------

        /// <summary>Optimizely 12: the field is lower camel case.</summary>
        private class V12Shaped
        {
#pragma warning disable CS0169, IDE0044, CA1823
            private static readonly ReaderWriterLockSlim cacheLock = new ReaderWriterLockSlim();
#pragma warning restore CS0169, IDE0044, CA1823
        }

        /// <summary>Optimizely 13: the same field, renamed.</summary>
        private class V13Shaped
        {
            private static readonly ReaderWriterLockSlim CacheLock = new ReaderWriterLockSlim();
        }

        /// <summary>A future rename to something we have never heard of.</summary>
        private class UnknownFieldName
        {
            private static readonly ReaderWriterLockSlim SomethingElseEntirely = new ReaderWriterLockSlim();
        }

        /// <summary>The lock is gone, replaced by some other synchronisation.</summary>
        private class NoLockField
        {
            private static readonly object Gate = new object();
            private static readonly SemaphoreSlim Throttle = new SemaphoreSlim(1);
        }

        /// <summary>The lock became per-instance rather than per-process.</summary>
        private class InstanceLockOnly
        {
            private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();
        }

        /// <summary>The field exists but nothing has assigned it yet.</summary>
        private class NullLock
        {
            // The locator reads this by reflection, which the compiler cannot see. Left in place
            // rather than removed: a field that is never read is exactly the shape under test.
#pragma warning disable CS0414
            private static readonly ReaderWriterLockSlim? CacheLock = null;
#pragma warning restore CS0414
        }

        /// <summary>Two locks, neither with a name we recognise.</summary>
        private class TwoAnonymousLocks
        {
            private static readonly ReaderWriterLockSlim FirstGate = new ReaderWriterLockSlim();
            private static readonly ReaderWriterLockSlim SecondGate = new ReaderWriterLockSlim();
        }

        /// <summary>Two locks, one of which is the one we want.</summary>
        private class TwoLocksOneNamed
        {
            internal static readonly ReaderWriterLockSlim DependencyGate = new ReaderWriterLockSlim();
            internal static readonly ReaderWriterLockSlim CacheLock = new ReaderWriterLockSlim();
        }

        /// <summary>The field is declared as object, so its type no longer identifies it.</summary>
        private class LockBehindObjectField
        {
            private static readonly object CacheLock = new ReaderWriterLockSlim();
        }

        /// <summary>A subclass of the lock, which should still be recognised.</summary>
        private sealed class DerivedLock : ReaderWriterLockSlim { }

        private class DerivedLockField
        {
            private static readonly DerivedLock CacheLock = new DerivedLock();
        }

        /// <summary>Touching the type at all throws.</summary>
        private class ThrowingInitializer
        {
            // Never assigned on purpose - the static constructor below throws before it could be.
#pragma warning disable CS0649
            internal static readonly ReaderWriterLockSlim CacheLock;
#pragma warning restore CS0649

            // Explicit, so initialization happens precisely when the field is read rather than
            // at some earlier point of the runtime's choosing.
            static ThrowingInitializer()
            {
                throw new InvalidOperationException("type initializer failed on purpose");
            }
        }

        // ---------------------------------------------------------------------------------
        // The shapes that should work.
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(typeof(V12Shaped))]
        [InlineData(typeof(V13Shaped))]
        [InlineData(typeof(UnknownFieldName))]
        [InlineData(typeof(DerivedLockField))]
        public void FindsTheLockRegardlessOfWhatTheFieldIsCalled(Type cacheType)
        {
            var found = CacheLockLocator.TryGetLock(cacheType, out var cacheLock, out var diagnostic);

            Assert.True(found, diagnostic);
            Assert.NotNull(cacheLock);
        }

        [Fact]
        public void PrefersTheKnownFieldNameWhenSeveralLocksExist()
        {
            var found = CacheLockLocator.TryGetLock(
                typeof(TwoLocksOneNamed), out var cacheLock, out var diagnostic);

            Assert.True(found, diagnostic);

            // Not merely "a lock" - the right one. Picking the wrong field would produce a
            // metric that looks plausible and reports contention on something else entirely.
            Assert.Same(TwoLocksOneNamed.CacheLock, cacheLock);
        }

        [Fact]
        public void ReportsWhereItIsReadingFromOnSuccess()
        {
            CacheLockLocator.TryGetLock(typeof(V13Shaped), out _, out var diagnostic);

            // The success path has to name its source too, so an operator can tell a working
            // probe from one that silently found nothing.
            Assert.Contains(nameof(V13Shaped), diagnostic, StringComparison.Ordinal);
            Assert.Contains("CacheLock", diagnostic, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // The shapes that should degrade. None of these may throw.
        // ---------------------------------------------------------------------------------

        [Fact]
        public void DegradesWhenTheCacheTypeIsMissingEntirely()
        {
            var found = CacheLockLocator.TryGetLock(null, out var cacheLock, out var diagnostic);

            Assert.False(found);
            Assert.Null(cacheLock);

            // Naming the type it looked for is what makes the log entry actionable.
            Assert.Contains(CacheLockLocator.CacheTypeName, diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public void DegradesWhenTheLockIsReplacedBySomethingElse()
        {
            var found = CacheLockLocator.TryGetLock(typeof(NoLockField), out var cacheLock, out var diagnostic);

            Assert.False(found);
            Assert.Null(cacheLock);
            Assert.Contains("no static ReaderWriterLockSlim field", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public void DegradesWhenTheLockBecomesPerInstance()
        {
            // A static read of an instance field would throw, so this shape has to be rejected
            // during the search rather than at the point of reading it.
            var found = CacheLockLocator.TryGetLock(typeof(InstanceLockOnly), out var cacheLock, out _);

            Assert.False(found);
            Assert.Null(cacheLock);
        }

        [Fact]
        public void DegradesWhenTheFieldIsNull()
        {
            var found = CacheLockLocator.TryGetLock(typeof(NullLock), out var cacheLock, out var diagnostic);

            Assert.False(found);
            Assert.Null(cacheLock);
            Assert.Contains("is null", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public void DeclinesToGuessBetweenSeveralUnnamedLocks()
        {
            var found = CacheLockLocator.TryGetLock(
                typeof(TwoAnonymousLocks), out var cacheLock, out var diagnostic);

            // Reporting contention on an arbitrarily chosen lock would be worse than reporting
            // nothing, because nothing about the resulting metric would look wrong.
            Assert.False(found);
            Assert.Null(cacheLock);
            Assert.Contains("FirstGate", diagnostic, StringComparison.Ordinal);
            Assert.Contains("SecondGate", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public void DegradesWhenTheFieldIsDeclaredAsObject()
        {
            // Documents a deliberate limitation: matching is on the declared field type, so a
            // lock hidden behind an object field is not found. Widening the match would mean
            // inspecting the value of every static field on the type, which is a much larger
            // thing to do to somebody else's class.
            var found = CacheLockLocator.TryGetLock(
                typeof(LockBehindObjectField), out var cacheLock, out _);

            Assert.False(found);
            Assert.Null(cacheLock);
        }

        [Fact]
        public void DegradesWhenReadingTheFieldThrows()
        {
            var found = CacheLockLocator.TryGetLock(
                typeof(ThrowingInitializer), out var cacheLock, out var diagnostic);

            Assert.False(found);
            Assert.Null(cacheLock);

            // The underlying cause, not the TypeInitializationException wrapper, which says
            // nothing about what actually went wrong.
            Assert.Contains("type initializer failed on purpose", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public void AlwaysProducesADiagnosticMessage()
        {
            // Every path, success or failure, has to say something. A blank reason in a log
            // during an incident is indistinguishable from the probe not having run.
            Type?[] allShapes =
            {
                null, typeof(V12Shaped), typeof(V13Shaped), typeof(UnknownFieldName),
                typeof(NoLockField), typeof(InstanceLockOnly), typeof(NullLock),
                typeof(TwoAnonymousLocks), typeof(TwoLocksOneNamed),
                typeof(LockBehindObjectField), typeof(DerivedLockField), typeof(ThrowingInitializer)
            };

            foreach (var shape in allShapes)
            {
                CacheLockLocator.TryGetLock(shape, out _, out var diagnostic);
                Assert.False(string.IsNullOrWhiteSpace(diagnostic));
            }
        }

        [Fact]
        public void NeverThrowsForAnyShape()
        {
            // The guarantee the initialization module depends on, stated directly. Includes
            // types that have nothing to do with caching at all, since a future lookup could
            // resolve to the wrong type entirely.
            Type?[] hostileShapes =
            {
                null, typeof(object), typeof(string), typeof(int), typeof(Array),
                typeof(ThrowingInitializer), typeof(LockBehindObjectField),
                typeof(TwoAnonymousLocks), typeof(NullLock), typeof(InstanceLockOnly)
            };

            foreach (var shape in hostileShapes)
            {
                var exception = Record.Exception(
                    () => CacheLockLocator.TryGetLock(shape, out _, out _));

                Assert.Null(exception);
            }
        }

        // ---------------------------------------------------------------------------------
        // Against the real Optimizely assemblies, not a stand-in.
        // ---------------------------------------------------------------------------------

#if CMS11
        [Fact]
        public void DegradesOnV11WhereThereIsNoSuchCacheType()
        {
            // Not a gap in the port - CMS 11 has no MemoryObjectInstanceCache at all. Its
            // ISynchronizedObjectInstanceCache is backed by the System.Web runtime cache, which
            // has no process-wide ReaderWriterLockSlim for anything to queue on, so there is no
            // equivalent of this counter to be had. Scanning every EPiServer assembly shipped with
            // 11.11.1 for a static ReaderWriterLockSlim on a type named *ObjectInstanceCache
            // returns nothing.
            //
            // Asserted rather than skipped, because "the locator finds nothing on V11" is the
            // designed behaviour and the thing that would be a defect is it finding something.
            var found = CacheLockLocator.TryGetLock(
                CacheLockLocator.FindCacheType(), out var cacheLock, out var diagnostic);

            Assert.False(found);
            Assert.Null(cacheLock);
            Assert.Contains(CacheLockLocator.CacheTypeName, diagnostic, StringComparison.Ordinal);
        }
#else
        [Fact]
        public void FindsTheRealOptimizelyCacheType()
        {
            // The test project references the same Optimizely packages the library targets, so
            // this resolves the actual shipped type: EPiServer.Framework on net6.0 through
            // net9.0, and EPiServer.Cache on net10.0, where it was moved to a separate assembly.
            // Verifying this is the whole reason the test project multi-targets.
            var cacheType = CacheLockLocator.FindCacheType();

            Assert.NotNull(cacheType);
            Assert.Equal(CacheLockLocator.CacheTypeName, cacheType!.FullName);
        }

        [Fact]
        public void ReadsTheLockOutOfTheRealOptimizelyCacheType()
        {
            var found = CacheLockLocator.TryGetLock(
                CacheLockLocator.FindCacheType(), out var cacheLock, out var diagnostic);

            Assert.True(found, diagnostic);
            Assert.NotNull(cacheLock);

            // Reading the counters is what the probe does every sample, and it must not
            // acquire the lock while doing it.
            Assert.True(cacheLock!.WaitingWriteCount >= 0);
            Assert.True(cacheLock.WaitingReadCount >= 0);
            Assert.False(cacheLock.IsWriteLockHeld);
        }
#endif

        [Fact]
        public void FindingTheCacheTypeNeverThrows()
        {
            var exception = Record.Exception(() => CacheLockLocator.FindCacheType());

            Assert.Null(exception);
        }
    }
}
