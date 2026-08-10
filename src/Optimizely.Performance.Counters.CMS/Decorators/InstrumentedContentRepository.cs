using System;
using System.Collections.Generic;
using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CmsContent;

namespace Optimizely.Performance.Counters.CMS.Decorators
{
    /// <summary>
    /// Decorator for IContentRepository that instruments content write operations.
    /// Tracks: save, publish, delete and move duration and rate.
    /// <para>
    /// IContentRepository extends IContentLoader, so this type derives from
    /// <see cref="InstrumentedContentLoader"/> and inherits all 23 read members already
    /// instrumented there. Only the repository members are declared below.
    /// </para>
    /// <para>
    /// CMS 13 promoted a number of members that were extension methods in CMS 11 and 12 onto
    /// the interface itself. Those live in the CMS13 region.
    /// </para>
    /// </summary>
    public class InstrumentedContentRepository : InstrumentedContentLoader, IContentRepository
    {
        private readonly IContentRepository _inner;

        /// <summary>
        /// Wraps the repository resolved by the container.
        /// </summary>
        /// <param name="inner">The implementation being decorated.</param>
        /// <param name="metricTracker">Sink for the emitted counters.</param>
        /// <param name="logger">Used only to report instrumentation failures; never to fail the caller.</param>
        public InstrumentedContentRepository(
            IContentRepository inner,
            IMetricTracker metricTracker,
            ILogger<InstrumentedContentRepository> logger)
            : base(inner, metricTracker, logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        #region Read members specific to IContentRepository (CMS 11, 12 and 13)

        /// <inheritdoc />
        public IEnumerable<T> GetLanguageBranches<T>(ContentReference contentLink) where T : IContentData =>
            _inner.GetLanguageBranches<T>(contentLink);

        /// <inheritdoc />
        public T GetDefault<T>(ContentReference parentLink) where T : IContentData =>
            _inner.GetDefault<T>(parentLink);

        /// <inheritdoc />
        public T GetDefault<T>(ContentReference parentLink, CultureInfo language) where T : IContentData =>
            _inner.GetDefault<T>(parentLink, language);

        /// <inheritdoc />
        public T GetDefault<T>(ContentReference parentLink, int contentTypeID) where T : IContentData =>
            _inner.GetDefault<T>(parentLink, contentTypeID);

        /// <inheritdoc />
        public T GetDefault<T>(ContentReference parentLink, int contentTypeID, CultureInfo language) where T : IContentData =>
            _inner.GetDefault<T>(parentLink, contentTypeID, language);

        /// <inheritdoc />
        public IEnumerable<ReferenceInformation> GetReferencesToContent(ContentReference contentLink, bool includeDescendents) =>
            _inner.GetReferencesToContent(contentLink, includeDescendents);

        /// <inheritdoc />
        public IEnumerable<IContent> ListDelayedPublish() => _inner.ListDelayedPublish();

        #endregion

        #region Write members (CMS 11, 12 and 13)

        /// <inheritdoc />
        public T CreateLanguageBranch<T>(ContentReference contentLink, CultureInfo language) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.CreateLanguageBranch<T>(contentLink, language);
                TrackWrite(Names.Save,"CreateLanguageBranch", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Save,"CreateLanguageBranch", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Copy(ContentReference source, ContentReference destination,
            AccessLevel requiredSourceAccess, AccessLevel requiredDestinationAccess, bool publishOnDestination)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Copy(source, destination, requiredSourceAccess, requiredDestinationAccess, publishOnDestination);
                TrackWrite(Names.Move,"Copy", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Move,"Copy", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void Delete(ContentReference contentLink, bool forceDelete, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.Delete(contentLink, forceDelete, access);
                TrackWrite(Names.Delete,"Delete", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Delete,"Delete", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void DeleteChildren(ContentReference contentLink, bool forceDelete, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.DeleteChildren(contentLink, forceDelete, access);
                TrackWrite(Names.Delete,"DeleteChildren", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Delete,"DeleteChildren", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void DeleteLanguageBranch(ContentReference contentLink, string languageBranch, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.DeleteLanguageBranch(contentLink, languageBranch, access);
                TrackWrite(Names.Delete,"DeleteLanguageBranch", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Delete,"DeleteLanguageBranch", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Move(ContentReference contentLink, ContentReference destination,
            AccessLevel requiredSourceAccess, AccessLevel requiredDestinationAccess)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Move(contentLink, destination, requiredSourceAccess, requiredDestinationAccess);
                TrackWrite(Names.Move,"Move", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Move,"Move", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Mirrors the IContentRepository.MoveToWastebasket(ContentReference, string) member, which V13 marks obsolete.")]
        public void MoveToWastebasket(ContentReference contentLink, string deletedBy)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.MoveToWastebasket(contentLink, deletedBy);
                TrackWrite(Names.Delete,"MoveToWastebasket", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Delete,"MoveToWastebasket", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Save(IContent content, SaveAction action, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            // A publish is a Save carrying the Publish action, not a distinct call. Splitting the
            // counter here is what makes PublishTimeMs meaningful on CMS 11 and 12, where the
            // interface has no separate Publish member.
            var counter = IsPublish(action) ? Names.Publish : Names.Save;
            try
            {
                var result = _inner.Save(content, action, access);
                TrackWrite(counter, "Save", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(counter, "Save", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion

#if CMS13
        #region CMS 13 only - members promoted from extension methods onto the interface

        /// <inheritdoc />
        public T CreateLanguageBranch<T>(ContentReference contentLink, ILanguageSelector languageSelector) where T : IContentData
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.CreateLanguageBranch<T>(contentLink, languageSelector);
                TrackWrite(Names.Save,"CreateLanguageBranch_Selector", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Save,"CreateLanguageBranch_Selector", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Copy(ContentReference source, ContentReference destination)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Copy(source, destination);
                TrackWrite(Names.Move,"Copy", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Move,"Copy", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Copy(ContentReference source, ContentReference destination, CopyContentOptions options)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Copy(source, destination, options);
                TrackWrite(Names.Move,"Copy_Options", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Move,"Copy_Options", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void Delete(ContentReference contentLink, bool forceDelete)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.Delete(contentLink, forceDelete);
                TrackWrite(Names.Delete,"Delete", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Delete,"Delete", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Move(ContentReference contentLink, ContentReference destination)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Move(contentLink, destination);
                TrackWrite(Names.Move,"Move", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Move,"Move", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void MoveToWastebasket(ContentReference contentLink)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.MoveToWastebasket(contentLink);
                TrackWrite(Names.Delete,"MoveToWastebasket", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Delete,"MoveToWastebasket", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void MoveToWastebasket(ContentReference contentLink, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.MoveToWastebasket(contentLink, access);
                TrackWrite(Names.Delete,"MoveToWastebasket_Access", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Delete,"MoveToWastebasket_Access", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Save(IContent content)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Save(content);
                TrackWrite(Names.Save,"Save", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Save,"Save", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Save(IContent content, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Save(content, access);
                TrackWrite(Names.Save,"Save_Access", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Save,"Save_Access", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Save(IContent content, SaveAction action)
        {
            var timer = OperationTimer.Start();
            var counter = IsPublish(action) ? Names.Publish : Names.Save;
            try
            {
                var result = _inner.Save(content, action);
                TrackWrite(counter, "Save_Action", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(counter, "Save_Action", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Publish(IContent content)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Publish(content);
                TrackWrite(Names.Publish,"Publish", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Publish,"Publish", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public ContentReference Publish(IContent content, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Publish(content, access);
                TrackWrite(Names.Publish,"Publish_Access", timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackWrite(Names.Publish,"Publish_Access", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void Publish(ContentReference contentLink, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.Publish(contentLink, access);
                TrackWrite(Names.Publish,"Publish_Link", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Publish,"Publish_Link", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void Publish(ContentReference contentLink, DateTime? delayPublishUntil, AccessLevel access)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.Publish(contentLink, delayPublishUntil, access);
                TrackWrite(Names.Publish,"Publish_Delayed", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Publish,"Publish_Delayed", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<CultureInfo> ListLanguageBranches() => _inner.ListLanguageBranches();

        /// <inheritdoc />
        public void ConvertLanguageBranch(ContentReference contentLink, CultureInfo currentLanguage,
            CultureInfo newLanguage, ConvertLanguageBranchOptions options)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.ConvertLanguageBranch(contentLink, currentLanguage, newLanguage, options);
                TrackWrite(Names.Save,"ConvertLanguageBranch_Options", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Save,"ConvertLanguageBranch_Options", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void ConvertLanguageBranch(ContentReference contentLink, CultureInfo currentLanguage, CultureInfo newLanguage)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.ConvertLanguageBranch(contentLink, currentLanguage, newLanguage);
                TrackWrite(Names.Save,"ConvertLanguageBranch", timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackWrite(Names.Save,"ConvertLanguageBranch", timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion
#endif

        #region Instrumentation helpers

        /// <summary>
        /// True when the save action publishes. SaveAction is a flags enum and callers routinely
        /// combine Publish with modifiers such as ForceCurrentVersion, so this masks rather than
        /// comparing for equality.
        /// </summary>
        private static bool IsPublish(SaveAction action) => (action & SaveAction.Publish) == SaveAction.Publish;

        /// <summary>
        /// Emits the duration and rate counters for a write.
        /// </summary>
        /// <param name="counter">
        /// Counter family - Save, Publish, Delete or Move, taken from
        /// <see cref="CounterNames.CmsContent"/>.
        /// </param>
        /// <param name="operationName">The specific member, used as the Operation dimension.</param>
        /// <param name="durationMs">Measured duration.</param>
        /// <param name="success">Whether the inner call returned without throwing.</param>
        private void TrackWrite(CounterPair counter, string operationName, double durationMs, bool success)
        {
            try
            {
                MetricTracker.TrackMetric(
                    counter.TimeMs,
                    durationMs,
                    "Operation", operationName);

                MetricTracker.TrackMetric(
                    counter.Operations,
                    1,
                    "Operation", operationName,
                    "Success", success ? "True" : "False");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to track {Counter} for operation {Operation}", counter.Operations, operationName);
            }
        }

        #endregion
    }
}
