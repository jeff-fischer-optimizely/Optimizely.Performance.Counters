using System;
using System.Globalization;

namespace Optimizely.Performance.Counters.Core.Http
{
    /// <summary>
    /// What a response's own headers permit a cache to do with it, reduced to the five outcomes
    /// that are worth counting separately.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive by construction, so the five shares always sum to the responses that were
    /// classified. That is the point of the reduction: a set of overlapping flags cannot be charted
    /// as a distribution, and "what fraction of what this site sends can a client reuse" is the
    /// question being asked.
    /// </remarks>
    public enum ResponseCacheability
    {
        /// <summary>
        /// Nothing in the response says anything about caching.
        /// </summary>
        /// <remarks>
        /// Not the same as "not cacheable". A response with no directives is left to heuristic
        /// freshness, which every client implements differently and none document - so the site has
        /// no idea how long its own output is being reused for, or whether it is. On most
        /// Optimizely sites this is the largest bucket, and it is the one worth moving.
        /// </remarks>
        NoDirective = 0,

        /// <summary>The response may not be written to any cache at all.</summary>
        NoStore = 1,

        /// <summary>
        /// The response may be stored, but not reused without asking the server first.
        /// </summary>
        /// <remarks>
        /// Covers <c>no-cache</c>, a zero freshness lifetime, and a bare <c>must-revalidate</c>.
        /// All three mean the same thing in traffic terms - a request reaches the site for every
        /// use - which is why they are counted as one bucket rather than three. Whether that
        /// request is cheap depends on whether a validator went out with it; see
        /// <see cref="ResponseCacheSummary.HasValidator"/>.
        /// </remarks>
        Revalidate = 2,

        /// <summary>Reusable by the browser that asked for it, and by nothing else.</summary>
        Private = 3,

        /// <summary>
        /// Reusable by any cache, including a shared one.
        /// </summary>
        /// <remarks>
        /// Means "not marked private" rather than "carries the <c>public</c> directive": a bare
        /// <c>max-age</c> is shared-cacheable too, and reading it otherwise would put most
        /// CDN-cacheable responses in the wrong bucket.
        /// </remarks>
        Public = 4
    }

