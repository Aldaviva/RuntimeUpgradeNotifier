namespace RuntimeUpgrade.Notifier.Data;

/// <summary>
/// What action <see cref="IRuntimeUpgradeNotifier"/> should take when the .NET Runtime that is running the current process gets upgraded to a new version.
/// </summary>
public enum RestartStrategy {

    /// <summary>
    /// <para>The default behavior; does nothing besides firing the <see cref="IRuntimeUpgradeNotifier.BeforeRuntimeUpgraded"/> and <see cref="IRuntimeUpgradeNotifier.RuntimeUpgraded"/> events.</para>
    /// </summary>
    Manual,

    /// <summary>
    /// <para>First, it fires a <see cref="IRuntimeUpgradeNotifier.BeforeRuntimeUpgraded"/> event.</para>
    /// <para>Then, it starts a new instance of the current process, with the same arguments, working directory, and environment variables.</para>
    /// <para>Finally, it fires the <see cref="IRuntimeUpgradeNotifier.RuntimeUpgraded"/> event with the new process' PID in <see cref="RuntimeUpgradeEventArgs.NewProcessId"/>.</para>
    /// <para>You should exit the current instance of this process when you receive this event, because if you don't there will be two instances of your process running. Does not use <see cref="IRuntimeUpgradeNotifier.ExitStrategy"/>.</para>
    /// </summary>
    AutoStartNewProcess,

    /// <summary>
    /// <para>First, it fires a <see cref="IRuntimeUpgradeNotifier.BeforeRuntimeUpgraded"/> event.</para>
    /// <para>Then, it starts a new instance of the current process, with the same arguments, working directory, and environment variables.</para>
    /// <para>Next, it fires a <see cref="IRuntimeUpgradeNotifier.RuntimeUpgraded"/> event with the new process' ID in <see cref="RuntimeUpgradeEventArgs.NewProcessId"/>.</para>
    /// <para>Finally, it automatically exits the current process using the strategy specified by <see cref="IRuntimeUpgradeNotifier.ExitStrategy"/>.</para>
    /// </summary>
    AutoRestartProcess,

    /// <summary>
    /// <para>First, it fires the <see cref="IRuntimeUpgradeNotifier.BeforeRuntimeUpgraded"/> and <see cref="IRuntimeUpgradeNotifier.RuntimeUpgraded"/> events.</para>
    /// <para>Then, it exits the current process, without starting a new instance first.</para>
    /// <para>Useful if an external watchdog will restart it. Control how this process exits, including its exit code, with <see cref="IRuntimeUpgradeNotifier.ExitStrategy"/>.</para>
    /// </summary>
    AutoStopProcess,

    /// <summary>
    /// <para>First, it fires the <see cref="IRuntimeUpgradeNotifier.BeforeRuntimeUpgraded"/> and <see cref="IRuntimeUpgradeNotifier.RuntimeUpgraded"/> events.</para>
    /// <para>Then, it gets the service name that the current process is running as, and tells systemd or Windows to restart it.</para>
    /// <para>Useful with <c>Microsoft.Extensions.Hosting.Systemd</c> and <c>Microsoft.Extensions.Hosting.WindowsServices</c>. Does not use <see cref="IRuntimeUpgradeNotifier.ExitStrategy"/>.</para>
    /// </summary>
    AutoRestartService

}