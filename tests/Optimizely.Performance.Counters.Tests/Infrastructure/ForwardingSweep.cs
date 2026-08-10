using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Outcome of invoking one interface member on a decorator.
    /// </summary>
    public sealed class MemberResult
    {
        public MemberResult(string member, bool forwarded, string? problem)
        {
            Member = member;
            Forwarded = forwarded;
            Problem = problem;
        }

        public string Member { get; }

        public bool Forwarded { get; }

        public string? Problem { get; }

        public override string ToString() => Forwarded ? $"{Member}: forwarded" : $"{Member}: {Problem}";
    }

    /// <summary>
    /// Invokes every member of an interface on a decorator and checks that each one reached the
    /// wrapped implementation with its arguments intact.
    /// <para>
    /// This exists because the decorators are more than sixty hand-written pass-throughs. A
    /// transposed argument or a member forwarded to the wrong overload compiles cleanly and would
    /// only surface as wrong behaviour in a production CMS.
    /// </para>
    /// </summary>
    public static class ForwardingSweep
    {
        /// <summary>
        /// Runs every member of <paramref name="interfaceType"/> (and its base interfaces) through
        /// <paramref name="decorator"/>.
        /// </summary>
        /// <param name="decorator">The decorator under test.</param>
        /// <param name="interfaceType">The interface it implements.</param>
        /// <param name="recorder">The recorder behind the wrapped implementation.</param>
        /// <returns>One result per member.</returns>
        public static IReadOnlyList<MemberResult> Run(object decorator, Type interfaceType, RecordingProxy recorder)
        {
            var results = new List<MemberResult>();

            foreach (var method in MembersOf(interfaceType))
            {
                var closed = Close(method);
                if (closed == null)
                {
                    results.Add(new MemberResult(Describe(method), false,
                        "generic parameters could not be closed over a satisfying type"));
                    continue;
                }

                var before = recorder.Invocations.Count;
                object?[] arguments;
                try
                {
                    arguments = ArgumentFactory.For(closed);
                }
                catch (Exception ex)
                {
                    results.Add(new MemberResult(Describe(closed), false, $"could not build arguments: {ex.Message}"));
                    continue;
                }

                try
                {
                    var returned = closed.Invoke(decorator, arguments);

                    // Async members must be observed, or an exception thrown inside the decorator
                    // after the forwarding call would go unnoticed.
                    if (returned is Task task)
                    {
                        task.GetAwaiter().GetResult();
                    }
                }
                catch (TargetInvocationException ex)
                {
                    results.Add(new MemberResult(Describe(closed), false,
                        $"threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}"));
                    continue;
                }
                catch (Exception ex)
                {
                    results.Add(new MemberResult(Describe(closed), false, $"threw {ex.GetType().Name}: {ex.Message}"));
                    continue;
                }

                var captured = recorder.Invocations.Skip(before).ToList();
                results.Add(Verify(closed, arguments, captured));
            }

            return results;
        }

        private static MemberResult Verify(MethodInfo invoked, object?[] arguments, IReadOnlyList<Invocation> captured)
        {
            var name = Describe(invoked);

            if (captured.Count == 0)
            {
                return new MemberResult(name, false, "did not reach the wrapped implementation");
            }

            var match = captured.FirstOrDefault(c => SameMember(c.Method, invoked));
            if (match == null)
            {
                var reached = string.Join(", ", captured.Select(c => Describe(c.Method)));
                return new MemberResult(name, false, $"forwarded to the wrong member: {reached}");
            }

            if (!match.Arguments.SequenceEqual(arguments))
            {
                return new MemberResult(name, false,
                    $"arguments changed in transit: sent [{string.Join(", ", arguments)}], " +
                    $"received [{string.Join(", ", match.Arguments)}]");
            }

            return new MemberResult(name, true, null);
        }

        /// <summary>
        /// The interface's own members plus those it inherits. <c>Type.GetMethods</c> on an
        /// interface does not walk base interfaces, and IContentRepository extends IContentLoader.
        /// </summary>
        private static IEnumerable<MethodInfo> MembersOf(Type interfaceType) =>
            new[] { interfaceType }
                .Concat(interfaceType.GetInterfaces())
                .SelectMany(t => t.GetMethods())
                .Distinct();

        /// <summary>
        /// Closes a generic method over types that satisfy its constraints. An interface named in
        /// a constraint always satisfies that constraint and any accompanying <c>class</c>
        /// constraint, so it is used directly rather than inventing a stub type.
        /// </summary>
        private static MethodInfo? Close(MethodInfo method)
        {
            if (!method.IsGenericMethodDefinition)
            {
                return method;
            }

            var arguments = new List<Type>();
            foreach (var parameter in method.GetGenericArguments())
            {
                var constraint = parameter.GetGenericParameterConstraints().FirstOrDefault();
                if (constraint == null)
                {
                    arguments.Add(typeof(object));
                    continue;
                }

                arguments.Add(constraint);
            }

            try
            {
                return method.MakeGenericMethod(arguments.ToArray());
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SameMember(MethodInfo a, MethodInfo b) =>
            a.DeclaringType == b.DeclaringType && a.ToString() == b.ToString();

        private static string Describe(MethodInfo method) =>
            $"{method.DeclaringType?.Name}.{method}";
    }
}
