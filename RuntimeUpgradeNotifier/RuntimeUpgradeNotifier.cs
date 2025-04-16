using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeUpgrade.Notifier.Data;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace RuntimeUpgrade.Notifier;

/// <summary>
/// <para>Register for notifications when the .NET runtime running the current process is upgraded while this program is still running.</para>
/// <para> </para>
/// <para>For example, Windows Update automatically installs .NET updates on Windows Client operating systems by default, and can be configured to do so on Windows Server OSes as well using <c>HKLM\SOFTWARE\Microsoft\.NET "AllowAUOnServerOS"=dword:1</c>.</para>
/// <para>This library is useful if you have a long-running .NET background process and Windows Update automatically installs a new patch version of the .NET Runtime that your process is using. In some cases, MSI restarts or kills your process automatically, but in other cases it leaves your process running. Your process may not crash immediately, but if it later tries to load a BCL assembly, the file will have already been deleted by the upgrade because the versioned paths changed. This results in a <see cref="FileNotFoundException"/> in your process, likely crashing it, possibly hours after the initial upgrade.</para>
/// <para> </para>
/// <para>This library allows you to use the notifications when runtimes are upgraded to take action, like restarting your process or calling custom code (like <c>Restart-Service</c> or <c>systemctl restart</c>).</para>
/// </summary>
public class RuntimeUpgradeNotifier: IDisposable {

    private const string IgnoreHangup = "RUNTIMEUPGRADENOTIFIER_NOHUP";

    private static readonly string OldRuntimeVersion = Environment.Version.ToString(3);

    private readonly object _eventLock              = new();
    private readonly string _watchedRuntimeFilename = Environment.OSVersion.Platform == PlatformID.Win32NT ? "coreclr.dll" : "libcoreclr.so";

    private event EventHandler<EventArgs>? BeforeRuntimeUpgradedInternal;
    private event EventHandler<RuntimeUpgradeEventArgs>? RuntimeUpgradedInternal;

    private RestartStrategy    _restartStrategy = RestartStrategy.Manual;
    private int                _subscriberCount;
    private FileSystemWatcher? _fileSystemWatcher;
    private string?            _watchedRuntimeDirectory;
    private string?            _systemdServiceName;

    private ILogger<RuntimeUpgradeNotifier> _logger = NullLogger<RuntimeUpgradeNotifier>.Instance;

    public ILoggerFactory LoggerFactory {
        set => _logger = value.CreateLogger<RuntimeUpgradeNotifier>();
    }

