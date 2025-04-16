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
    /// <para>You should exit the current instance of this process when you receive this event, because if you don't there will be two instances of your process running.</para>
    /// </summary>
    AutoStartNewProcess,

    /// <summary>
    /// <para>Starts a new instance of the current process, with the same arguments, working directory, and environment variables.</para>
    /// <para>Next, it fires a <see cref="RuntimeUpgradeNotifier.RuntimeUpgraded"/> event with the new process' ID in <see cref="RuntimeUpgradeEventArgs.NewProcessId"/>.</para>
    /// <para>Finally, it automatically exits the current process with the status code specified in <see cref="RuntimeUpgradeEventArgs.CurrentProcessExitCode"/>, which you can change to a different number, or to <c>null</c> to leave the current process running.</para>
    /// <para>This uses <see cref="Environment.Exit"/>, but you may instead switch to <see cref="RestartStrategy.AutoStartNewProcess"/> if you want to use a more specialized exit technique like Forms' <c>Application.Exit()</c>, WPF's <c>Application.Current.Shutdown()</c>, or the .NET Host's <c>IHostApplicationLifetime.StopApplication()</c>.</para>
    /// </summary>
    AutoRestartProcess,

    /// <summary>
    /// <para>Gets the service unit that the current process is running as, and tells systemd to restart it.</para>
    /// <para>Useful with <c>Microsoft.Extensions.Hosting.Systemd</c>.</para>
    /// </summary>
    AutoRestartSystemdService

}