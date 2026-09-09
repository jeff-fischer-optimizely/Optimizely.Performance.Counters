#if NET472
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web;
using Optimizely.Performance.Counters.Core.Http;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Http
{
    /// <summary>
    /// The V11 sink: the HTTP module that turns a finished response into a
    /// <see cref="ResponseCacheSummary"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thinner than its V12/V13 counterpart, and unavoidably so. The measurement itself reads
    /// <c>HttpResponse.Headers</c>, which throws <c>PlatformNotSupportedException</c> anywhere but
    /// inside a running integrated-mode pipeline - there is no System.Web equivalent of
    /// <c>DefaultHttpContext</c> to hand it. Driving that path is what
    /// <c>docs/SMOKE_TEST.md</c> is for.
    /// </para>
    /// <para>
    /// What is left is the part that has actually gone wrong before: whether the module reaches the
    /// pipeline at all. That is two assembly-level contracts and one package reference, none of
    /// which the compiler checks and all of which fail silently on a real site - the module simply
    /// never runs, and the counters read zero responses on a site serving thousands.
    /// </para>
    /// </remarks>
    public class HttpCacheabilityModuleTests
    {
        [Fact]
        public void The_assembly_asks_asp_net_to_register_the_module_before_the_application_starts()
        {
            // The only hook that can still add a module programmatically. Without this attribute the
            // package installs, compiles, and measures nothing - which is why it is asserted here
            // rather than left to the one place it would otherwise be visible, a running site.
            var attribute = typeof(HttpCacheabilityModule).Assembly
                .GetCustomAttributes<PreApplicationStartMethodAttribute>()
                .SingleOrDefault(candidate => candidate.Type == typeof(HttpCacheabilityModule));

            Assert.NotNull(attribute);
            Assert.Equal(nameof(HttpCacheabilityModule.RegisterModule), attribute!.MethodName);
        }

        [Fact]
        public void The_method_asp_net_is_pointed_at_is_one_it_can_call()
        {
            // PreApplicationStartMethod is resolved by name at run time, so the signature is a
            // contract with the framework rather than with any caller: public, static, void, no
            // parameters. Anything else throws while the application is starting.
            var method = typeof(HttpCacheabilityModule).GetMethod(
                nameof(HttpCacheabilityModule.RegisterModule),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            Assert.NotNull(method);
            Assert.Equal(typeof(void), method!.ReturnType);
        }

        [Fact]
        public void Registration_really_reaches_microsoft_web_infrastructure()
        {
            // The reference is on one target framework and pinned at the version a V11 site already
            // has in bin, so the risk is that it does not bind there. A type load failure would be
            // swallowed by RegisterModule exactly like every other failure, leaving a site with no
            // module and no explanation.
            //
            // Called outside a web application, where it may either succeed or be refused on the
            // framework's own terms - both mean the call was made. What is being ruled out is the
            // third outcome: a TypeLoadException or a missing assembly, which is the only one that
            // says the reference does not resolve at run time.
            var register = typeof(HttpCacheabilityModule).GetMethod(
                "Register", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(register);

            var thrown = Record.Exception(() => register!.Invoke(null, null))?.GetBaseException();

            Assert.False(
                thrown is TypeLoadException
                || thrown is FileNotFoundException
                || thrown is FileLoadException,
                "Microsoft.Web.Infrastructure did not bind at run time: " + thrown);
        }

        [Fact]
        public void Registering_the_module_never_throws()
        {
            // The entry point ASP.NET calls, and it runs before the site has a log or an error page.
            // A site with no cacheability counters is a gap in a chart; an exception escaping here
            // stops the application from starting at all.
            HttpCacheabilityModule.RegisterModule();
        }

        [Fact]
        public void An_application_that_cannot_be_measured_is_left_alone()
        {
            // Classic mode has no managed response header collection, so nothing is subscribed at
            // all rather than an exception being caught once per request for the life of the site.
            // Under test HttpRuntime.UsingIntegratedPipeline is false, which is that path.
            Assert.False(HttpRuntime.UsingIntegratedPipeline);

            // Not a using statement: IHttpModule declares its own Dispose rather than implementing
            // IDisposable, so ASP.NET calls it by interface and so does this.
            var module = new HttpCacheabilityModule();

            module.Init(new HttpApplication());
            module.Dispose();
        }

        [Fact]
        public void A_module_initialized_with_nothing_does_not_throw()
        {
            var module = new HttpCacheabilityModule();

            module.Init(null!);
            module.Dispose();
        }
    }
}
#endif
