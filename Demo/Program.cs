using RuntimeUpgrade.Notifier;
using RuntimeUpgrade.Notifier.Data;
using System.Windows.Forms;

MessageBox.Show($"""
                 Running with .NET {Environment.Version.ToString(3)}

                 Process path: {Environment.ProcessPath}
                 Arguments: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}
                 """, "RuntimeUpgradeNotifier Demo", MessageBoxButtons.OK, MessageBoxIcon.Information);

using IRuntimeUpgradeNotifier runtimeUpgradeNotifier = new RuntimeUpgradeNotifier();

runtimeUpgradeNotifier.RestartStrategy = RestartStrategy.AutoStartNewProcess;
runtimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    MessageBox.Show($"Runtime upgraded, restarted this program with PID {evt.NewProcessId ?? null} and exiting this process",
        "RuntimeUpgradeNotifier Demo", MessageBoxButtons.OK, MessageBoxIcon.Information);
};

TaskCompletionSource exiter = new();
await exiter.Task;