# Optimizely.Performance.Counters - after installation

This folder was created by the package on your first build. It holds a settings template and this
file. Neither is read by the running site: the site reads its settings from `appsettings.json`
(CMS 12 and CMS 13) or `web.config` (CMS 11), so the template is something to copy **from**.

Nothing here needs to be done for the package to work. Every value in the template is the default
already in force, and a site that never touches it publishes exactly the same counters it does now.
The template exists so that when you do need to change something - most often to switch a probe off
during an incident - you can see what there is to change without reading the source.

## What you have

| File | Applies to | What to do with it |
| --- | --- | --- |
| `Optimizely.Instrumentation.template.json` | CMS 12, CMS 13 | Merge the `Optimizely` block into `appsettings.json` |
| `Optimizely.Instrumentation.template.config` | CMS 11 | Merge the `<add>` elements into `<appSettings>` in `web.config` |

Only the one matching your site's .NET version is delivered.

## Merging it

Copy across the settings you are changing and leave the rest out. A partial section is normal:
anything absent keeps its default, so a `web.config` or `appsettings.json` carrying one line is a
perfectly good configuration.

For CMS 12 and CMS 13, if `appsettings.json` already has an `Optimizely` block - it usually does,
and `Optimizely.Performance.DotNetCounters` adds one - put `Instrumentation` inside the existing
block rather than adding a second `Optimizely` key. JSON keeps the last of two duplicate keys and
the first one's contents are silently lost.

A minimal example for CMS 12 or CMS 13, turning the cache lock probe off and leaving everything
else alone:

```json
{
  "Optimizely": {
    "Instrumentation": {
      "Probes": {
        "CacheLock": { "Enabled": false }
      }
    }
  }
}
```

The same thing on CMS 11:

```xml
<appSettings>
  <add key="Optimizely:Instrumentation:Probes:CacheLock:Enabled" value="false" />
</appSettings>
```

The key paths are identical on all three versions, so settings carry across an upgrade unchanged -
they only change shape, from XML attributes to JSON nesting.

## Checking it took effect

Settings are read once, during startup, and the modules log what they decided at information level.
A setting that did not parse is logged as a warning naming the key, the value and the default being
used instead, and a key the package does not recognise is logged as a warning listing it. If you
change something and see no line about it in the startup log, the change is not being read - check
that it is inside the right section and spelled the way the template spells it.

Nothing in this package throws on a bad setting. A site that will not start because a counter
setting is misspelled would be a worse outcome than a counter running with its default.

## The one setting worth knowing before you need it

```
Optimizely:Instrumentation:Enabled = false
```

The master switch. Nothing is decorated, no probe starts, and no counter from this package is
registered with Application Insights - each module logs one line and returns. It is there so that
an operator who suspects this package during an incident can rule it out with a setting and a
recycle rather than a deployment.

This is separate from `Optimizely:PerformanceCounters`, which belongs to
`Optimizely.Performance.DotNetCounters` and controls the stock .NET, IIS and OS counters. The two
packages are usually installed together and are configured independently; switching one off leaves
the other running.

## Turning off the delivery of this folder

The copy happens once, when the file is not already there, so removing the folder brings it back on
the next build. To stop it for good, add this to your project file:

```xml
<PropertyGroup>
  <OptimizelyInstrumentationCopyTemplate>false</OptimizelyInstrumentationCopyTemplate>
</PropertyGroup>
```
