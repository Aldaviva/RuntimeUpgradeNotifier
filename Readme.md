RuntimeUpgradeNotifier
===

*Receive notifications when the .NET Runtime running your process gets upgraded to a new version, so you can restart your process and avoid crashing later.*

<!-- MarkdownTOC autolink="true" bracket="round" autoanchor="false" levels="1,2,3" -->

- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
    - [Automatically restart process](#automatically-restart-process)
    - [Automatically start new process and manually exit old process](#automatically-start-new-process-and-manually-exit-old-process)
    - [Manually restart process \(notify only\)](#manually-restart-process-notify-only)

<!-- /MarkdownTOC -->

## Requirements
- .NET Runtime 8 or later

## Installation

```sh
dotnet add package RuntimeUpgradeNotifier
```

## Usage
By default, this library will only notify you when the .NET Runtime is upgraded instead of starting or stopping any processes, but you can choose to have it automatically start or stop your process as well.

### Automatically restart process
When the .NET Runtime for your process is upgraded, this will start a new instance of your program with the same command-line arguments, environment variables, and working directory as the current process. It will then exit the old process with status code 1.

```cs
using RuntimeUpgrade.Notifier;
using RuntimeUpgrade.Notifier.Data;

RuntimeUpgradeNotifier.RestartBehavior = RestartBehavior.AutoRestartProcess;

RuntimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    Console.WriteLine($"Restarting process with PID {evt.NewProcessId}");
    return ValueTask.CompletedTask;
};
```

#### Custom exit code
By default, the current process will exit with status code 0 as soon as the `RuntimeUpgraded` event handler returns. You can change this value with `CurrentProcessExitCode`.

```
RuntimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    evt.CurrentProcessExitCode = 1;
    return ValueTask.CompletedTask;
};
```

### Automatically start new process and manually exit old process

This allows you to shut down your application more gracefully than `Environment.Exit()` can.

```cs
RuntimeUpgradeNotifier.RestartBehavior = RestartBehavior.AutoStartNewProcess;
RuntimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    /*       WPF: */ Application.Current.Shutdown();
    /*     Forms: */ Application.Exit();
    /* .NET Host: */ hostApplicationLifetime.StopApplication();

    return ValueTask.CompletedTask;
};
```

### Manually restart process (notify only)

This grants you full customization of the shutdown and restart of your program. For example, you can help your process resume its execution by transferring state using  extra command-line arguments, environment variables, IPC messages, or any other signaling technique.

```cs
RuntimeUpgradeNotifier.RestartBehavior = RestartBehavior.Manual; //default
RuntimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    Process.Start(Environment.ProcessPath, Environment.GetCommandLineArgs().Skip(1).Concat(["--restarted-after-runtime-upgrade"]));
    
    Application.Current.Shutdown();
};
```

#### Service restart
If you have a background service or daemon, you can configure Windows or systemd to automatically restart your program when it exits with a non-zero status code.
- **Windows:**
    - GUI: `services.msc` → service properties → Recovery → Choose `Restart the service` for "First failure", "Second failure", and "Subsequent failures"
    - CLI: `sc failure MyService actions= restart/0/restart/0/restart/0 reset= 86400`
- [**systemd:**](https://superuser.com/a/1530041/339084)
    ```ini
    [Service]
    Restart=on-failure
    ```
