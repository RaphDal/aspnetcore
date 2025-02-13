// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

internal static class DebugProxyLauncher
{
    private static readonly object LaunchLock = new object();
    private static readonly TimeSpan DebugProxyLaunchTimeout = TimeSpan.FromSeconds(10);
    private static Task<string>? LaunchedDebugProxyUrl;
    private static readonly Regex NowListeningRegex = new Regex(@"^\s*Now listening on: (?<url>.*)$", RegexOptions.None, TimeSpan.FromSeconds(10));
    private static readonly Regex ApplicationStartedRegex = new Regex(@"^\s*Application started\. Press Ctrl\+C to shut down\.$", RegexOptions.None, TimeSpan.FromSeconds(10));
    private static readonly string[] MessageSuppressionPrefixes = new[]
    {
            "Hosting environment:",
            "Content root path:",
            "Now listening on:",
            "Application started. Press Ctrl+C to shut down.",
        };

    public static Task<string> EnsureLaunchedAndGetUrl(IServiceProvider serviceProvider, string devToolsHost)
    {
        lock (LaunchLock)
        {
            if (LaunchedDebugProxyUrl == null)
            {
                LaunchedDebugProxyUrl = LaunchAndGetUrl(serviceProvider, devToolsHost);
            }

            return LaunchedDebugProxyUrl;
        }
    }

    private static async Task<string> LaunchAndGetUrl(IServiceProvider serviceProvider, string devToolsHost)
    {
        var tcs = new TaskCompletionSource<string>();

        var environment = serviceProvider.GetRequiredService<IWebHostEnvironment>();
        var executablePath = LocateDebugProxyExecutable(environment);
        var muxerPath = DotNetMuxer.MuxerPathOrDefault();
        var ownerPid = Environment.ProcessId;

        var processStartInfo = new ProcessStartInfo
        {
            FileName = muxerPath,
            Arguments = $"exec \"{executablePath}\" --OwnerPid {ownerPid} --DevToolsUrl {devToolsHost}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        RemoveUnwantedEnvironmentVariables(processStartInfo.Environment);

        var debugProxyProcess = Process.Start(processStartInfo);
        if (debugProxyProcess is null)
        {
            tcs.TrySetException(new InvalidOperationException("Unable to start debug proxy process."));
        }
        else
        {
            PassThroughConsoleOutput(debugProxyProcess);
            CompleteTaskWhenServerIsReady(debugProxyProcess, tcs);

            new CancellationTokenSource(DebugProxyLaunchTimeout).Token.Register(() =>
            {
                tcs.TrySetException(new TimeoutException($"Failed to start the debug proxy within the timeout period of {DebugProxyLaunchTimeout.TotalSeconds} seconds."));
            });
        }

        return await tcs.Task;
    }

    private static void RemoveUnwantedEnvironmentVariables(IDictionary<string, string?> environment)
    {
        // Generally we expect to pass through most environment variables, since dotnet might
        // need them for arbitrary reasons to function correctly. However, we specifically don't
        // want to pass through any ASP.NET Core hosting related ones, since the child process
        // shouldn't be trying to use the same port numbers, etc. In particular we need to break
        // the association with IISExpress and the MS-ASPNETCORE-TOKEN check.
        // For more context on this, see https://github.com/dotnet/aspnetcore/issues/20308.
        var keysToRemove = environment.Keys.Where(key => key.StartsWith("ASPNETCORE_", StringComparison.Ordinal)).ToList();
        foreach (var key in keysToRemove)
        {
            environment.Remove(key);
        }
    }

    private static string LocateDebugProxyExecutable(IWebHostEnvironment environment)
    {
        var assembly = Assembly.Load(environment.ApplicationName);
        var debugProxyPath = Path.Combine(
            Path.GetDirectoryName(assembly.Location)!,
            "BlazorDebugProxy",
            "BrowserDebugHost.dll");

        if (!File.Exists(debugProxyPath))
        {
            throw new FileNotFoundException(
                $"Cannot start debug proxy because it cannot be found at '{debugProxyPath}'");
        }

        return debugProxyPath;
    }

    private static void PassThroughConsoleOutput(Process process)
    {
        process.OutputDataReceived += (sender, eventArgs) =>
        {
            // It's confusing if the debug proxy emits its own startup status messages, because the developer
            // may think the ports/environment/paths refer to their actual application. So we want to suppress
            // them, but we can't stop the debug proxy app from emitting the messages entirely (e.g., via
            // SuppressStatusMessages) because we need the "Now listening on" one to detect the chosen port.
            // Instead, we'll filter out known strings from the passthrough logic. It's legit to hardcode these
            // strings because they are also hardcoded like this inside WebHostExtensions.cs and can't vary
            // according to culture.
            if (eventArgs.Data is not null)
            {
                foreach (var prefix in MessageSuppressionPrefixes)
                {
                    if (eventArgs.Data.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }

            Console.WriteLine(eventArgs.Data);
        };
    }

    private static void CompleteTaskWhenServerIsReady(Process aspNetProcess, TaskCompletionSource<string> taskCompletionSource)
    {
        string? capturedUrl = null;
        aspNetProcess.OutputDataReceived += OnOutputDataReceived;
        aspNetProcess.BeginOutputReadLine();

        void OnOutputDataReceived(object sender, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrEmpty(eventArgs.Data))
            {
                taskCompletionSource.TrySetException(new InvalidOperationException(
                    "No output has been recevied from the application."));
                return;
            }

            if (ApplicationStartedRegex.IsMatch(eventArgs.Data))
            {
                aspNetProcess.OutputDataReceived -= OnOutputDataReceived;
                if (!string.IsNullOrEmpty(capturedUrl))
                {
                    taskCompletionSource.TrySetResult(capturedUrl);
                }
                else
                {
                    taskCompletionSource.TrySetException(new InvalidOperationException(
                        "The application started listening without first advertising a URL"));
                }
            }
            else
            {
                var match = NowListeningRegex.Match(eventArgs.Data);
                if (match.Success)
                {
                    capturedUrl = match.Groups["url"].Value;
                    capturedUrl = capturedUrl.Replace("http://", "ws://");
                    capturedUrl = capturedUrl.Replace("https://", "wss://");
                }
            }
        }
    }
}
