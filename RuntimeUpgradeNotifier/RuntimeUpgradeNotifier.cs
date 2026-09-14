using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using RuntimeUpgrade.Notifier.Data;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;

#pragma warning disable CA1416 // checked at runtime because the OS-agnostic build may be run on any OS including Windows, especially in dependents' tests

namespace RuntimeUpgrade.Notifier;

/// <inheritdoc cref="IRuntimeUpgradeNotifier" />
public sealed class RuntimeUpgradeNotifier: IRuntimeUpgradeNotifier {

    private const string IGNORE_HANGUP = "RUNTIMEUPGRADENOTIFIER_NOHUP";

    private static readonly string   OLD_RUNTIME_VERSION      = Environment.Version.ToString(3);
    private static readonly string?  PROCESS_PATH             = Environment.ProcessPath;
    private static readonly string[] COMMAND_LINE_ARGS        = Environment.GetCommandLineArgs();
    private static readonly bool     IS_WINDOWS               = Environment.OSVersion.Platform == PlatformID.Win32NT;
    private static readonly string   WATCHED_RUNTIME_FILENAME = IS_WINDOWS ? "coreclr.dll" : "libcoreclr.so";
    private static readonly string   POWERSHELL_PATH          = IS_WINDOWS ? Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe") : string.Empty;

    private readonly object                                                  eventLock                      = new();
    private readonly ICollection<AsyncEventHandler>                          beforeRuntimeUpgradedCallbacks = [];
    private readonly ICollection<AsyncEventHandler<RuntimeUpgradeEventArgs>> runtimeUpgradedCallbacks       = [];

    private int                             subscriberCount;
    private FileSystemWatcher?              fileSystemWatcher;
    private string?                         watchedRuntimeDirectory;
    private string?                         serviceName;
    private ILogger<RuntimeUpgradeNotifier> logger = NullLogger<RuntimeUpgradeNotifier>.Instance;
    private bool?                           warnAboutServerHostingBundle;

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory {
        set {
            logger = value.CreateLogger<RuntimeUpgradeNotifier>();
            WarnAboutServerHostingBundleIfNecessary();
        }
    }

    static RuntimeUpgradeNotifier() {
        try {
            if (!IS_WINDOWS && Environment.GetEnvironmentVariable(IGNORE_HANGUP)?.ToLowerInvariant() is "1" or "true") {
                PosixSignalRegistration.Create(PosixSignal.SIGHUP, static signal => signal.Cancel = true);
            }

            // Eagerly load dynamic libraries that will be required later, because they will get deleted during an upgrade. This prevents "FileNotFoundException: Could not load file or assembly" errors.
            _ = new ProcessStartInfo();
            _ = Environment.CurrentDirectory;
            new AnonymousPipeServerStream().Dispose(); // Process.Start needs System.IO.Pipes to be loaded
            Stopwatch.StartNew().Reset();
            Task.WhenAll();
        } catch (SecurityException) {} catch (IOException) {}
    }

    /// <inheritdoc />
    public RestartStrategy RestartStrategy {
        get;
        set {
            if (field != value) {
                lock (eventLock) {
                    subscriberCount += value switch {
                        not RestartStrategy.Manual when field is RestartStrategy.Manual => 1,
                        RestartStrategy.Manual when field is not RestartStrategy.Manual => -1,
                        _                                                               => 0
                    };
                    field = value;

                    logger.LogTrace("Changed restart strategy to {strat}, new subscriber count is {subs}", field, subscriberCount);

                    if (value != RestartStrategy.Manual && subscriberCount == 1) {
                        StartListening();
                    } else if (value == RestartStrategy.Manual && subscriberCount == 0) {
                        StopListening();
                    }
                }

                if (value == RestartStrategy.AutoRestartService && serviceName == null) {
                    logger.LogTrace("Getting service name");
                    int selfPid = Environment.ProcessId;

                    try {
                        if (IS_WINDOWS) {
                            using ManagementObjectSearcher   wmiSearch  = new(new SelectQuery("Win32_Service", $"ProcessId = {selfPid}", ["Name"]));
                            using ManagementObjectCollection wmiResults = wmiSearch.Get();
                            using ManagementObject?          wmiResult  = wmiResults.Cast<ManagementObject>().FirstOrDefault();
                            serviceName = (string?) wmiResult?["Name"];
                        } else {
                            using Process ps = Process.Start(new ProcessStartInfo("/usr/bin/ps", ["-o", "unit=", selfPid.ToString()]) { RedirectStandardOutput = true })!;
                            ps.WaitForExit();
                            serviceName = ps.ExitCode == 0 ? ps.StandardOutput.ReadToEnd().Trim() : null;
                        }
                    } catch (Win32Exception e) {
                        logger.LogError(e, "Failed to get service name of current process");
                    } catch (SystemException e) {
                        logger.LogError(e, "Failed to get service name of current process");
                    }

                    if (serviceName != null) {
                        logger.LogTrace("This process is currently running as the service {name}", serviceName);
                    } else {
                        logger.LogDebug("This process is not currently running as a service, falling back from {oldStrat} to {newStrat} if it needs to be restarted",
                            nameof(RestartStrategy.AutoRestartService), nameof(RestartStrategy.AutoRestartProcess));
                        field = RestartStrategy.AutoRestartProcess;
                    }
                }

                WarnAboutServerHostingBundleIfNecessary();
            }
        }
    } = RestartStrategy.Manual;

