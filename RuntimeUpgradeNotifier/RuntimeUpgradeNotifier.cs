using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public class RuntimeUpgradeNotifier: IRuntimeUpgradeNotifier {

    private const string IgnoreHangup = "RUNTIMEUPGRADENOTIFIER_NOHUP";

    private static readonly string   OldRuntimeVersion      = Environment.Version.ToString(3);
    private static readonly string?  ProcessPath            = Environment.ProcessPath;
    private static readonly string[] CommandLineArgs        = Environment.GetCommandLineArgs();
    private static readonly bool     Windows                = Environment.OSVersion.Platform == PlatformID.Win32NT;
    private static readonly string   WatchedRuntimeFilename = Windows ? "coreclr.dll" : "libcoreclr.so";
    private static readonly string   PowershellPath         = Windows ? Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe") : string.Empty;

    private event EventHandler<EventArgs>? BeforeRuntimeUpgradedInternal;
    private event EventHandler<RuntimeUpgradeEventArgs>? RuntimeUpgradedInternal;

    private readonly object _eventLock = new();

    private int                _subscriberCount;
    private FileSystemWatcher? _fileSystemWatcher;
    private string?            _watchedRuntimeDirectory;
    private string?            _serviceName;

    private ILogger<RuntimeUpgradeNotifier> _logger = NullLogger<RuntimeUpgradeNotifier>.Instance;

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory {
        set => _logger = value.CreateLogger<RuntimeUpgradeNotifier>();
    }

    /// <exception cref="ApplicationException">A Windows program is built without a Windows-specific TFM like <c>net8.0-windows</c></exception>
    static RuntimeUpgradeNotifier() {
        try {
            if (!Windows && Environment.GetEnvironmentVariable(IgnoreHangup)?.ToLowerInvariant() is "1" or "true") {
                PosixSignalRegistration.Create(PosixSignal.SIGHUP, signal => signal.Cancel = true);
            }

            // Eagerly load dynamic libraries that will be required later, because they will get deleted during an upgrade. This prevents "FileNotFoundException: Could not load file or assembly" errors.
            _ = new ProcessStartInfo();
            _ = Environment.CurrentDirectory;
            new AnonymousPipeServerStream().Dispose(); // Process.Start needs System.IO.Pipes to be loaded
        } catch (SecurityException) {}
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
                        if (Windows) {
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
                        _logger.LogWarning("This process is not currently running as a service, falling back from {oldStrat} to {newStrat} if it needs to be restarted",
                            nameof(RestartStrategy.AutoRestartService), nameof(RestartStrategy.AutoRestartProcess));
                        field = RestartStrategy.AutoRestartProcess;
                    }
                }
            }
        }
    } = RestartStrategy.Manual;

    /// <inheritdoc />
    public ExitStrategy ExitStrategy { get; set; } = new EnvironmentExit(null);

    /// <inheritdoc />
    public event EventHandler<RuntimeUpgradeEventArgs>? RuntimeUpgraded {
        add {
            lock (_eventLock) {
                RuntimeUpgradedInternal += value;
                if (++_subscriberCount == 1) {
                    StartListening();
                }
            }
        }
        remove {
            lock (_eventLock) {
                RuntimeUpgradedInternal -= value;
                if (--_subscriberCount == 0) {
                    StopListening();
                }
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<EventArgs>? BeforeRuntimeUpgraded {
        add {
            lock (_eventLock) {
                BeforeRuntimeUpgradedInternal += value;
                if (++_subscriberCount == 1) {
                    StartListening();
                }
            }
        }
        remove {
            lock (_eventLock) {
                BeforeRuntimeUpgradedInternal -= value;
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
                .FirstOrDefault(module => module.ModuleName.Equals(WatchedRuntimeFilename, StringComparison.OrdinalIgnoreCase))?.FileName) ?? string.Empty;

            if (_watchedRuntimeDirectory != string.Empty) {
                _fileSystemWatcher         =  new FileSystemWatcher(_watchedRuntimeDirectory, WatchedRuntimeFilename) { EnableRaisingEvents = true, IncludeSubdirectories = false };
                _fileSystemWatcher.Deleted += OnRuntimeFileDeletedAsync;
                _logger.LogTrace("Watching for deletion of {path}", Path.Combine(_watchedRuntimeDirectory, WatchedRuntimeFilename));
                _logger.LogDebug("Monitoring .NET {runtimeVer} Runtime for upgrades", OldRuntimeVersion);
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

    private void OnRuntimeFileDeletedAsync(object sender, FileSystemEventArgs evt) {
        if ((evt.ChangeType & WatcherChangeTypes.Deleted) != 0) {
            _logger.LogInformation(".NET {oldVer} Runtime was upgraded, {action}", OldRuntimeVersion, RestartStrategy switch {
                RestartStrategy.Manual              => "not doing anything besides firing events",
                RestartStrategy.AutoStartNewProcess => "starting a new process for this program but not killing the old process",
                RestartStrategy.AutoRestartProcess  => "starting a new process for this program and killing the old process",
                RestartStrategy.AutoRestartService  => "requesting a service restart from the operating system",
                RestartStrategy.AutoStopProcess     => "stopping this program but not starting a new process",
                _                                   => "unsupported restart strategy"
            });

            BeforeRuntimeUpgradedInternal?.Invoke(this, EventArgs.Empty);

            RuntimeUpgradeEventArgs eventArgs = new();

            if (RestartStrategy is RestartStrategy.AutoRestartProcess or RestartStrategy.AutoStartNewProcess) {
                _logger.LogTrace("Starting new process of this program");
                eventArgs.NewProcessId = StartNewProcessForCurrentProgram();
            }

            RuntimeUpgradedInternal?.Invoke(this, eventArgs);

            switch (RestartStrategy) {
                case RestartStrategy.AutoRestartProcess:
                case RestartStrategy.AutoStopProcess:
                    try {
                        _logger.LogTrace("Stopping old process");
                        ExitStrategy.StopCurrentProcess();
                    } catch (SecurityException e) {
                        _logger.LogError(e, "Failed to exit current process");
                    }
                    break;
                case RestartStrategy.AutoRestartService: {
                    _logger.LogTrace("Restarting service {serviceName}", _serviceName);
                    try {
                        ProcessStartInfo startInfo = Windows
                            ? new ProcessStartInfo(PowershellPath, ["-Command", "Restart-Service", "-Name", _serviceName!])
                            : new ProcessStartInfo("/usr/bin/systemctl", ["restart", _serviceName!]);

                        using Process restartCommand = Process.Start(startInfo)!;
                        restartCommand.WaitForExit();
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
        } else {
            _logger.LogWarning("Ignoring event {changeType} on {name}", evt.ChangeType, evt.Name);
        }
    }

    private void StopListening() {
        if (_fileSystemWatcher != null) {
            _fileSystemWatcher.Deleted -= OnRuntimeFileDeletedAsync;
            _fileSystemWatcher.Dispose();
            _fileSystemWatcher = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        StopListening();
        GC.SuppressFinalize(this);
    }

    private int? StartNewProcessForCurrentProgram() {
        try {
            ProcessStartInfo processStartInfo = new(ProcessPath!, CommandLineArgs.Skip(1)) {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute  = false
            };
            if (!Windows) {
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

}