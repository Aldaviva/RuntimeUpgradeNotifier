namespace RuntimeUpgrade.Notifier.Data;

/// <summary>
/// What action <see cref="RuntimeUpgradeNotifier"/> should take when the .NET Runtime that is running the current process gets upgraded to a new version.
/// </summary>
public enum RestartStrategy {

    /// <summary>
    /// The default behavior; does nothing besides firing the <see cref="RuntimeUpgradeNotifier.RuntimeUpgraded"/> event.
    /// </summary>
    Manual,

    /// <summary>
    /// <para>Starts a new instance of the current process, with the same arguments, working directory, and environment variables, before firing the <see cref="RuntimeUpgradeNotifier.RuntimeUpgraded"/> event with the new process' PID in <see cref="RuntimeUpgradeEventArgs.NewProcessId"/>.</para>
    /// <para>You should exit the current instance of this process when you receive this event, because if you don't there will be two instances of your process running. Does not use <see cref="RuntimeUpgradeNotifier.ExitStrategy"/>.</para>
    /// </summary>
    AutoStartNewProcess,

    /// <summary>
    /// <para>Starts a new instance of the current process, with the same arguments, working directory, and environment variables.</para>
    /// <para>Next, it fires a <see cref="RuntimeUpgradeNotifier.RuntimeUpgraded"/> event with the new process' ID in <see cref="RuntimeUpgradeEventArgs.NewProcessId"/>.</para>
    /// <para>Finally, it automatically exits the current process using the strategy specified by <see cref="RuntimeUpgradeNotifier.ExitStrategy"/>.</para>
    /// </summary>
    AutoRestartProcess,

    /// <summary>
    /// Exit the current process without starting a new instance first. Useful if an external watchdog will restart it. Control how this process exits, including its exit code, with <see cref="RuntimeUpgradeNotifier.ExitStrategy"/>.
    /// </summary>
    AutoStopProcess,

    /// <summary>
    /// <para>Gets the service name that the current process is running as, and tells systemd or Windows to restart it.</para>
    /// <para>Useful with <c>Microsoft.Extensions.Hosting.Systemd</c> and <c>Microsoft.Extensions.Hosting.WindowsServices</c>.</para>
    /// <para>Does not use <see cref="IRuntimeUpgradeNotifier.ExitStrategy"/>.</para>
    /// </summary>
    AutoRestartService

}