    /// <inheritdoc />
    public ExitStrategy ExitStrategy { get; set; } = new EnvironmentExit(null);

    /// <inheritdoc />
    public TimeSpan WindowsInstallerFinishedDebounceDuration {
        get;
        set {
            if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value), value, "duration must be non-negative");
            field = value;
        }
    } = TimeSpan.FromMinutes(6);

    /// <inheritdoc />
    public event AsyncEventHandler<RuntimeUpgradeEventArgs> RuntimeUpgraded {
        add {
            lock (eventLock) {
                runtimeUpgradedCallbacks.Add(value);
                if (++subscriberCount == 1) {
                    StartListening();
                }
            }
        }
        remove {
            lock (eventLock) {
                runtimeUpgradedCallbacks.Remove(value);
                if (--subscriberCount == 0) {
                    StopListening();
                }
            }
        }
    }

    /// <inheritdoc />
    public event AsyncEventHandler BeforeRuntimeUpgraded {
        add {
            lock (eventLock) {
                beforeRuntimeUpgradedCallbacks.Add(value);
                if (++subscriberCount == 1) {
                    StartListening();
                }
            }
        }
        remove {
            lock (eventLock) {
                beforeRuntimeUpgradedCallbacks.Remove(value);
                if (--subscriberCount == 0) {
                    StopListening();
                }
            }
        }
    }

    private void StartListening() {
        try {
            using Process currentProcess = Process.GetCurrentProcess();
            watchedRuntimeDirectory ??= Path.GetDirectoryName(currentProcess.Modules.Cast<ProcessModule>()
                .FirstOrDefault(static module => module.ModuleName.Equals(WATCHED_RUNTIME_FILENAME, StringComparison.OrdinalIgnoreCase))?.FileName) ?? string.Empty;

            if (watchedRuntimeDirectory != string.Empty) {
                fileSystemWatcher         =  new FileSystemWatcher(watchedRuntimeDirectory, WATCHED_RUNTIME_FILENAME) { EnableRaisingEvents = true, IncludeSubdirectories = false };
                fileSystemWatcher.Deleted += OnRuntimeFileDeleted;
                logger.LogDebug("Monitoring .NET {runtimeVer} Runtime for upgrades by watching for deletion of {path}", OLD_RUNTIME_VERSION,
                    Path.Combine(watchedRuntimeDirectory, WATCHED_RUNTIME_FILENAME));
            } else {
                onListeningError(null);
            }
        } catch (NotSupportedException e) {
            onListeningError(e);
        } catch (Win32Exception e) {
            onListeningError(e);
        } catch (PathTooLongException e) {
            onListeningError(e);
        } catch (FileNotFoundException e) {
            onListeningError(e);
        }

        void onListeningError(Exception? e) => logger.LogError(e, "Failed to list modules loaded by current process or listen for changes to that file, not notifying for runtime upgrades.");
    }

    private async void OnRuntimeFileDeleted(object sender, FileSystemEventArgs evt) {
        try {
            if ((evt.ChangeType & WatcherChangeTypes.Deleted) != 0) {
                if (IS_WINDOWS) {
                    try {
                        Stopwatch sinceInstallationEnded = Stopwatch.StartNew();
                        while (sinceInstallationEnded.Elapsed < WindowsInstallerFinishedDebounceDuration) {
                            if (Mutex.TryOpenExisting(@"Global\_MSIExecute", out Mutex? msiMutex)) {
                                msiMutex.Dispose();

                                if (sinceInstallationEnded.IsRunning) { // starting installation
                                    sinceInstallationEnded.Reset();
                                }
                            } else if (!sinceInstallationEnded.IsRunning) { // stopping installation
                                sinceInstallationEnded.Restart();
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                        }
                    } catch (UnauthorizedAccessException e) {
                        logger.LogWarning(e, "Not allowed to find Windows Installer system mutex, assuming no msiexec installation is in progress now");
                    } catch (IOException e) {
                        onUncaughtException(e);
                    } catch (Exception e) when (e is not OutOfMemoryException) {
                        onUncaughtException(e);
                    }

                    void onUncaughtException(Exception e) {
                        logger.LogError(e, "Uncaught exception while waiting for Windows Installer to finish its installation before notifying program about .NET runtime upgrade");
                    }
                }

                logger.LogInformation(".NET {oldVer} Runtime was upgraded, {action}", OLD_RUNTIME_VERSION, RestartStrategy switch {
                    RestartStrategy.Manual              => "not doing anything besides firing events",
                    RestartStrategy.AutoStartNewProcess => "starting a new process for this program but not killing the old process",
                    RestartStrategy.AutoRestartProcess  => "starting a new process for this program and killing the old process",
                    RestartStrategy.AutoRestartService  => "requesting a service restart from the operating system",
                    RestartStrategy.AutoStopProcess     => "stopping this program but not starting a new process",
                    _                                   => "unsupported restart strategy"
                });

                IEnumerable<AsyncEventHandler>                          beforeRuntimeUpgradedCallbacks_;
                IEnumerable<AsyncEventHandler<RuntimeUpgradeEventArgs>> runtimeUpgradedCallbacks_;
                lock (eventLock) {
                    beforeRuntimeUpgradedCallbacks_ = beforeRuntimeUpgradedCallbacks.ToList();
                    runtimeUpgradedCallbacks_       = runtimeUpgradedCallbacks.ToList();
                }

                await Task.WhenAll(beforeRuntimeUpgradedCallbacks_.Select(handler => handler(this))).ConfigureAwait(false);

                RuntimeUpgradeEventArgs eventArgs = new();
                if (RestartStrategy is RestartStrategy.AutoRestartProcess or RestartStrategy.AutoStartNewProcess) {
                    logger.LogTrace("Starting new process of this program");
                    eventArgs.NewProcessId = StartNewProcessForCurrentProgram();
                }

                await Task.WhenAll(runtimeUpgradedCallbacks_.Select(handler => handler(this, eventArgs))).ConfigureAwait(false);

                switch (RestartStrategy) {
                    case RestartStrategy.AutoRestartProcess:
                    case RestartStrategy.AutoStopProcess:
                        try {
                            logger.LogTrace("Stopping old process");
                            await ExitStrategy.StopCurrentProcess().ConfigureAwait(false);
                        } catch (SecurityException e) {
                            logger.LogError(e, "Failed to exit current process");
                        }
                        break;
                    case RestartStrategy.AutoRestartService: {
                        logger.LogTrace("Restarting service {serviceName}", serviceName);
                        try {
                            ProcessStartInfo startInfo = IS_WINDOWS
                                ? new ProcessStartInfo(POWERSHELL_PATH, ["-Command", "Restart-Service", "-Name", serviceName!])
                                : new ProcessStartInfo("/usr/bin/systemctl", ["restart", serviceName!]);

                            using Process restartCommand = Process.Start(startInfo)!;
                            await restartCommand.WaitForExitAsync().ConfigureAwait(false);
                            if (restartCommand.ExitCode is not 0 and var exitCode) {
                                throw new ApplicationException($"Restarting service failed with exit code {exitCode}");
                            }
                        } catch (Exception e) {
                            logger.LogError(e, "Failed to restart service process, killing this process with exit code 1 to force it to be restarted");
                            try {
                                Environment.Exit(1);
                            } catch (SecurityException e2) {
                                logger.LogError(e2, "Failed to exit current process after service restart also failed");
                            }
                        }
                        break;
                    }
                    default:
                        break;
                }
            }
        } catch (Exception e) when (e is not OutOfMemoryException) {
            logger.LogError(e, $"Uncaught exception in {nameof(OnRuntimeFileDeleted)}");
        }
    }

    private void StopListening() {
        if (fileSystemWatcher != null) {
            fileSystemWatcher.Deleted -= OnRuntimeFileDeleted;
            fileSystemWatcher.Dispose();
            fileSystemWatcher = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        StopListening();
    }

    private int? StartNewProcessForCurrentProgram() {
        try {
            ProcessStartInfo processStartInfo = new(PROCESS_PATH!, COMMAND_LINE_ARGS.Skip(1)) {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute  = false
            };
            if (!IS_WINDOWS) {
                processStartInfo.Environment[IGNORE_HANGUP] = true.ToString();
            }
            using Process? newProcess = Process.Start(processStartInfo);
            if (newProcess != null) {
                return newProcess.Id;
            } else {
                onForkException(null);
            }
        } catch (Win32Exception e) {
            onForkException(e);
        } catch (PlatformNotSupportedException e) {
            onForkException(e);
        } catch (NotSupportedException e) {
            onForkException(e);
        } catch (IOException e) {
            onForkException(e);
        } catch (SecurityException e) {
            onForkException(e);
        }

        return null;

        void onForkException(Exception? e) => logger.LogError(e, "Failed to restart current process");
    }

    private void WarnAboutServerHostingBundleIfNecessary() {
        warnAboutServerHostingBundle ??= IS_WINDOWS ? logger.IsEnabled(LogLevel.Warning) ? !isRunningInIIS() && isAspNetCoreApp() && !isServerHostingBundleInstalled() : null : false;

        if (warnAboutServerHostingBundle == true && logger is not NullLogger<RuntimeUpgradeNotifier>) {
            warnAboutServerHostingBundle = false;
            logger.LogWarning("This framework-dependent ASP.NET Core app is running without IIS, but the Hosting Bundle is not installed. This can lead to the app being killed and not restarted " +
                "while upgrading the .NET runtimes to newer versions. To avoid this problem, you can install the Hosting Bundle from https://dotnet.microsoft.com/download.");
        }

        static bool isAspNetCoreApp() => (AppDomain.CurrentDomain.GetData("APP_CONTEXT_DEPS_FILES") as string)?
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Any(static filePath => "Microsoft.AspNetCore.App.deps.json".Equals(Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase)) ?? false;

        static bool isRunningInIIS() => Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\inetsrv\w3wp.exe").Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

        static bool isServerHostingBundleInstalled() {
            string expectedDisplayNamePrefix = $"Microsoft .NET {Environment.Version.Major}.{Environment.Version.Minor}.";
            return ((IEnumerable<string>) [
                    @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
                ])
                .SelectMany(getDisplayName)
                .Any(displayName => displayName != null
                    && displayName.StartsWith(expectedDisplayNamePrefix, StringComparison.Ordinal)
                    && displayName.EndsWith(" - Windows Server Hosting", StringComparison.Ordinal));

            static IEnumerable<string?> getDisplayName(string uninstallKeyPath) {
                using RegistryKey? uninstallKey         = Registry.LocalMachine.OpenSubKey(uninstallKeyPath, false);
                string[]           uninstallSubKeyNames;
                try {
                    uninstallSubKeyNames = uninstallKey?.GetSubKeyNames() ?? [];
                } catch (IOException) {
                    // leave uninstallSubKeyNames null
                    yield break;
                }

                foreach (string uninstallSubKeyName in uninstallSubKeyNames) {
                    using RegistryKey? uninstallSubKey = uninstallKey?.OpenSubKey(uninstallSubKeyName, false);
                    string?            displayName     = null;
                    try {
                        displayName = uninstallSubKey?.GetValue("DisplayName", null) as string;
                    } catch (IOException) {
                        //leave displayName null
                    }
                    yield return displayName;
                }
            }
        }

    }

}