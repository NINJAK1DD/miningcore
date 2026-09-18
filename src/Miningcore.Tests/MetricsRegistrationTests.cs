using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Miningcore.Tests;

public class MetricsRegistrationTests
{
    [Fact]
    public async Task DefaultRegistry_ConcurrentFamilyRegistrationAndScrapesPreserveSamples()
    {
        // A child gives the real default registry a fresh first scrape and discards
        // the finite synthetic family inventory afterward. Keep parallel tests enabled.
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach(var argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.deps.json"),
            Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"), "metrics-registration",
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if(!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        Assert.True(process.ExitCode == 0, $"Metrics registration probe failed: {await stdout}\n{await stderr}");
        Assert.Contains("4096 families, 128 concurrent scrapes, all values verified", await stdout);
    }
}
