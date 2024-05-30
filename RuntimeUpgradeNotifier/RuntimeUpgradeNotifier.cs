using RuntimeUpgrade.Notifier.Data;
using System.ComponentModel;
using System.Diagnostics;
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
public static class RuntimeUpgradeNotifier {

    private const int DefaultExitCode = 0;

    private static readonly object EventLock              = new();
    private static readonly string WatchedRuntimeFilename = Environment.OSVersion.Platform == PlatformID.Win32NT ? "coreclr.dll" : "libcoreclr.so";

    private static event AsyncEventHandler<RuntimeUpgradeEventArgs>? RuntimeUpgradedInternal;

    private static RestartBehavior    _restartBehavior = RestartBehavior.Manual;
    private static int                _subscriberCount;
    private static FileSystemWatcher? _fileSystemWatcher;
    private static string?            _watchedRuntimeDirectory;

    /// <summary>
    /// <para>Specify if this library should take any automatic action when the .NET runtime is upgraded. By default, this is <see cref="Data.RestartBehavior.Manual"/>, and it does nothing besides fire the <see cref="RuntimeUpgraded"/> event.</para>
    /// <para>If you set this to <see cref="Data.RestartBehavior.AutoStartNewProcess"/>, it will automatically start a new copy of this process when the .NET runtime is upgraded. Next, it will fire a <see cref="RuntimeUpgraded"/> event with the <see cref="RuntimeUpgradeEventArgs.NewProcessId"/>, at which point you should exit the current instance of this process.</para>
    /// <para>If you set this to <see cref="Data.RestartBehavior.AutoRestartProcess"/>, it will automatically start a new copy of this process when the .NET runtime is upgraded and also automatically exit the current instance of this process. You can change the exit code from the default value of <c>1</c> using <see cref="RuntimeUpgradeEventArgs.CurrentProcessExitCode"/> in an event handler for the <see cref="RuntimeUpgraded"/> event, or you can abort the exit by setting it to <c>null</c> or changing <see cref="RestartBehavior"/> to <see cref="Data.RestartBehavior.AutoStartNewProcess"/>. This uses <see cref="Environment.Exit"/>, but you may instead switch to <see cref="Data.RestartBehavior.AutoStartNewProcess"/> if you want to use a more specialized exit technique like Forms' <c>Application.Exit()</c>, WPF's <c>Application.Current.Shutdown()</c>, or the .NET Host's <c>IHostApplicationLifetime.StopApplication()</c>.</para>
    /// </summary>
    public static RestartBehavior RestartBehavior {
        get => _restartBehavior;
        set {
            lock (EventLock) {
                if (_restartBehavior != value) {
                    int subscriberDifference = value switch {
                        not RestartBehavior.Manual when _restartBehavior is RestartBehavior.Manual => 1,
                        RestartBehavior.Manual when _restartBehavior is not RestartBehavior.Manual => -1,
                        _                                                                          => 0
                    };
                    int newSubscriberCount = Interlocked.Add(ref _subscriberCount, subscriberDifference);
                    _restartBehavior = value;

                    if (value != RestartBehavior.Manual && newSubscriberCount == 1) {
                        StartListening();
                    } else if (value == RestartBehavior.Manual && newSubscriberCount == 0) {
                        StopListening();
                    }
                }
            }
        }
    }

    /// <summary>
    /// <para>Event fired when the .NET Runtime that is running the current process is upgraded to a new version, and the old .NET version is uninstalled.</para>
    /// </summary>
    public static event AsyncEventHandler<RuntimeUpgradeEventArgs>? RuntimeUpgraded {
        add {
            lock (EventLock) {
                int newSubscriberCount = Interlocked.Increment(ref _subscriberCount);
                RuntimeUpgradedInternal += value;
                if (newSubscriberCount == 1) {
                    StartListening();
                }
            }
        }
        remove {
            lock (EventLock) {
                RuntimeUpgradedInternal -= value;
                int newSubscriberCount = Interlocked.Decrement(ref _subscriberCount);
                if (newSubscriberCount == 0) {
                    StopListening();
                }
            }
        }
    }

    private static void StartListening() {
        try {
            using Process currentProcess = Process.GetCurrentProcess();
            _watchedRuntimeDirectory ??= Path.GetDirectoryName(currentProcess.Modules.Cast<ProcessModule>()
                .FirstOrDefault(module => module.ModuleName.Equals(WatchedRuntimeFilename, StringComparison.OrdinalIgnoreCase))?.FileName) ?? string.Empty;

            if (_watchedRuntimeDirectory != string.Empty) {
                _fileSystemWatcher         =  new FileSystemWatcher(_watchedRuntimeDirectory, WatchedRuntimeFilename) { EnableRaisingEvents = true, IncludeSubdirectories = false };
                _fileSystemWatcher.Deleted += onRuntimeFileDeletedAsync;
            }
        } catch (NotSupportedException e) {
            OnException(e);
        } catch (Win32Exception e) {
            OnException(e);
        } catch (PathTooLongException e) {
            OnException(e);
        } catch (FileNotFoundException e) {
            OnException(e);
        }

        static void OnException(Exception e) =>
            Trace.WriteLine($"Failed to list modules loaded by current process or listening for changes to that file, not notifying for runtime upgrades.\n{e.Message}\n{e.StackTrace}");
    }

    private static async void onRuntimeFileDeletedAsync(object sender, FileSystemEventArgs evt) {
        if ((evt.ChangeType & WatcherChangeTypes.Deleted) != 0) {
            RuntimeUpgradeEventArgs eventArgs = new() { CurrentProcessExitCode = RestartBehavior == RestartBehavior.AutoRestartProcess ? DefaultExitCode : null };

            if (RestartBehavior != RestartBehavior.Manual) {
                eventArgs.NewProcessId = RestartCurrentProcess();
            }

            if (RuntimeUpgradedInternal is { } publisher) {
                await publisher.Invoke(null, eventArgs).ConfigureAwait(false);
            }

            if (RestartBehavior == RestartBehavior.AutoRestartProcess && eventArgs.CurrentProcessExitCode is { } exitCode) {
                try {
                    Environment.Exit(exitCode);
                } catch (SecurityException e) {
                    Trace.WriteLine($"Failed to exit current process: {e.Message}");
                }
            }
        }
    }

    private static void StopListening() {
        if (_fileSystemWatcher != null) {
            _fileSystemWatcher.Deleted -= onRuntimeFileDeletedAsync;
            _fileSystemWatcher.Dispose();
            _fileSystemWatcher = null;
        }
    }

    private static int? RestartCurrentProcess() {
        try {
            using Process? newProcess = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Environment.GetCommandLineArgs().Skip(1)) { WorkingDirectory = Environment.CurrentDirectory });
            return newProcess?.Id;
        } catch (Win32Exception e) {
            OnException(e);
        } catch (PlatformNotSupportedException e) {
            OnException(e);
        } catch (NotSupportedException e) {
            OnException(e);
        } catch (IOException e) {
            OnException(e);
        } catch (SecurityException e) {
            OnException(e);
        }

        return null;

        static void OnException(Exception e) => Trace.WriteLine($"Failed to restart current process\n{e.Message}\n{e.StackTrace}");
    }

}