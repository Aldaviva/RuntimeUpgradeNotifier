namespace RuntimeUpgrade.Notifier.Data;

/// <summary>
/// Data returned with the event that is fired when the .NET runtime is upgraded
/// </summary>
public class RuntimeUpgradeEventArgs {

    /// <summary>
    /// If <see cref="RuntimeUpgradeNotifier.RestartStrategy"/> is <see cref="RestartStrategy.AutoStartNewProcess"/> or <see cref="RestartStrategy.AutoRestartProcess"/>, this is the PID of the newly started process, otherwise, this will be <c>null</c> because no new process will have been started by this program.
    /// </summary>
    public int? NewProcessId { get; internal set; }

}