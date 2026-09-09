# Optimizely.Performance.Counters - post-install message.
#
# Runs only for packages.config installs, which in practice means a CMS 11 site: NuGet stopped
# running install scripts when PackageReference arrived, so V12 and V13 sites never see this. Those
# are told the same thing by the build target in build\<PackageId>.targets, which prints a line the
# first time it delivers the template.
#
# It prints and does nothing else. Editing a site's web.config from an install script is how a
# package ends up blamed for a startup failure nobody can trace, and there is nothing here worth
# that: the package runs correctly with no configuration at all.

param($installPath, $toolsPath, $package, $project)

$folder = 'App_Data\Optimizely.Performance.Counters'

Write-Host ''
Write-Host "$($package.Id) $($package.Version) installed." -ForegroundColor Cyan
Write-Host ''
Write-Host 'No configuration is required. The counters publish with their defaults as installed.'
Write-Host ''
Write-Host 'To change or switch off any of them, a settings template has been added to your project:'
Write-Host "  $folder\Optimizely.Instrumentation.template.config" -ForegroundColor White
Write-Host ''
Write-Host 'Every value in it is the default already in force. Copy the <add> elements you want to'
Write-Host 'change into the <appSettings> element of web.config and leave the rest out.'
Write-Host ''
Write-Host 'The one worth knowing before you need it, which switches the whole package off:'
Write-Host '  <add key="Optimizely:Instrumentation:Enabled" value="false" />' -ForegroundColor White
Write-Host ''
Write-Host "See $folder\README_AFTER_INSTALL.md for the rest."
Write-Host ''
