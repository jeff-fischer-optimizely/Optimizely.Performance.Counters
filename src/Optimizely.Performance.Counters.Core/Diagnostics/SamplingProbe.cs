using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Base for probes that sample a runtime condition on their own thread.
    /// <para>
    /// Distinct from <see cref="Telemetry.PeriodicMetricReporter"/>, which flushes counts a
    /// decorator accumulated on the hot path. Nothing calls a probe; it goes and looks. That
    /// difference is why this uses a dedicated thread rather than a timer: timer callbacks are
    /// dispatched on the thread pool, so a timer-driven probe stops running under exactly the
    /// conditions it exists to detect, and reports numbers biased towards health at the moment
    /// the site is least healthy.
    /// </para>
    /// <para>
    /// The lifecycle rules are the fiddly part, and they are here rather than in each probe
    /// because getting one of them wrong is not a missing metric. An unhandled exception on a
    /// thread created by hand terminates the process on .NET Core, and an initialization module
    /// that throws is a site that will not start.
    /// </para>
    /// </summary>
    public abstract class SamplingProbe : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly string _threadName;
        private readonly bool _enabled;
        private readonly TimeSpan _sampleInterval;
        private readonly int _logsPerMinute;

        // Deliberately never disposed. The sampler waits on this from its own thread, so any
        // disposal would race that wait, and touching a disposed wait handle throws - on a thread
        // where that ends the process. One event held for the life of a process-wide probe is the
        // cheaper trade. An earlier revision used a CancellationTokenSource and did dispose it,
        // and a restart after disposal took the test host down with it.
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);

        private Thread? _thread;
        private int _started;
        private int _disposed;

        private long _logWindowStartTicks;
        private int _logsInWindow;

        /// <summary>
        /// Initializes the probe. Nothing runs until <see cref="Start"/> is called.
        /// </summary>
        /// <param name="logger">Log sink. May be null.</param>
        /// <param name="threadName">Name given to the sampling thread, for debuggers and dumps.</param>
        /// <param name="enabled">Whether the probe may run at all.</param>
        /// <param name="sampleInterval">Time between samples.</param>
        /// <param name="logsPerMinute">Cap on log entries per minute. Zero silences the probe.</param>
        protected SamplingProbe(
            ILogger? logger,
            string threadName,
            bool enabled,
            TimeSpan sampleInterval,
            int logsPerMinute)
        {
            _logger = logger;
            _threadName = threadName;
            _enabled = enabled;

            // Clamped here rather than trusted. A zero or negative interval spins the sampling
            // thread at full speed against whatever it is measuring, which turns a probe into the
            // load it is supposed to be reporting on.
            _sampleInterval = sampleInterval < TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : sampleInterval;

            _logsPerMinute = logsPerMinute;
            _logWindowStartTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Whether the probe is currently sampling.
        /// </summary>
        public bool IsRunning => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _disposed) == 0;

        /// <summary>
        /// Starts sampling. Does nothing if the probe is disabled, already started, disposed, or
        /// if <see cref="OnStarting"/> declines.
        /// </summary>
        public void Start()
        {
            // Disposal is part of the guard, not just double-start. Optimizely runs an
            // initialization module again across an app domain recycle, and a start after
            // disposal must not raise a thread that then touches torn-down state.
            if (!_enabled
                || Volatile.Read(ref _disposed) != 0
                || Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            bool proceed;

            try
            {
                proceed = OnStarting();
            }
            catch (Exception ex)
            {
                // OnStarting is where probes reflect, seed baselines and attach listeners - the
                // most likely place for one to fail, and the least acceptable place for it to
                // take the site's startup with it.
                _logger?.LogWarning(ex, "{Probe} could not start and will not report.", _threadName);
                return;
            }

            if (!proceed)
            {
                return;
            }

            _thread = new Thread(Loop)
            {
                // Background so it can never hold up process shutdown, and below normal so that on
                // a saturated machine the probe yields to the work it is measuring.
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = _threadName
            };

            _thread.Start();
        }

        /// <summary>
        /// Stops sampling. Safe to call more than once, and before <see cref="Start"/>.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stop.Set();

            try
            {
                OnStopping();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "{Probe} did not shut down cleanly.", _threadName);
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Runs once before the sampling thread starts. Return false to decline to run.
        /// </summary>
        /// <remarks>
        /// Where a probe resolves what it is going to sample, seeds a baseline for a delta, or
        /// decides it cannot work on this runtime. Declining is an ordinary outcome and is not
        /// logged as a fault by the base.
        /// </remarks>
        protected virtual bool OnStarting() => true;

        /// <summary>
        /// Runs on disposal, before the sampling thread has necessarily noticed.
        /// </summary>
        protected virtual void OnStopping()
        {
        }

        /// <summary>
        /// Takes one sample. Called on the probe's own thread.
        /// </summary>
        /// <remarks>
        /// May throw; the loop catches and carries on. A probe that dies at the first hiccup
        /// leaves a silent gap that looks exactly like a healthy site.
        /// </remarks>
        protected abstract void Sample();

        /// <summary>
        /// Blocks for the given time, returning true if the probe was stopped while waiting.
        /// </summary>
        /// <remarks>
        /// For probes that need to hold a capture window open. Waiting on the stop signal rather
        /// than sleeping means disposal is not delayed by the length of the window.
        /// </remarks>
        /// <param name="duration">How long to wait.</param>
        protected bool WaitOrStop(TimeSpan duration) => _stop.Wait(duration);

        /// <summary>
        /// Writes a log entry, subject to the per-minute cap.
        /// </summary>
        /// <remarks>
        /// The cap exists because everything a probe reports is by definition something going
        /// wrong, and the conditions that trip one persist for minutes. Uncapped, the probe floods
        /// the log at exactly the moment the site can least afford the I/O.
        /// </remarks>
        /// <param name="write">Receives the logger. Not called when the cap is reached.</param>
        protected void TryLog(Action<ILogger> write)
        {
            if (_logsPerMinute <= 0 || _logger == null)
            {
                return;
            }

            var now = DateTime.UtcNow.Ticks;
            var windowStart = Interlocked.Read(ref _logWindowStartTicks);

            if (now - windowStart >= TimeSpan.TicksPerMinute
                && Interlocked.CompareExchange(ref _logWindowStartTicks, now, windowStart) == windowStart)
            {
                Interlocked.Exchange(ref _logsInWindow, 0);
            }

            if (Interlocked.Increment(ref _logsInWindow) > _logsPerMinute)
            {
                return;
            }

            try
            {
                write(_logger);
            }
            catch
            {
                // Logging is the last thing standing; if it fails there is nowhere to say so.
            }
        }

        /// <remarks>
        /// The whole body is guarded. This runs on a thread created here rather than a pool
        /// thread, so anything escaping it is an unhandled exception, which on .NET Core takes the
        /// process with it. A performance counter has no business being able to do that.
        /// </remarks>
        private void Loop()
        {
            try
            {
                while (!_stop.IsSet)
                {
                    try
                    {
                        Sample();
                    }
                    catch (Exception ex)
                    {
                        TryLog(log => log.LogWarning(ex, "{Probe} failed to take a sample.", _threadName));
                    }

                    if (_stop.Wait(_sampleInterval))
                    {
                        return;
                    }
                }
            }
            catch
            {
                // Nothing left to do but stop quietly.
            }
        }
    }
}