    static RuntimeUpgradeNotifier() {
        if (Environment.OSVersion.Platform == PlatformID.Unix && Environment.GetEnvironmentVariable(IgnoreHangup)?.ToLowerInvariant() is "1" or "true") {
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, signal => { signal.Cancel = true; });
        }
    }

    /// <summary>
    /// <para>Specify if this library should take any automatic action when the .NET runtime is upgraded. By default, this is <see cref="RestartStrategy.Manual"/>, and it does nothing besides fire the <see cref="RuntimeUpgraded"/> event.</para>
    /// <para>If you set this to <see cref="RestartStrategy.AutoStartNewProcess"/>, it will automatically start a new copy of this process when the .NET runtime is upgraded. Next, it will fire a <see cref="RuntimeUpgraded"/> event with the <see cref="RuntimeUpgradeEventArgs.NewProcessId"/>, at which point you should exit the current instance of this process.</para>
    /// <para>If you set this to <see cref="RestartStrategy.AutoRestartProcess"/>, it will automatically start a new copy of this process when the .NET runtime is upgraded and also automatically exit the current instance of this process. You can change the exit code from the default value of <c>1</c> using <see cref="RuntimeUpgradeEventArgs.CurrentProcessExitCode"/> in an event handler for the <see cref="RuntimeUpgraded"/> event, or you can abort the exit by setting it to <c>null</c> or changing <see cref="RestartStrategy"/> to <see cref="RestartStrategy.AutoStartNewProcess"/>. This uses <see cref="Environment.Exit"/>, but you may instead switch to <see cref="RestartStrategy.AutoStartNewProcess"/> if you want to use a more specialized exit technique like Forms' <c>Application.Exit()</c>, WPF's <c>Application.Current.Shutdown()</c>, or the .NET Host's <c>IHostApplicationLifetime.StopApplication()</c>.</para>
    /// </summary>
    public RestartStrategy RestartStrategy {
        get => _restartStrategy;
        set {
            if (_restartStrategy != value) {
                lock (_eventLock) {
                    _subscriberCount += value switch {
                        not RestartStrategy.Manual when _restartStrategy is RestartStrategy.Manual => 1,
                        RestartStrategy.Manual when _restartStrategy is not RestartStrategy.Manual => -1,
                        _                                                                          => 0
                    };
                    _restartStrategy = value;

                    _logger.LogTrace("Changed restart strategy to {strat}, new subscriber count is {subs}", _restartStrategy, _subscriberCount);

                    if (value != RestartStrategy.Manual && _subscriberCount == 1) {
                        StartListening();
                    } else if (value == RestartStrategy.Manual && _subscriberCount == 0) {
                        StopListening();
                    }
                }

                if (value == RestartStrategy.AutoRestartSystemdService && _systemdServiceName == null) {
                    _logger.LogTrace("Getting systemd service name");
                    using Process ps = Process.Start(new ProcessStartInfo("/usr/bin/ps", ["-o", "unit=", Environment.ProcessId.ToString()]) { RedirectStandardOutput = true })!;
                    ps.WaitForExit();
                    _systemdServiceName = ps.StandardOutput.ReadToEnd().Trim();
                    _logger.LogTrace("This process is currently running as the systemd service {name}", _systemdServiceName);
                }
            }
        }
    }

    /// <summary>
    /// Used to stop the current process when the .NET runtime is upgraded and <see cref="RestartStrategy"/> is <see cref="RestartStrategy.AutoRestartProcess"/>.
    /// </summary>
    public ShutdownStrategy ShutdownStrategy { get; set; } = new EnvironmentExit();

    /// <summary>
    /// <para>Event fired when the .NET Runtime that is running the current process is upgraded to a new version, and the old .NET version is uninstalled.</para>
    /// </summary>
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
                .FirstOrDefault(module => module.ModuleName.Equals(_watchedRuntimeFilename, StringComparison.OrdinalIgnoreCase))?.FileName) ?? string.Empty;

            if (_watchedRuntimeDirectory != string.Empty) {
                _fileSystemWatcher         =  new FileSystemWatcher(_watchedRuntimeDirectory, _watchedRuntimeFilename) { EnableRaisingEvents = true, IncludeSubdirectories = false };
                _fileSystemWatcher.Deleted += OnRuntimeFileDeletedAsync;
                _logger.LogTrace("Watching for deletion of {path}", Path.Combine(_watchedRuntimeDirectory, _watchedRuntimeFilename));
                _logger.LogInformation("Monitoring .NET {runtimeVer} Runtime for upgrades", OldRuntimeVersion);
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
                RestartStrategy.Manual                    => "not doing anything besides firing events",
                RestartStrategy.AutoStartNewProcess       => "starting a new process for this program but not killing the old process",
                RestartStrategy.AutoRestartProcess        => "starting a new process for this program and killing the old process",
                RestartStrategy.AutoRestartSystemdService => "requesting a service restart from systemd",
                _                                         => "unsupported restart strategy"
            });

            if (BeforeRuntimeUpgradedInternal is { } beforePublisher) {
                beforePublisher.Invoke(null, EventArgs.Empty);
            }

            RuntimeUpgradeEventArgs eventArgs = new();

            if (RestartStrategy is RestartStrategy.AutoRestartProcess or RestartStrategy.AutoStartNewProcess) {
                _logger.LogTrace("Starting new process of this program");
                eventArgs.NewProcessId = StartNewProcessForCurrentProgram();
            }

            if (RuntimeUpgradedInternal is { } publisher) {
                publisher.Invoke(null, eventArgs);
            }

            switch (RestartStrategy) {
                case RestartStrategy.AutoRestartProcess:
                    try {
                        _logger.LogTrace("Stopping old process");
                        ShutdownStrategy.StopCurrentProcess();
                    } catch (SecurityException e) {
                        _logger.LogError(e, "Failed to exit current process");
                    }
                    break;
                case RestartStrategy.AutoRestartSystemdService: {
                    _logger.LogTrace("Restarting systemd service {serviceName}", _systemdServiceName);
                    try {
                        using Process systemctl = Process.Start("/usr/bin/systemctl", ["restart", _systemdServiceName!]);
                        systemctl.WaitForExit();
                    } catch (Exception e) when (e is not OutOfMemoryException) {
                        _logger.LogError(e, "Failed to restart systemd process, killing this process with exit code 1 to force systemd to restart it");
                        Environment.Exit(1);
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

    public void Dispose() => StopListening();

    private int? StartNewProcessForCurrentProgram() {
        try {
            using Process? newProcess = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Environment.GetCommandLineArgs().Skip(1)) {
                WorkingDirectory = Environment.CurrentDirectory,
                Environment = {
                    [IgnoreHangup] = true.ToString()
                },
                UseShellExecute = false
            });
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