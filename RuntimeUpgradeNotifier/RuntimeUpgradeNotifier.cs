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

    private const string IgnoreHangup = "RUNTIMEUPGRADENOTIFIER_NOHUP";

    private static readonly string   OldRuntimeVersion      = Environment.Version.ToString(3);
    private static readonly string?  ProcessPath            = Environment.ProcessPath;
    private static readonly string[] CommandLineArgs        = Environment.GetCommandLineArgs();
    private static readonly bool     IsWindows              = Environment.OSVersion.Platform == PlatformID.Win32NT;
    private static readonly string   WatchedRuntimeFilename = IsWindows ? "coreclr.dll" : "libcoreclr.so";
    private static readonly string   PowershellPath         = IsWindows ? Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe") : string.Empty;

    private readonly object                                                  _eventLock                      = new();
    private readonly ICollection<AsyncEventHandler>                          _beforeRuntimeUpgradedCallbacks = [];
    private readonly ICollection<AsyncEventHandler<RuntimeUpgradeEventArgs>> _runtimeUpgradedCallbacks       = [];

    private int                             _subscriberCount;
    private FileSystemWatcher?              _fileSystemWatcher;
    private string?                         _watchedRuntimeDirectory;
    private string?                         _serviceName;
    private ILogger<RuntimeUpgradeNotifier> _logger = NullLogger<RuntimeUpgradeNotifier>.Instance;
    private bool?                           _warnAboutServerHostingBundle;

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory {
        set {
            _logger = value.CreateLogger<RuntimeUpgradeNotifier>();
            WarnAboutServerHostingBundleIfNecessary();
        }
    }

    static RuntimeUpgradeNotifier() {
        try {
            if (!IsWindows && Environment.GetEnvironmentVariable(IgnoreHangup)?.ToLowerInvariant() is "1" or "true") {
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
                lock (_eventLock) {
                    _subscriberCount += value switch {
                        not RestartStrategy.Manual when field is RestartStrategy.Manual => 1,
                        RestartStrategy.Manual when field is not RestartStrategy.Manual => -1,
                        _                                                               => 0
                    };
                    field = value;

                    _logger.LogTrace("Changed restart strategy to {strat}, new subscriber count is {subs}", field, _subscriberCount);

                    if (value != RestartStrategy.Manual && _subscriberCount == 1) {
                        StartListening();
                    } else if (value == RestartStrategy.Manual && _subscriberCount == 0) {
                        StopListening();
                    }
                }

                if (value == RestartStrategy.AutoRestartService && _serviceName == null) {
                    _logger.LogTrace("Getting service name");
                    int selfPid = Environment.ProcessId;

                    try {
                        if (IsWindows) {
                            using ManagementObjectSearcher   wmiSearch  = new(new SelectQuery("Win32_Service", $"ProcessId = {selfPid}", ["Name"]));
                            using ManagementObjectCollection wmiResults = wmiSearch.Get();
                            using ManagementObject?          wmiResult  = wmiResults.Cast<ManagementObject>().FirstOrDefault();
                            _serviceName = (string?) wmiResult?["Name"];
                        } else {
                            using Process ps = Process.Start(new ProcessStartInfo("/usr/bin/ps", ["-o", "unit=", selfPid.ToString()]) { RedirectStandardOutput = true })!;
                            ps.WaitForExit();
                            _serviceName = ps.ExitCode == 0 ? ps.StandardOutput.ReadToEnd().Trim() : null;
                        }
                    } catch (Win32Exception e) {
                        _logger.LogError(e, "Failed to get service name of current process");
                    } catch (SystemException e) {
                        _logger.LogError(e, "Failed to get service name of current process");
                    }

                    if (_serviceName != null) {
                        _logger.LogTrace("This process is currently running as the service {name}", _serviceName);
                    } else {
                        _logger.LogDebug("This process is not currently running as a service, falling back from {oldStrat} to {newStrat} if it needs to be restarted",
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
            lock (_eventLock) {
                _runtimeUpgradedCallbacks.Add(value);
                if (++_subscriberCount == 1) {
                    StartListening();
                }
            }
        }
        remove {
            lock (_eventLock) {
                _runtimeUpgradedCallbacks.Remove(value);
                if (--_subscriberCount == 0) {
                    StopListening();
                }
            }
        }
    }

    /// <inheritdoc />
    public event AsyncEventHandler BeforeRuntimeUpgraded {
        add {
            lock (_eventLock) {
                _beforeRuntimeUpgradedCallbacks.Add(value);
                if (++_subscriberCount == 1) {
                    StartListening();
                }
            }
        }
        remove {
            lock (_eventLock) {
                _beforeRuntimeUpgradedCallbacks.Remove(value);
                if (--_subscriberCount == 0) {
                    StopListening();
                }
            }
        }
    }

    private void StartListening() {
        try {
            using Process currentProcess = Process.GetCurrentProcess();
            _watchedRuntimeDirectory ??= Path.GetDirectoryName(currentProcess.Modules.Cast<ProcessModule>()
                .FirstOrDefault(static module => module.ModuleName.Equals(WatchedRuntimeFilename, StringComparison.OrdinalIgnoreCase))?.FileName) ?? string.Empty;

            if (_watchedRuntimeDirectory != string.Empty) {
                _fileSystemWatcher         =  new FileSystemWatcher(_watchedRuntimeDirectory, WatchedRuntimeFilename) { EnableRaisingEvents = true, IncludeSubdirectories = false };
                _fileSystemWatcher.Deleted += OnRuntimeFileDeleted;
                _logger.LogDebug("Monitoring .NET {runtimeVer} Runtime for upgrades by watching for deletion of {path}", OldRuntimeVersion,
                    Path.Combine(_watchedRuntimeDirectory, WatchedRuntimeFilename));
            } else {
                OnListeningError(null);
            }
        } catch (NotSupportedException e) {
            OnListeningError(e);
        } catch (Win32Exception e) {
            OnListeningError(e);
        } catch (PathTooLongException e) {
            OnListeningError(e);
        } catch (FileNotFoundException e) {
            OnListeningError(e);
        }

        void OnListeningError(Exception? e) => _logger.LogError(e, "Failed to list modules loaded by current process or listen for changes to that file, not notifying for runtime upgrades.");
    }

    private async void OnRuntimeFileDeleted(object sender, FileSystemEventArgs evt) {
        try {
            if ((evt.ChangeType & WatcherChangeTypes.Deleted) != 0) {
                if (IsWindows) {
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
                        _logger.LogWarning(e, "Not allowed to find Windows Installer system mutex, assuming no msiexec installation is in progress now");
                    } catch (IOException e) {
                        OnUncaughtException(e);
                    } catch (Exception e) when (e is not OutOfMemoryException) {
                        OnUncaughtException(e);
                    }

                    void OnUncaughtException(Exception e) {
                        _logger.LogError(e, "Uncaught exception while waiting for Windows Installer to finish its installation before notifying program about .NET runtime upgrade");
                    }
                }

                _logger.LogInformation(".NET {oldVer} Runtime was upgraded, {action}", OldRuntimeVersion, RestartStrategy switch {
                    RestartStrategy.Manual              => "not doing anything besides firing events",
                    RestartStrategy.AutoStartNewProcess => "starting a new process for this program but not killing the old process",
                    RestartStrategy.AutoRestartProcess  => "starting a new process for this program and killing the old process",
                    RestartStrategy.AutoRestartService  => "requesting a service restart from the operating system",
                    RestartStrategy.AutoStopProcess     => "stopping this program but not starting a new process",
                    _                                   => "unsupported restart strategy"
                });

                IEnumerable<AsyncEventHandler>                          beforeRuntimeUpgradedCallbacks;
                IEnumerable<AsyncEventHandler<RuntimeUpgradeEventArgs>> runtimeUpgradedCallbacks;
                lock (_eventLock) {
                    beforeRuntimeUpgradedCallbacks = _beforeRuntimeUpgradedCallbacks.ToList();
                    runtimeUpgradedCallbacks       = _runtimeUpgradedCallbacks.ToList();
                }

                await Task.WhenAll(beforeRuntimeUpgradedCallbacks.Select(handler => handler(this))).ConfigureAwait(false);

                RuntimeUpgradeEventArgs eventArgs = new();
                if (RestartStrategy is RestartStrategy.AutoRestartProcess or RestartStrategy.AutoStartNewProcess) {
                    _logger.LogTrace("Starting new process of this program");
                    eventArgs.NewProcessId = StartNewProcessForCurrentProgram();
                }

                await Task.WhenAll(runtimeUpgradedCallbacks.Select(handler => handler(this, eventArgs))).ConfigureAwait(false);

                switch (RestartStrategy) {
                    case RestartStrategy.AutoRestartProcess:
                    case RestartStrategy.AutoStopProcess:
                        try {
                            _logger.LogTrace("Stopping old process");
                            await ExitStrategy.StopCurrentProcess().ConfigureAwait(false);
                        } catch (SecurityException e) {
                            _logger.LogError(e, "Failed to exit current process");
                        }
                        break;
                    case RestartStrategy.AutoRestartService: {
                        _logger.LogTrace("Restarting service {serviceName}", _serviceName);
                        try {
                            ProcessStartInfo startInfo = IsWindows
                                ? new ProcessStartInfo(PowershellPath, ["-Command", "Restart-Service", "-Name", _serviceName!])
                                : new ProcessStartInfo("/usr/bin/systemctl", ["restart", _serviceName!]);

                            using Process restartCommand = Process.Start(startInfo)!;
                            await restartCommand.WaitForExitAsync().ConfigureAwait(false);
                            if (restartCommand.ExitCode is not 0 and var exitCode) {
                                throw new ApplicationException($"Restarting service failed with exit code {exitCode}");
                            }
                        } catch (Exception e) {
                            _logger.LogError(e, "Failed to restart service process, killing this process with exit code 1 to force it to be restarted");
                            try {
                                Environment.Exit(1);
                            } catch (SecurityException e2) {
                                _logger.LogError(e2, "Failed to exit current process after service restart also failed");
                            }
                        }
                        break;
                    }
                    default:
                        break;
                }
            }
        } catch (Exception e) when (e is not OutOfMemoryException) {
            _logger.LogError(e, $"Uncaught exception in {nameof(OnRuntimeFileDeleted)}");
        }
    }

    private void StopListening() {
        if (_fileSystemWatcher != null) {
            _fileSystemWatcher.Deleted -= OnRuntimeFileDeleted;
            _fileSystemWatcher.Dispose();
            _fileSystemWatcher = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        StopListening();
    }

    private int? StartNewProcessForCurrentProgram() {
        try {
            ProcessStartInfo processStartInfo = new(ProcessPath!, CommandLineArgs.Skip(1)) {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute  = false
            };
            if (!IsWindows) {
                processStartInfo.Environment[IgnoreHangup] = true.ToString();
            }
            using Process? newProcess = Process.Start(processStartInfo);
            if (newProcess != null) {
                return newProcess.Id;
            } else {
                OnForkException(null);
            }
        } catch (Win32Exception e) {
            OnForkException(e);
        } catch (PlatformNotSupportedException e) {
            OnForkException(e);
        } catch (NotSupportedException e) {
            OnForkException(e);
        } catch (IOException e) {
            OnForkException(e);
        } catch (SecurityException e) {
            OnForkException(e);
        }

        return null;

        void OnForkException(Exception? e) => _logger.LogError(e, "Failed to restart current process");
    }

    private void WarnAboutServerHostingBundleIfNecessary() {
        _warnAboutServerHostingBundle ??= IsWindows && !IsRunningInIIS() && IsAspNetCoreApp() && !IsServerHostingBundleInstalled();

        if (_logger is not NullLogger<RuntimeUpgradeNotifier> && _warnAboutServerHostingBundle is true) {
            _warnAboutServerHostingBundle = false;
            _logger.LogWarning(
                "This framework-dependent ASP.NET Core app is running without IIS, but the Hosting Bundle is not installed. This can lead to the app being killed and not restarted while upgrading the .NET runtimes to newer versions. To avoid this problem, you can install the Hosting Bundle from https://dotnet.microsoft.com/download.");
        }

        static bool IsAspNetCoreApp() => (AppDomain.CurrentDomain.GetData("APP_CONTEXT_DEPS_FILES") as string)?
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Any(static filePath => "Microsoft.AspNetCore.App.deps.json".Equals(Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase)) ?? false;

        static bool IsRunningInIIS() => Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\inetsrv\w3wp.exe").Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

        static bool IsServerHostingBundleInstalled() {
            string expectedDisplayNamePrefix = $"Microsoft .NET {Environment.Version.Major}.{Environment.Version.Minor}.";
            return ((IEnumerable<string>) [
                    @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
                ])
                .SelectMany(GetDisplayName)
                .Any(displayName => displayName is not null
                    && displayName.StartsWith(expectedDisplayNamePrefix, StringComparison.Ordinal)
                    && displayName.EndsWith(" - Windows Server Hosting", StringComparison.Ordinal));

            static IEnumerable<string?> GetDisplayName(string uninstallKeyPath) {
                using RegistryKey? uninstallKey = Registry.LocalMachine.OpenSubKey(uninstallKeyPath, false);
                foreach (string uninstallSubKeyName in uninstallKey?.GetSubKeyNames() ?? []) {
                    using RegistryKey? uninstallSubKey = uninstallKey?.OpenSubKey(uninstallSubKeyName, false);
                    yield return uninstallSubKey?.GetValue("DisplayName", null) as string;
                }
            }
        }
    }

}