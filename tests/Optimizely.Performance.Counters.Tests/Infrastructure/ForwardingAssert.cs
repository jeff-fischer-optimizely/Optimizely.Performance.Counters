using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Turns a <see cref="ForwardingSweep"/> result set into a single assertion whose message
    /// names every member that failed, rather than stopping at the first one.
    /// </summary>
    public static class ForwardingAssert
    {
        /// <summary>
        /// Fails if any member did not reach the wrapped implementation intact.
        /// </summary>
        /// <param name="results">What the sweep found.</param>
        /// <param name="atLeast">
        /// Lower bound on the member count. A sweep that stopped enumerating - by losing the walk
        /// over base interfaces, say - would otherwise pass by covering almost nothing. The bound
        /// is the count common to every supported major, since later ones only add members.
        /// </param>
        public static void AllForwarded(IReadOnlyList<MemberResult> results, int atLeast)
        {
            Assert.True(
                results.Count >= atLeast,
                $"The sweep only reached {results.Count} members; at least {atLeast} were expected.");

            var failures = results.Where(r => !r.Forwarded).ToList();

            Assert.True(
                failures.Count == 0,
                $"{failures.Count} of {results.Count} members did not forward correctly:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(f => "  " + f)));
        }
    }
}
