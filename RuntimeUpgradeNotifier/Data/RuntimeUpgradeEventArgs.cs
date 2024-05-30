namespace RuntimeUpgrade.Notifier.Data;

/// <summary>
/// Data returned with the event that is fired when the .NET runtime is upgraded
/// </summary>
public class RuntimeUpgradeEventArgs {

    /// <summary>
    /// If <see cref="RuntimeUpgradeNotifier.RestartBehavior"/> is <see cref="RestartBehavior.AutoStartNewProcess"/> or <see cref="RestartBehavior.AutoRestartProcess"/>, this is the PID of the newly started process, otherwise, if it's <see cref="RestartBehavior.Manual"/>, this will be <c>null</c> because no new process will have been started.
    /// </summary>
    public int? NewProcessId { get; internal set; }

    /// <summary>
    /// <para>Allows you to control the behavior of <see cref="RuntimeUpgradeNotifier"/> when <see cref="RuntimeUpgradeNotifier.RestartBehavior"/> is <see cref="RestartBehavior.AutoRestartProcess"/>. In this case, <see cref="CurrentProcessExitCode"/> defaults to <c>0</c>, so the current process will exit with status code <c>0</c> when all the event handlers for <see cref="RuntimeUpgradeNotifier.RuntimeUpgraded"/> finish.</para>
    /// <para>To change the status code returned during the current process' imminent exit, set this property to another number, like <c>0</c> or <c>-1</c>.</para>
    /// <para>To prevent <see cref="RuntimeUpgradeNotifier"/> from exiting the current process despite <see cref="RuntimeUpgradeNotifier.RestartBehavior"/> previously being set to <see cref="RestartBehavior.AutoRestartProcess"/>, you can set <see cref="CurrentProcessExitCode"/> to <c>null</c>, which is equivalent to making a last-second change of <see cref="RuntimeUpgradeNotifier.RestartBehavior"/> to <see cref="RestartBehavior.AutoStartNewProcess"/>.</para>
    /// <para>This property will be set to <c>null</c> and its value will have no effect when <see cref="RuntimeUpgradeNotifier.RestartBehavior"/> is set to <see cref="RestartBehavior.Manual"/> or <see cref="RestartBehavior.AutoStartNewProcess"/>, because the current process does not exit in those cases.</para>
    /// </summary>
    public int? CurrentProcessExitCode { get; set; }

}