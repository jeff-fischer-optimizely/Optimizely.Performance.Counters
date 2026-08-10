using System;
using System.Collections.Generic;
using System.Globalization;
using EPiServer;
using EPiServer.Core;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CmsContent;

namespace Optimizely.Performance.Counters.CMS.Decorators
{
    /// <summary>
    /// Decorator for IContentLoader that instruments content load operations.
    /// Tracks: operation duration, operation rate, and item counts for batch loads.
    /// <para>
    /// IContentLoader is identical across CMS 11, 12 and 13, so this type needs no
    /// version guards. All 23 interface members are instrumented.
    /// </para>
    /// </summary>
    public class InstrumentedContentLoader : IContentLoader
    {
        private readonly IContentLoader _inner;

        /// <summary>
        /// Sink for the emitted counters. Exposed to <see cref="InstrumentedContentRepository"/>,
        /// which inherits the 23 loader members from this type rather than restating them.
        /// </summary>
        protected IMetricTracker MetricTracker { get; }

        /// <summary>
        /// Used only to report instrumentation failures; never to fail the caller.
        /// </summary>
        protected ILogger Logger { get; }

        /// <summary>
        /// Wraps the loader resolved by the container.
        /// </summary>
        /// <param name="inner">The implementation being decorated.</param>
        /// <param name="metricTracker">Sink for the emitted counters.</param>
        /// <param name="logger">Used only to report instrumentation failures; never to fail the caller.</param>
        public InstrumentedContentLoader(
            IContentLoader inner,
            IMetricTracker metricTracker,
            ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            MetricTracker = metricTracker ?? throw new ArgumentNullException(nameof(metricTracker));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Get

        /// <inheritdoc />
        public T Get<T>(Guid contentGuid) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Get<T>(contentGuid);
                TrackLoad("Get_Guid", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackLoad("Get_Guid", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public T Get<T>(Guid contentGuid, LoaderOptions settings) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Get<T>(contentGuid, settings);
                TrackLoad("Get_Guid_Options", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackLoad("Get_Guid_Options", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public T Get<T>(Guid contentGuid, CultureInfo language) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Get<T>(contentGuid, language);
                TrackLoad("Get_Guid_Language", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackLoad("Get_Guid_Language", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public T Get<T>(ContentReference contentLink) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Get<T>(contentLink);
                TrackLoad("Get", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackLoad("Get", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public T Get<T>(ContentReference contentLink, CultureInfo language) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Get<T>(contentLink, language);
                TrackLoad("Get_Language", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackLoad("Get_Language", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public T Get<T>(ContentReference contentLink, LoaderOptions settings) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Get<T>(contentLink, settings);
                TrackLoad("Get_Options", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackLoad("Get_Options", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion

        #region GetChildren

        /// <inheritdoc />
        public IEnumerable<T> GetChildren<T>(ContentReference contentLink) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetChildren<T>(contentLink);
                TrackLoad("GetChildren", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetChildren", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<T> GetChildren<T>(ContentReference contentLink, CultureInfo language) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetChildren<T>(contentLink, language);
                TrackLoad("GetChildren_Language", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetChildren_Language", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<T> GetChildren<T>(ContentReference contentLink, LoaderOptions settings) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetChildren<T>(contentLink, settings);
                TrackLoad("GetChildren_Options", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetChildren_Options", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<T> GetChildren<T>(ContentReference contentLink, CultureInfo language, int startIndex, int maxRows)
            where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetChildren<T>(contentLink, language, startIndex, maxRows);
                TrackLoad("GetChildren_Language_Paged", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetChildren_Language_Paged", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<T> GetChildren<T>(ContentReference contentLink, LoaderOptions settings, int startIndex, int maxRows)
            where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetChildren<T>(contentLink, settings, startIndex, maxRows);
                TrackLoad("GetChildren_Options_Paged", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetChildren_Options_Paged", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion

        #region Traversal

        /// <inheritdoc />
        public IEnumerable<ContentReference> GetDescendents(ContentReference contentLink)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetDescendents(contentLink);
                TrackLoad("GetDescendents", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetDescendents", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<IContent> GetAncestors(ContentReference contentLink)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetAncestors(contentLink);
                TrackLoad("GetAncestors", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetAncestors", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion

        #region GetItems / GetBySegment

        /// <inheritdoc />
        public IEnumerable<IContent> GetItems(IEnumerable<ContentReference> contentLinks, CultureInfo language)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetItems(contentLinks, language);
                TrackLoad("GetItems_Language", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetItems_Language", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<IContent> GetItems(IEnumerable<ContentReference> contentLinks, LoaderOptions settings)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetItems(contentLinks, settings);
                TrackLoad("GetItems_Options", timer.ElapsedMilliseconds, success: true, CountIfMaterialized(result));
                return result;
            }
            catch
            {
                TrackLoad("GetItems_Options", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IContent GetBySegment(ContentReference parentLink, string urlSegment, CultureInfo language)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetBySegment(parentLink, urlSegment, language);
                TrackLoad("GetBySegment_Language", timer.ElapsedMilliseconds, success: result != null);
                // GetBySegment returns null for a segment that doesn't resolve, but the interface
                // is not annotated for that. Pass it straight through rather than changing the
                // contract callers already rely on.
                return result!;
            }
            catch
            {
                TrackLoad("GetBySegment_Language", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IContent GetBySegment(ContentReference parentLink, string urlSegment, LoaderOptions settings)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.GetBySegment(parentLink, urlSegment, settings);
                TrackLoad("GetBySegment_Options", timer.ElapsedMilliseconds, success: result != null);
                // See the language overload above: null is a legitimate "no match" result.
                return result!;
            }
            catch
            {
                TrackLoad("GetBySegment_Options", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion

        #region TryGet

        /// <inheritdoc />
        public bool TryGet<T>(ContentReference contentLink, out T content) where T : IContentData
        {
            var timer = OperationTimer.Start();
            var result = _inner.TryGet(contentLink, out content);
            TrackLoad("TryGet", timer.ElapsedMilliseconds, success: result);
            return result;
        }

        /// <inheritdoc />
        public bool TryGet<T>(ContentReference contentLink, CultureInfo language, out T content) where T : IContentData
        {
            var timer = OperationTimer.Start();
            var result = _inner.TryGet(contentLink, language, out content);
            TrackLoad("TryGet_Language", timer.ElapsedMilliseconds, success: result);
            return result;
        }

        /// <inheritdoc />
        public bool TryGet<T>(ContentReference contentLink, LoaderOptions settings, out T content) where T : IContentData
        {
            var timer = OperationTimer.Start();
            var result = _inner.TryGet(contentLink, settings, out content);
            TrackLoad("TryGet_Options", timer.ElapsedMilliseconds, success: result);
            return result;
        }

        /// <inheritdoc />
        public bool TryGet<T>(Guid contentGuid, out T content) where T : IContentData
        {
            var timer = OperationTimer.Start();
            var result = _inner.TryGet(contentGuid, out content);
            TrackLoad("TryGet_Guid", timer.ElapsedMilliseconds, success: result);
            return result;
        }

        /// <inheritdoc />
        public bool TryGet<T>(Guid contentGuid, CultureInfo language, out T content) where T : IContentData
        {
            var timer = OperationTimer.Start();
            var result = _inner.TryGet(contentGuid, language, out content);
            TrackLoad("TryGet_Guid_Language", timer.ElapsedMilliseconds, success: result);
            return result;
        }

        /// <inheritdoc />
        public bool TryGet<T>(Guid contentGuid, LoaderOptions loaderOptions, out T content) where T : IContentData
        {
            var timer = OperationTimer.Start();
            var result = _inner.TryGet(contentGuid, loaderOptions, out content);
            TrackLoad("TryGet_Guid_Options", timer.ElapsedMilliseconds, success: result);
            return result;
        }

        #endregion

        #region Instrumentation helpers

        /// <summary>
        /// Item count for a returned sequence, but only when it can be read for free.
        /// <para>
        /// Calling <c>Count()</c> or <c>ToList()</c> here would force enumeration the caller
        /// may not have asked for, and would allocate on a path that runs thousands of times a
        /// second. The CMS implementations return materialized collections, so the count is
        /// available via <see cref="ICollection{T}"/>; anything genuinely lazy is left uncounted
        /// rather than enumerated.
        /// </para>
        /// </summary>
        private static int? CountIfMaterialized<T>(IEnumerable<T>? sequence) =>
            sequence is ICollection<T> collection ? collection.Count : (int?)null;

        private void TrackLoad(string operationName, double durationMs, bool success, int? itemCount = null)
        {
            try
            {
                // Track operation duration
                MetricTracker.TrackMetric(
                    Names.Load.TimeMs,
                    durationMs,
                    "Operation", operationName);

                // Track operation rate
                MetricTracker.TrackMetric(
                    Names.Load.Operations,
                    1,
                    "Operation", operationName,
                    "Success", success ? "True" : "False");

                // Track item count for batch operations
                if (itemCount.HasValue)
                {
                    MetricTracker.TrackMetric(
                        Names.ItemsLoaded,
                        itemCount.Value,
                        "Operation", operationName);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to track content load metric for operation {Operation}", operationName);
            }
        }

        #endregion
    }
}
