using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Who is reporting, and from where.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field here is written onto the events explicitly rather than left to Application
    /// Insights' own context. <c>cloud_RoleInstance</c> is populated differently by the ASP.NET Core
    /// SDK, by the App Service codeless agent and by whatever initializer a site has added of its
    /// own, is absent on V11, and can be rewritten by the host after we have gone. More decisively:
    /// the state file is keyed by the identity this class computes, so if the events carried a
    /// different one the change rows and the inventory rows would stop lining up and nothing would
    /// say why.
    /// </para>
    /// <para>
    /// <see cref="MachineName"/> is emitted alongside because it is what the SDK derives
    /// <c>cloud_RoleInstance</c> from in a Linux container - the container id - which is the join
    /// back to the rest of the telemetry.
    /// </para>
    /// </remarks>
    public sealed class DeploymentEnvironment
    {
        private DeploymentEnvironment(
            string instanceId,
            string roleName,
            string machineName,
            string? slot,
            string? containerImage,
            string runtime,
            string optimizelyVersion,
            DateTimeOffset processStartUtc,
            bool isAzureAppService,
            bool appServiceStorageEnabled)
        {
            InstanceId = instanceId;
            RoleName = roleName;
            MachineName = machineName;
            Slot = slot;
            ContainerImage = containerImage;
            Runtime = runtime;
            OptimizelyVersion = optimizelyVersion;
            ProcessStartUtc = processStartUtc;
            IsAzureAppService = isAzureAppService;
            AppServiceStorageEnabled = appServiceStorageEnabled;
        }

        /// <summary>Gets the identity the state file is keyed by.</summary>
        public string InstanceId { get; }

        /// <summary>Gets the application this instance belongs to.</summary>
        public string RoleName { get; }

        /// <summary>Gets the machine name, which in a Linux container is the container id.</summary>
        public string MachineName { get; }

        /// <summary>Gets the deployment slot, or null when not on App Service.</summary>
        public string? Slot { get; }

        /// <summary>
        /// Gets the container image this instance was started from, or null.
        /// </summary>
        /// <remarks>
        /// Worth capturing wherever it exists, because on a container topology it is the
        /// deployment's own name - the DXP images are tagged with the package that built them - and
        /// no amount of assembly scanning reconstructs that. It answers "which build is this"
        /// where the fingerprint answers "are these the same bits".
        /// </remarks>
        public string? ContainerImage { get; }

        /// <summary>
        /// Gets the runtime description.
        /// </summary>
        /// <remarks>
        /// The one part of the deployment the directory scan cannot see. On a framework-dependent
        /// deployment the shared framework is outside the application directory, so a runtime
        /// patch changes what is running without changing a single file the scan reads.
        /// </remarks>
        public string Runtime { get; }

        /// <summary>Gets the detected Optimizely major.</summary>
        public string OptimizelyVersion { get; }

        /// <summary>Gets when this process started, which separates a restart from a deployment.</summary>
        public DateTimeOffset ProcessStartUtc { get; }

        /// <summary>Gets whether this is running on Azure App Service.</summary>
        public bool IsAzureAppService { get; }

        /// <summary>
        /// Gets whether App Service's persistent storage share is mounted.
        /// </summary>
        /// <remarks>
        /// False is the default for a custom container, and is what the DXP topology this was built
        /// against runs: the setting is absent, so <c>/home</c> is container-local and nothing
        /// written there outlives the container. Since a deployment on that topology <em>is</em> a
        /// new container, it means no previous manifest is ever there to compare against.
        /// <see cref="DeploymentStateStore"/> reports that rather than hiding it.
        /// </remarks>
        public bool AppServiceStorageEnabled { get; }

        /// <summary>
        /// Reads the environment.
        /// </summary>
        /// <param name="optimizelyVersion">The detected Optimizely major, as text.</param>
        /// <returns>The environment.</returns>
        public static DeploymentEnvironment Detect(string optimizelyVersion)
        {
            var machineName = Safe(() => Environment.MachineName) ?? "unknown";
            var websiteInstance = Variable("WEBSITE_INSTANCE_ID");
            var siteName = Variable("WEBSITE_SITE_NAME");

            return new DeploymentEnvironment(
                // WEBSITE_INSTANCE_ID first because it identifies the App Service worker, which
                // outlives an individual container - so state keyed by it survives a container
                // restart that the machine name would have orphaned.
                instanceId: websiteInstance ?? machineName,
                roleName: siteName ?? EntryAssemblyName() ?? machineName,
                machineName: machineName,
                slot: Variable("WEBSITE_SLOT_NAME"),
                containerImage: Variable("DOCKER_CUSTOM_IMAGE_NAME"),
                runtime: Safe(() => RuntimeInformation.FrameworkDescription) ?? "unknown",
                optimizelyVersion: optimizelyVersion,
                processStartUtc: ProcessStart(),
                isAzureAppService: websiteInstance != null || siteName != null,
                appServiceStorageEnabled: IsTrue(Variable("WEBSITES_ENABLE_APP_SERVICE_STORAGE")));
        }

        private static string? Variable(string name)
        {
            var value = Safe(() => Environment.GetEnvironmentVariable(name));

            return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        private static bool IsTrue(string? value) =>
            value != null
            && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.Ordinal));

        private static string? EntryAssemblyName() =>
            Safe(() => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name);

        private static DateTimeOffset ProcessStart()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    return process.StartTime.ToUniversalTime();
                }
            }
            catch (Exception)
            {
                // Denied in some hardened containers. The process start time is a nicety - it
                // separates "restarted" from "redeployed" when reading a timeline by eye - and not
                // worth a failure.
                return DateTimeOffset.MinValue;
            }
        }

        private static string? Safe(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Writes the environment onto an event's properties.
        /// </summary>
        /// <param name="properties">The property bag to add to.</param>
        public void Describe(System.Collections.Generic.IDictionary<string, string> properties)
        {
            properties["InstanceId"] = InstanceId;
            properties["RoleName"] = RoleName;
            properties["MachineName"] = MachineName;
            properties["Runtime"] = Runtime;
            properties["OptimizelyVersion"] = OptimizelyVersion;

            if (Slot != null)
            {
                properties["Slot"] = Slot;
            }

            if (ContainerImage != null)
            {
                properties["ContainerImage"] = ContainerImage;
            }

            if (ProcessStartUtc != DateTimeOffset.MinValue)
            {
                properties["ProcessStartUtc"] =
                    ProcessStartUtc.ToString("O", CultureInfo.InvariantCulture);
            }
        }
    }
}
