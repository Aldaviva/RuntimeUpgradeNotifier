using RuntimeUpgrade.Notifier;
using RuntimeUpgrade.Notifier.Data;

MessageBox.Show($"""
                 Running with .NET {Environment.Version.ToString(3)}

                 Process path: {Environment.ProcessPath}
                 Arguments: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}
                 """, "RuntimeUpgradeNotifier Demo", MessageBoxButtons.OK, MessageBoxIcon.Information);

RuntimeUpgradeNotifier.RestartBehavior = RestartBehavior.AutoStartNewProcess;
RuntimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    evt.CurrentProcessExitCode = 2;
    MessageBox.Show($"Runtime upgraded, restarted this program with PID {evt.NewProcessId ?? null} and exiting this process",
        "RuntimeUpgradeNotifier Demo", MessageBoxButtons.OK, MessageBoxIcon.Information);
    return ValueTask.CompletedTask;
};

TaskCompletionSource exiter = new();
await exiter.Task;