    /// <summary>
    /// One outbound response's caching headers, read once and reduced to the four facts the
    /// counters are built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately knows nothing about ASP.NET. The two sinks that do - the middleware on V12 and
    /// V13, the HTTP module on V11 - pull four header values out of whatever their host calls a
    /// response and hand them here, so the classification itself is one implementation tested
    /// against strings rather than two tested against web servers.
    /// </para>
    /// <para>
    /// A struct, and produced without allocating. This runs once per response on the thread that is
    /// about to send it, so it parses <c>Cache-Control</c> by scanning indices rather than by
    /// splitting the string - the measurement of how cacheable a site is must not be a per-response
    /// allocation on the site's own hot path.
    /// </para>
    /// </remarks>
    public readonly struct ResponseCacheSummary
    {
        /// <summary>
        /// Ten years, the ceiling applied to any stated freshness lifetime.
        /// </summary>
        /// <remarks>
        /// Not validation - an absurd <c>max-age</c> is still counted, just not at face value. The
        /// counter reports a mean and a maximum, and a single header carrying
        /// <c>max-age=99999999999</c> would otherwise be the only thing either of them ever said.
        /// </remarks>
        public const long MaxFreshnessSeconds = 315360000;

        private ResponseCacheSummary(
            ResponseCacheability cacheability,
            double freshnessSeconds,
            bool hasValidator,
            bool hasSetCookie)
        {
            Cacheability = cacheability;
            FreshnessSeconds = freshnessSeconds;
            HasValidator = hasValidator;
            HasSetCookie = hasSetCookie;
        }

        /// <summary>What a cache is permitted to do with this response.</summary>
        public ResponseCacheability Cacheability { get; }

        /// <summary>
        /// How long the response stays fresh, in seconds, or a negative value when it never says.
        /// </summary>
        /// <remarks>
        /// Only ever set for <see cref="ResponseCacheability.Private"/> and
        /// <see cref="ResponseCacheability.Public"/>. A revalidating response has a freshness
        /// lifetime of zero by definition, and averaging those zeros into the same series would
        /// pull the mean towards a number no response actually stated.
        /// </remarks>
        public double FreshnessSeconds { get; }

        /// <summary>Whether <see cref="FreshnessSeconds"/> holds a stated lifetime.</summary>
        public bool HasFreshness => FreshnessSeconds >= 0;

        /// <summary>
        /// Whether the response carried an <c>ETag</c> or a <c>Last-Modified</c>.
        /// </summary>
        /// <remarks>
        /// The difference between a revalidation costing a 304 and costing the whole body again.
        /// Read against <see cref="ResponseCacheability.Revalidate"/>: a site whose responses all
        /// revalidate is not necessarily in trouble, but a site whose responses all revalidate
        /// <em>without</em> a validator is re-sending everything it has already sent.
        /// </remarks>
        public bool HasValidator { get; }

        /// <summary>Whether the response set a cookie.</summary>
        public bool HasSetCookie { get; }

        /// <summary>
        /// Whether the response claims to be shared-cacheable while carrying a <c>Set-Cookie</c>.
        /// </summary>
        /// <remarks>
        /// The most common way a site's caching is undone by the site itself. A shared cache will
        /// not store a response with a <c>Set-Cookie</c> on it - storing one would hand a second
        /// visitor the first visitor's session - so a CDN quietly declines the whole response and
        /// the origin keeps serving it. Nothing errors, no header looks wrong in isolation, and the
        /// site's own hit rate says nothing because the miss never reaches it.
        /// </remarks>
        public bool SharedCacheConflict =>
            HasSetCookie && Cacheability == ResponseCacheability.Public;

        /// <summary>
        /// Classifies one response from its caching headers.
        /// </summary>
        /// <param name="cacheControl">The <c>Cache-Control</c> header, or null when absent.</param>
        /// <param name="expires">The <c>Expires</c> header, or null when absent.</param>
        /// <param name="hasValidator">Whether an <c>ETag</c> or <c>Last-Modified</c> went out.</param>
        /// <param name="hasSetCookie">Whether a <c>Set-Cookie</c> went out.</param>
        /// <returns>The classification.</returns>
        public static ResponseCacheSummary Describe(
            string? cacheControl, string? expires, bool hasValidator, bool hasSetCookie) =>
            Describe(cacheControl, expires, hasValidator, hasSetCookie, DateTimeOffset.UtcNow);

        /// <summary>
        /// Classifies one response against a supplied clock.
        /// </summary>
        /// <param name="cacheControl">The <c>Cache-Control</c> header, or null when absent.</param>
        /// <param name="expires">The <c>Expires</c> header, or null when absent.</param>
        /// <param name="hasValidator">Whether an <c>ETag</c> or <c>Last-Modified</c> went out.</param>
        /// <param name="hasSetCookie">Whether a <c>Set-Cookie</c> went out.</param>
        /// <param name="nowUtc">The moment the response is being sent.</param>
        /// <returns>The classification.</returns>
        /// <remarks>
        /// The clock is a parameter because <c>Expires</c> is only meaningful relative to one, and
        /// a classification that read the wall clock could not be asserted against a fixed date.
        /// Nothing in production passes anything but <see cref="DateTimeOffset.UtcNow"/>.
        /// </remarks>
        public static ResponseCacheSummary Describe(
            string? cacheControl,
            string? expires,
            bool hasValidator,
            bool hasSetCookie,
            DateTimeOffset nowUtc)
        {
            var directives = Directives.Empty;

            if (!string.IsNullOrEmpty(cacheControl))
            {
                directives.ReadFrom(cacheControl!);
            }

            var cacheability = ResponseCacheability.NoDirective;
            double freshness = -1;

            if (directives.NoStore)
            {
                cacheability = ResponseCacheability.NoStore;
            }
            else
            {
                // The client's view, so max-age wins over s-maxage where both are stated. A
                // response that is stale for a CDN and fresh for a browser is fresh here, and the
                // shared half of the story is told by Public versus Private instead.
                var stated = directives.MaxAge >= 0 ? directives.MaxAge : directives.SharedMaxAge;

                if (directives.NoCache || stated == 0)
                {
                    cacheability = ResponseCacheability.Revalidate;
                }
                else if (stated > 0)
                {
                    cacheability = directives.Private
                        ? ResponseCacheability.Private
                        : ResponseCacheability.Public;
                    freshness = stated;
                }
                else if (directives.Private)
                {
                    cacheability = ResponseCacheability.Private;
                }
                else if (directives.Public)
                {
                    cacheability = ResponseCacheability.Public;
                }
                else if (directives.MustRevalidate)
                {
                    cacheability = ResponseCacheability.Revalidate;
                }
                else if (!string.IsNullOrEmpty(expires))
                {
                    // Reached only when Cache-Control said nothing this cares about, which is the
                    // order HTTP itself specifies: Cache-Control overrides Expires wherever both
                    // are present.
                    var stillFreshFor = FreshnessFromExpires(expires!, nowUtc);

                    if (stillFreshFor > 0)
                    {
                        cacheability = ResponseCacheability.Public;
                        freshness = stillFreshFor;
                    }
                    else
                    {
                        cacheability = ResponseCacheability.Revalidate;
                    }
                }
            }

            return new ResponseCacheSummary(cacheability, freshness, hasValidator, hasSetCookie);
        }

        /// <remarks>
        /// An unparseable value counts as already expired rather than as absent, because the two
        /// idioms that do not parse - <c>Expires: 0</c> and <c>Expires: -1</c> - are both ways of
        /// saying "do not reuse this", and HTTP requires a recipient to treat any invalid Expires
        /// as a time in the past.
        /// </remarks>
        private static double FreshnessFromExpires(string expires, DateTimeOffset nowUtc)
        {
            if (!DateTimeOffset.TryParse(
                    expires,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var when))
            {
                return 0;
            }

            var seconds = (when - nowUtc).TotalSeconds;

            return seconds <= 0 ? 0 : Math.Min(seconds, MaxFreshnessSeconds);
        }

        /// <summary>
        /// The <c>Cache-Control</c> directives this classification looks at.
        /// </summary>
        /// <remarks>
        /// Filled by scanning the header in place. Everything else in the header - <c>immutable</c>,
        /// <c>stale-while-revalidate</c>, <c>proxy-revalidate</c>, anything a CDN invented - is
        /// skipped rather than rejected: an unrecognised directive does not change which of the
        /// five buckets a response falls into, and a parser that failed on one would silently stop
        /// classifying the responses most likely to be interesting.
        /// </remarks>
        private struct Directives
        {
            public bool NoStore;
            public bool NoCache;
            public bool Public;
            public bool Private;
            public bool MustRevalidate;

            /// <summary>Seconds from <c>max-age</c>, or -1 when it was not stated.</summary>
            public long MaxAge;

            /// <summary>Seconds from <c>s-maxage</c>, or -1 when it was not stated.</summary>
            public long SharedMaxAge;

            public static Directives Empty => new Directives { MaxAge = -1, SharedMaxAge = -1 };

            /// <summary>
            /// Reads one header value, which may hold any number of comma-separated directives.
            /// </summary>
            /// <param name="value">The raw header value.</param>
            public void ReadFrom(string value)
            {
                var index = 0;

                while (index < value.Length)
                {
                    // Quotes are tracked because a directive may carry a quoted field list -
                    // private="Set-Cookie, X-Thing" is legal - and a naive split on the comma
                    // inside it would read the second half as a directive of its own.
                    var start = index;
                    var quoted = false;

                    while (index < value.Length)
                    {
                        var c = value[index];

                        if (c == '"')
                        {
                            quoted = !quoted;
                        }
                        else if (c == ',' && !quoted)
                        {
                            break;
                        }

                        index++;
                    }

                    var end = index;

                    if (index < value.Length)
                    {
                        index++;
                    }

                    Read(value, start, end);
                }
            }

            private void Read(string value, int start, int end)
            {
                while (start < end && IsWhitespace(value[start]))
                {
                    start++;
                }

                while (end > start && IsWhitespace(value[end - 1]))
                {
                    end--;
                }

                if (start == end)
                {
                    return;
                }

                var separator = -1;

                for (var i = start; i < end; i++)
                {
                    if (value[i] == '=')
                    {
                        separator = i;
                        break;
                    }
                }

                var nameEnd = separator < 0 ? end : separator;

                while (nameEnd > start && IsWhitespace(value[nameEnd - 1]))
                {
                    nameEnd--;
                }

                var length = nameEnd - start;

                if (NameIs(value, start, length, "no-store"))
                {
                    NoStore = true;
                }
                else if (NameIs(value, start, length, "no-cache"))
                {
                    NoCache = true;
                }
                else if (NameIs(value, start, length, "public"))
                {
                    Public = true;
                }
                else if (NameIs(value, start, length, "private"))
                {
                    Private = true;
                }
                else if (NameIs(value, start, length, "must-revalidate"))
                {
                    MustRevalidate = true;
                }
                else if (separator > 0 && NameIs(value, start, length, "max-age"))
                {
                    MaxAge = ParseSeconds(value, separator + 1, end);
                }
                else if (separator > 0 && NameIs(value, start, length, "s-maxage"))
                {
                    SharedMaxAge = ParseSeconds(value, separator + 1, end);
                }
            }

            /// <remarks>
            /// Compares in place and folds case by hand. Directive names are ASCII and
            /// case-insensitive per HTTP, and this runs once per directive per response - a
            /// <c>Substring</c> and a <c>ToLowerInvariant</c> to do the same job would allocate
            /// twice for every one of them.
            /// </remarks>
            private static bool NameIs(string value, int start, int length, string name)
            {
                if (length != name.Length)
                {
                    return false;
                }

                for (var i = 0; i < length; i++)
                {
                    var c = value[start + i];

                    if (c >= 'A' && c <= 'Z')
                    {
                        c = (char)(c + 32);
                    }

                    if (c != name[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <returns>
            /// The value in seconds, or -1 if it is not a plain non-negative integer. Anything
            /// above <see cref="MaxFreshnessSeconds"/> is clamped to it.
            /// </returns>
            private static long ParseSeconds(string value, int start, int end)
            {
                while (start < end && (IsWhitespace(value[start]) || value[start] == '"'))
                {
                    start++;
                }

                while (end > start && (IsWhitespace(value[end - 1]) || value[end - 1] == '"'))
                {
                    end--;
                }

                if (start == end)
                {
                    return -1;
                }

                long seconds = 0;

                for (var i = start; i < end; i++)
                {
                    var c = value[i];

                    if (c < '0' || c > '9')
                    {
                        return -1;
                    }

                    seconds = (seconds * 10) + (c - '0');

                    if (seconds >= MaxFreshnessSeconds)
                    {
                        return MaxFreshnessSeconds;
                    }
                }

                return seconds;
            }

            private static bool IsWhitespace(char c) => c == ' ' || c == '\t';
        }
    }
}
