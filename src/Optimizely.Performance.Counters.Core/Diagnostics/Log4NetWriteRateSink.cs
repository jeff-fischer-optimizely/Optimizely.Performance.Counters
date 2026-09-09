#if NET472
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// The counting appender itself, as a <see cref="DispatchProxy"/> over log4net's
    /// <c>IAppender</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public only because <see cref="DispatchProxy"/> requires it: the proxy it generates derives
    /// from this type into a dynamic assembly, which cannot subclass an internal one. Nothing here
    /// is part of the supported surface and no caller outside this assembly has any reason to
    /// name it.
    /// </para>
    /// <para>
    /// A proxy rather than a subclass of <c>AppenderSkeleton</c> because log4net is an optional
    /// dependency that this package must not reference. log4net's assembly version tracks its
    /// package version - 2.0.8 is <c>2.0.8.0</c> and 2.0.17 is <c>2.0.17.0</c> - so a compile-time
    /// reference would bind to one exact identity and leave every site on a different patch
    /// relying on a binding redirect to load this package at all. Reflection binds to whatever the
    /// site already loaded.
    /// </para>
    /// </remarks>
    public class Log4NetWriteRateAppenderProxy : DispatchProxy
    {
        // log4net reads this back when it dumps its configuration, and uses it for the
        // name-based RemoveAppender overload. Prefixed so it is obvious in a log4net diagnostic
        // dump which appender is ours.
        private string _name = "Optimizely.Performance.Counters.LogWriteRate";

        /// <summary>
        /// Called for each event log4net hands the appender. Set by
        /// <c>Log4NetWriteRateSink</c> immediately after the proxy is created.
        /// </summary>
        internal Action<object>? OnAppend { get; set; }

        /// <summary>
        /// Dispatches the four <c>IAppender</c> members. Everything but <c>DoAppend</c> is
        /// bookkeeping.
        /// </summary>
        /// <param name="targetMethod">The interface method log4net called.</param>
        /// <param name="args">Its arguments.</param>
        /// <returns>The return value, or null for the void members.</returns>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "DoAppend":
                    var loggingEvent = args != null && args.Length > 0 ? args[0] : null;

                    if (loggingEvent != null)
                    {
                        OnAppend?.Invoke(loggingEvent);
                    }

                    return null;

                case "get_Name":
                    return _name;

                case "set_Name":
                    if (args != null && args.Length > 0 && args[0] is string name)
                    {
                        _name = name;
                    }

                    return null;

                default:
                    // Close, and anything a future log4net adds to the interface. There is nothing
                    // to release: the appender holds a delegate and an integer accessor.
                    return null;
            }
        }
    }

    /// <summary>
    /// Attaches the log write rate counter to log4net, which is how a V11 site logs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hook is an appender on the <em>root</em> logger of every configured log4net repository.
    /// That is the one place every write passes through: log4net resolves the appenders for an
    /// event by walking from its logger up the parent chain, and it does that walk per event, so
    /// an appender added to root after the site started still sees everything that follows. The
    /// two configurations a CMS 11 site actually ships with - Optimizely's own
    /// <c>EPiServerLog.config</c>, and an inline <c>&lt;log4net&gt;</c> section in
    /// <c>web.config</c> - differ only in which repository ends up configured, and enumerating all
    /// of them covers both without having to know which one is in use.
    /// </para>
    /// <para>
    /// Two things this deliberately does not catch, both worth knowing before reading the counter.
    /// A logger configured with <c>additivity="false"</c> stops the walk before it reaches root,
    /// so its events are not counted - that is rare, and it is a decision the site made to keep a
    /// noisy subsystem out of the main log. And a repository created after initialization is not
    /// seen; in practice log4net repositories are created while the framework starts, well before
    /// any initialization module runs.
    /// </para>
    /// <para>
    /// Nothing here references log4net at compile time. See
    /// <see cref="Log4NetWriteRateAppenderProxy"/> for why that constraint is not negotiable.
    /// </para>
    /// </remarks>
    internal static class Log4NetWriteRateSink
    {
        // log4net's own level values, from log4net.Core.Level. Read rather than reconstructed:
        // the levels are ordered by an integer, custom levels slot in between the built-in ones,
        // and comparing the number is what log4net itself does. WARN is 60000 and ERROR is 70000,
        // so a site that defines its own level between them lands on the right side of both.
        private const int WarnValue = 60000;
        private const int ErrorValue = 70000;

        /// <summary>
        /// Adds the counting appender to every configured log4net repository.
        /// </summary>
        /// <param name="recorder">Where counted writes go.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>
        /// A handle that removes the appender again, or null if log4net was not found or nothing
        /// could be attached to. Null is an ordinary outcome, not a fault: a V11 site is free to
        /// log through something else.
        /// </returns>
        internal static IDisposable? TryAttach(LogWriteRateRecorder recorder, ILogger? logger)
        {
            // Deliberately not Assembly.Load. If log4net is not already loaded then the site is
            // not logging through it, and loading it here would create an empty repository and
            // attach a counter to nothing.
            var log4net = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, "log4net", StringComparison.OrdinalIgnoreCase));

            if (log4net == null)
            {
                logger?.LogInformation(
                    "log4net is not loaded, so log write rate counters were not attached. On CMS 11 " +
                    "that usually means the site logs through something other than " +
                    "EPiServer.Logging.Log4Net.");

                return null;
            }

            var logManagerType = log4net.GetType("log4net.LogManager", throwOnError: false);
            var appenderType = log4net.GetType("log4net.Appender.IAppender", throwOnError: false);
            var attachableType = log4net.GetType("log4net.Core.IAppenderAttachable", throwOnError: false);
            var loggingEventType = log4net.GetType("log4net.Core.LoggingEvent", throwOnError: false);

            if (logManagerType == null
                || appenderType == null
                || attachableType == null
                || loggingEventType == null)
            {
                logger?.LogWarning(
                    "A log4net assembly is loaded but does not have the shape this package expects, " +
                    "so log write rate counters were not attached. Version: {Version}.",
                    log4net.GetName().Version);

                return null;
            }

            var levelValue = BuildLevelAccessor(loggingEventType);
            var appender = CreateAppender(appenderType, recorder, levelValue);

            var addAppender = attachableType.GetMethod("AddAppender", new[] { appenderType });
            var removeAppender = attachableType.GetMethod("RemoveAppender", new[] { appenderType });

            if (addAppender == null || removeAppender == null)
            {
                return null;
            }

            var roots = RootLoggers(logManagerType, attachableType);
            var attached = new List<object>();

            foreach (var root in roots)
            {
                addAppender.Invoke(root, new[] { appender });
                attached.Add(root);
            }

            if (attached.Count == 0)
            {
                logger?.LogWarning(
                    "log4net is loaded but no configured repository exposed a root logger, so log " +
                    "write rate counters were not attached. The three Optimizely.Runtime.Logging " +
                    "counters will stay at zero.");

                return null;
            }

            logger?.LogInformation(
                "Log write rate counters attached to {Count} log4net repository root logger(s). " +
                "Writes below the level log4net is configured to accept are not counted, because " +
                "log4net discards them before any appender sees them.",
                attached.Count);

            return new Attachment(attached, removeAppender, appender);
        }

        /// <remarks>
        /// Only a <c>Hierarchy</c> has a root logger, and only its root implements
        /// <c>IAppenderAttachable</c>. Anything else is somebody's custom repository, which is
        /// skipped rather than reported: it is not a failure, it is a repository we have no
        /// documented way into.
        /// </remarks>
        private static IEnumerable<object> RootLoggers(Type logManagerType, Type attachableType)
        {
            var getAll = logManagerType.GetMethod("GetAllRepositories", Type.EmptyTypes);

            if (getAll?.Invoke(null, null) is not Array repositories)
            {
                yield break;
            }

            foreach (var repository in repositories)
            {
                if (repository == null)
                {
                    continue;
                }

                var root = repository.GetType()
                    .GetProperty("Root", BindingFlags.Public | BindingFlags.Instance)?
                    .GetValue(repository);

                if (root != null && attachableType.IsInstanceOfType(root))
                {
                    yield return root;
                }
            }
        }

        private static object CreateAppender(
            Type appenderType, LogWriteRateRecorder recorder, Func<object, int> levelValue)
        {
            var create = typeof(DispatchProxy)
                .GetMethod(nameof(DispatchProxy.Create), BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(appenderType, typeof(Log4NetWriteRateAppenderProxy));

            var appender = create.Invoke(null, null)!;

            ((Log4NetWriteRateAppenderProxy)appender).OnAppend = loggingEvent =>
            {
                LogWriteSeverity severity;

                try
                {
                    var value = levelValue(loggingEvent);

                    severity = value >= ErrorValue
                        ? LogWriteSeverity.Error
                        : value >= WarnValue
                            ? LogWriteSeverity.Warning
                            : LogWriteSeverity.Normal;
                }
                catch
                {
                    // An event with no level is malformed rather than impossible, and it is still
                    // a write. Counting it as ordinary is more honest than dropping it, and an
                    // exception escaping here would surface inside log4net's own append loop.
                    severity = LogWriteSeverity.Normal;
                }

                recorder.Record(severity);
            };

            return appender;
        }

        /// <remarks>
        /// A compiled accessor rather than two <c>PropertyInfo.GetValue</c> calls, because this
        /// runs on the site's own threads once per log write. Reflection per event would put the
        /// cost of measuring logging into the same order as the logging.
        /// </remarks>
        private static Func<object, int> BuildLevelAccessor(Type loggingEventType)
        {
            var levelProperty = loggingEventType.GetProperty("Level")
                ?? throw new MissingMemberException(loggingEventType.FullName, "Level");

            var valueProperty = levelProperty.PropertyType.GetProperty("Value")
                ?? throw new MissingMemberException(levelProperty.PropertyType.FullName, "Value");

            var parameter = Expression.Parameter(typeof(object), "loggingEvent");

            var body = Expression.Property(
                Expression.Property(Expression.Convert(parameter, loggingEventType), levelProperty),
                valueProperty);

            return Expression.Lambda<Func<object, int>>(body, parameter).Compile();
        }

        private sealed class Attachment : IDisposable
        {
            private readonly IReadOnlyList<object> _roots;
            private readonly MethodInfo _removeAppender;
            private readonly object _appender;

            internal Attachment(IReadOnlyList<object> roots, MethodInfo removeAppender, object appender)
            {
                _roots = roots;
                _removeAppender = removeAppender;
                _appender = appender;
            }

            public void Dispose()
            {
                foreach (var root in _roots)
                {
                    try
                    {
                        _removeAppender.Invoke(root, new[] { _appender });
                    }
                    catch
                    {
                        // Shutdown. A log4net repository that has already been shut down throws
                        // here, and there is nothing to do about it.
                    }
                }
            }
        }
    }
}
#endif
