using Microsoft.Extensions.Logging;
using RuntimeUpgrade.Notifier.Data;

namespace RuntimeUpgrade.Notifier;

/// <summary>
/// <para>Register for notifications when the .NET runtime running the current process is upgraded while this program is still running.</para>
/// <para> </para>
/// <para>For example, Windows Update automatically installs .NET updates on Windows Client operating systems by default, and can be configured to do so on Windows Server OSes as well using <c>HKLM\SOFTWARE\Microsoft\.NET "AllowAUOnServerOS"=dword:1</c>.</para>
/// <para>This library is useful if you have a long-running .NET background process and Windows Update automatically installs a new patch version of the .NET Runtime that your process is using. In some cases, MSI restarts or kills your process automatically, but in other cases it leaves your process running. Your process may not crash immediately, but if it later tries to load a BCL assembly, the file will have already been deleted by the upgrade because the versioned paths changed. This results in a <see cref="FileNotFoundException"/> in your process, likely crashing it, possibly hours after the initial upgrade.</para>
/// <para> </para>
/// <para>This library allows you to use the notifications when runtimes are upgraded to take action, like restarting your process or calling custom code (like <c>Restart-Service</c> or <c>systemctl restart</c>).</para>
/// </summary>
public interface IRuntimeUpgradeNotifier: IDisposable {

    /// <summary>
    /// Microsoft logger factory if you want this library to log messages. By default, it does not log anything.
    /// </summary>
    ILoggerFactory LoggerFactory { set; }

    /// <summary>
    /// <para>Specify if this library should take any automatic action when the .NET runtime is upgraded. By default, this is <see cref="Data.RestartStrategy.Manual"/>, and it does nothing besides fire the <see cref="RuntimeUpgraded"/> event.</para>
    /// <para>See all of the <seealso cref="Data.RestartStrategy"/> enum values for descriptions of each option.</para>
    /// </summary>
    RestartStrategy RestartStrategy { get; set; }

#pragma warning disable CS1574
    /// <summary>
    /// <para>Used to stop the current process when the .NET runtime is upgraded and <see cref="RestartStrategy"/> is <see cref="RestartStrategy.AutoRestartProcess"/> or <see cref="RestartStrategy.AutoStopProcess"/>. By default, it calls <see cref="Environment.Exit"/>.</para>
    /// <para>Provided implementations:
    /// <list type="bullet">
    /// <item><description><see cref="EnvironmentExit"/></description></item>
    /// <item><description><see cref="HostedLifetimeExit"/></description></item>
    /// <item><description><see cref="CancellationTokenExit"/></description></item>
    /// <item><description><see cref="SemaphoreExit"/></description></item>
    /// <item><description><see cref="TaskExit"/></description></item>
    /// <item><description><see cref="DelegateExit"/></description></item>
    /// <item><description><see cref="FormsApplicationExit"/> (Windows only)</description></item>
    /// <item><description><see cref="WpfApplicationExit"/> (Windows only)</description></item>
    /// </list></para>
    /// <para>You can also define custom behavior to shut down your program by implementing the <see cref="ExitStrategy"/> interface.</para>
    /// </summary>
    ExitStrategy ExitStrategy { get; set; }
#pragma warning restore CS1574

    /// <summary>
    /// <para>Minimum duration that defines how long Windows Installer (MSI) installations must not be running continuously after a runtime upgrade before triggering a notification or restarting the program.</para>
    /// <para>This attempts to solve the problem where some .NET runtimes get upgraded non-atomically. For example, Visual Studio Installer removes the old runtime, waits, and then installs the new runtime, leaving a window of time in between when no suitable runtime is installed and restarting the dependent program would lead to a crash with a "You must install .NET" error.</para>
    /// <para>The default duration is 2 minutes. Only used on Windows, and ignored on Linux.</para>
    /// </summary>
    /// <exception accessor="set" cref="ArgumentOutOfRangeException">duration is negative</exception>
    TimeSpan WindowsInstallerFinishedDebounceDuration { get; set; }

    /// <summary>
    /// <para>Event fired when the .NET Runtime that is running the current process is upgraded to a new version, and the old .NET version is uninstalled.</para>
    /// <para>Specifically, this event is fired after any new process is started, but before the current process is stopped. Note that starting and stopping processes can be configured by <see cref="RestartStrategy"/>, but this event will be fired even if the start or stop are skipped.</para>
    /// </summary>
    event AsyncEventHandler<RuntimeUpgradeEventArgs> RuntimeUpgraded;

    /// <summary>
    /// Event fired before starting the new process after the .NET Runtime has been upgraded. Note that starting a new process can be configured by <see cref="RestartStrategy"/>, but this event will be fired even if the start is skipped.
    /// </summary>
    event AsyncEventHandler BeforeRuntimeUpgraded;

}