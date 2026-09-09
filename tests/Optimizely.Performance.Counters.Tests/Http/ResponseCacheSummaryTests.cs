using System;
using Optimizely.Performance.Counters.Core.Http;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Http
{
    /// <summary>
    /// The classification the whole feature rests on: four header values in, one of five buckets
    /// out.
    /// </summary>
    /// <remarks>
    /// Worth this many cases because everything downstream is arithmetic. The recorder only counts
    /// what it is handed and the sinks only fetch headers, so a bucket assigned wrongly here is a
    /// chart that is confidently wrong - and unlike a missing counter, nothing about it looks
    /// broken.
    /// </remarks>
    public class ResponseCacheSummaryTests
    {
        // A fixed clock, so that the Expires cases assert against a date rather than against
        // whenever the test happened to run.
        private static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void A_response_that_says_nothing_is_counted_as_saying_nothing()
        {
            // Not "uncacheable". The distinction is the point of having a bucket for it: these
            // responses are handed to whatever heuristic each client applies, so the site cannot
            // say how long its own output is being reused for.
            var summary = Describe(null, null);

            Assert.Equal(ResponseCacheability.NoDirective, summary.Cacheability);
            Assert.False(summary.HasFreshness);
        }

        [Theory]
        [InlineData("no-store")]
        [InlineData("no-store, no-cache, must-revalidate, max-age=600")]
        [InlineData("private, no-store")]
        public void No_store_beats_everything_else_in_the_header(string cacheControl)
        {
            // Precedence rather than preference: no-store means the response may not be written
            // down at all, so nothing else the header says can make it reusable.
            Assert.Equal(ResponseCacheability.NoStore, Describe(cacheControl, null).Cacheability);
        }

        [Theory]
        [InlineData("no-cache")]
        [InlineData("public, no-cache")]
        [InlineData("max-age=0")]
        [InlineData("s-maxage=0")]
        [InlineData("must-revalidate")]
        [InlineData("private, max-age=0")]
        public void Anything_that_forces_a_round_trip_is_one_bucket(string cacheControl)
        {
            // Three different spellings of the same traffic outcome - a request reaches the site
            // for every use - so they are counted together rather than as three thin series.
            Assert.Equal(ResponseCacheability.Revalidate, Describe(cacheControl, null).Cacheability);
        }

        [Theory]
        [InlineData("public, max-age=600")]
        [InlineData("max-age=600")]
        [InlineData("public")]
        public void A_response_not_marked_private_is_counted_as_shared_cacheable(string cacheControl)
        {
            // A bare max-age is shared-cacheable too. Requiring the literal 'public' directive
            // would put most of what a CDN can actually store into the wrong bucket.
            Assert.Equal(ResponseCacheability.Public, Describe(cacheControl, null).Cacheability);
        }

        [Theory]
        [InlineData("private")]
        [InlineData("private, max-age=600")]
        [InlineData("public, private, max-age=600")]
        public void The_private_directive_is_what_makes_a_response_browser_only(string cacheControl)
        {
            Assert.Equal(ResponseCacheability.Private, Describe(cacheControl, null).Cacheability);
        }

        [Fact]
        public void Freshness_is_read_from_max_age()
        {
            var summary = Describe("public, max-age=600", null);

            Assert.True(summary.HasFreshness);
            Assert.Equal(600, summary.FreshnessSeconds);
        }

        [Fact]
        public void Max_age_wins_over_s_maxage_because_the_question_is_about_the_client()
        {
            // A response that is stale for a CDN and fresh for a browser is fresh here. The shared
            // half of that story is told by Public versus Private, not by the lifetime.
            var summary = Describe("max-age=600, s-maxage=0", null);

            Assert.Equal(ResponseCacheability.Public, summary.Cacheability);
            Assert.Equal(600, summary.FreshnessSeconds);
        }

        [Fact]
        public void S_maxage_is_used_when_max_age_is_absent()
        {
            var summary = Describe("public, s-maxage=300", null);

            Assert.Equal(ResponseCacheability.Public, summary.Cacheability);
            Assert.Equal(300, summary.FreshnessSeconds);
        }

        [Theory]
        [InlineData("no-cache")]
        [InlineData("max-age=0")]
        [InlineData("no-store")]
        [InlineData("public")]
        [InlineData(null)]
        public void Only_a_reusable_response_reports_a_lifetime(string? cacheControl)
        {
            // A revalidating response is fresh for zero seconds by definition. Publishing those
            // zeros into the same series would drag its mean towards a number no response stated.
            Assert.False(Describe(cacheControl, null).HasFreshness);
        }

        [Fact]
        public void An_absurd_lifetime_is_clamped_rather_than_rejected()
        {
            // Still counted, just not at face value: the counter reports a mean and a maximum, and
            // one header carrying a decade of nonsense would otherwise be all either ever said.
            var summary = Describe("max-age=99999999999999999999", null);

            Assert.Equal(ResponseCacheability.Public, summary.Cacheability);
            Assert.Equal(ResponseCacheSummary.MaxFreshnessSeconds, summary.FreshnessSeconds);
        }

        [Theory]
        [InlineData("max-age=abc")]
        [InlineData("max-age=")]
        [InlineData("max-age=-5")]
        [InlineData("max-age")]
        public void A_lifetime_that_is_not_a_number_counts_as_not_stated(string cacheControl)
        {
            // With nothing else in the header, that leaves the response saying nothing - which is
            // exactly what a client will make of it too.
            var summary = Describe(cacheControl, null);

            Assert.Equal(ResponseCacheability.NoDirective, summary.Cacheability);
            Assert.False(summary.HasFreshness);
        }

        [Fact]
        public void Directive_names_are_matched_without_regard_to_case_or_spacing()
        {
            var summary = Describe("  PUBLIC ,\tMax-Age = 300 ", null);

            Assert.Equal(ResponseCacheability.Public, summary.Cacheability);
            Assert.Equal(300, summary.FreshnessSeconds);
        }

        [Fact]
        public void A_comma_inside_a_quoted_field_list_is_not_a_directive_separator()
        {
            // private="a, b" is legal, and splitting naively on the comma would read ' b"' as a
            // directive of its own. Harmless here, but the same slip against no-cache="..." would
            // hide the directive that follows it.
            var summary = Describe("private=\"Set-Cookie, X-Thing\", max-age=60", null);

            Assert.Equal(ResponseCacheability.Private, summary.Cacheability);
            Assert.Equal(60, summary.FreshnessSeconds);
        }

        [Fact]
        public void Unrecognised_directives_are_skipped_rather_than_failing_the_parse()
        {
            // CDNs invent directives constantly. A parser that gave up on one would stop
            // classifying exactly the responses most likely to be interesting.
            var summary = Describe("immutable, stale-while-revalidate=30, public, max-age=60", null);

            Assert.Equal(ResponseCacheability.Public, summary.Cacheability);
            Assert.Equal(60, summary.FreshnessSeconds);
        }

        [Fact]
        public void A_header_of_nothing_but_unrecognised_directives_says_nothing()
        {
            Assert.Equal(
                ResponseCacheability.NoDirective, Describe("immutable", null).Cacheability);
        }

        [Fact]
        public void Expires_is_read_when_cache_control_is_absent()
        {
            var summary = Describe(null, Now.AddMinutes(10).ToString("R"));

            Assert.Equal(ResponseCacheability.Public, summary.Cacheability);
            Assert.Equal(600, summary.FreshnessSeconds, precision: 0);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("not a date at all")]
        public void An_expires_that_does_not_parse_counts_as_already_expired(string expires)
        {
            // Expires: 0 and Expires: -1 are the two commonest ways of saying "do not reuse this",
            // and HTTP requires an invalid Expires to be read as a time in the past. Treating them
            // as absent would put a deliberately uncacheable response in the NoDirective bucket.
            Assert.Equal(
                ResponseCacheability.Revalidate, Describe(null, expires).Cacheability);
        }

        [Fact]
        public void An_expires_in_the_past_counts_as_a_round_trip()
        {
            Assert.Equal(
                ResponseCacheability.Revalidate,
                Describe(null, Now.AddMinutes(-1).ToString("R")).Cacheability);
        }

        [Fact]
        public void Cache_control_overrides_expires_wherever_both_are_present()
        {
            // The order HTTP itself specifies, and the case that matters: a site setting a long
            // Expires beside a short max-age is served on the max-age.
            var summary = Describe("max-age=60", Now.AddYears(1).ToString("R"));

            Assert.Equal(60, summary.FreshnessSeconds);
        }

        [Fact]
        public void Expires_is_still_read_when_cache_control_said_nothing_useful()
        {
            var summary = Describe("immutable", Now.AddMinutes(5).ToString("R"));

            Assert.Equal(ResponseCacheability.Public, summary.Cacheability);
            Assert.Equal(300, summary.FreshnessSeconds, precision: 0);
        }

        [Fact]
        public void A_validator_is_reported_whatever_the_bucket()
        {
            // Read against Revalidate, where it is the difference between a 304 and the whole body
            // going out again.
            var summary = ResponseCacheSummary.Describe(
                "no-cache", null, hasValidator: true, hasSetCookie: false, Now);

            Assert.Equal(ResponseCacheability.Revalidate, summary.Cacheability);
            Assert.True(summary.HasValidator);
        }

        [Fact]
        public void A_shared_cacheable_response_that_sets_a_cookie_is_a_conflict()
        {
            // The finding the whole counter exists for. A shared cache refuses to store this, so
            // the cache headers on it buy nothing at all and the origin keeps serving it.
            var summary = ResponseCacheSummary.Describe(
                "public, max-age=600", null, hasValidator: false, hasSetCookie: true, Now);

            Assert.True(summary.SharedCacheConflict);
        }

        [Theory]
        [InlineData("private, max-age=600")]
        [InlineData("no-store")]
        [InlineData(null)]
        public void A_cookie_on_anything_else_is_not_a_conflict(string? cacheControl)
        {
            // Only the shared-cacheable claim is wasted by a cookie. A private response is supposed
            // to carry one, and flagging those would bury the finding in noise.
            var summary = ResponseCacheSummary.Describe(
                cacheControl, null, hasValidator: false, hasSetCookie: true, Now);

            Assert.False(summary.SharedCacheConflict);
        }

        private static ResponseCacheSummary Describe(string? cacheControl, string? expires) =>
            ResponseCacheSummary.Describe(
                cacheControl, expires, hasValidator: false, hasSetCookie: false, Now);
    }
}
