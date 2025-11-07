📦 RuntimeUpgradeNotifier
===

[![NuGet](https://img.shields.io/nuget/v/RuntimeUpgradeNotifier?logo=nuget&color=informational)](https://www.nuget.org/packages/RuntimeUpgradeNotifier)

*Receive notifications when the .NET Runtime running your process gets upgraded to a new version, so you can restart your process and avoid crashing later.*

<!-- MarkdownTOC autolink="true" bracket="round" autoanchor="false" levels="1,2,3,4" -->

- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
    - [Get started](#get-started)
    - [Restart Strategy: what to do when the .NET runtime is upgraded](#restart-strategy-what-to-do-when-the-net-runtime-is-upgraded)
        - [Manual](#manual)
        - [Automatically start a new process](#automatically-start-a-new-process)
        - [Automatically restart process](#automatically-restart-process)
        - [Automatically stop process](#automatically-stop-process)
        - [Automatically restart service](#automatically-restart-service)
    - [Exit Strategy: how to stop the current process](#exit-strategy-how-to-stop-the-current-process)
        - [Environment exit](#environment-exit)
        - [App host](#app-host)
        - [Cancellation token](#cancellation-token)
        - [Semaphore](#semaphore)
        - [Windows Forms](#windows-forms)
        - [Windows Presentation Foundation](#windows-presentation-foundation)
        - [Custom exit strategy](#custom-exit-strategy)

<!-- /MarkdownTOC -->

## Requirements
- .NET Runtime 8 or later
- Linux or Windows

## Installation

```sh
dotnet add package RuntimeUpgradeNotifier
```

## Usage

### Get started
Construct a new instance of `RuntimeUpgradeNotifier`.

```cs
using RuntimeUpgrade.Notifier;

using IRuntimeUpgradeNotifier runtimeUpgradeNotifier = new RuntimeUpgradeNotifier();
```

### Restart Strategy: what to do when the .NET runtime is upgraded

#### Manual
By default, this library will only notify you when the .NET Runtime is upgraded, instead of starting or stopping any processes. You can listen for events to determine when the .NET runtime was upgraded and take any actions you want.
```cs
runtimeUpgradeNotifier.RestartStrategy = RestartStrategy.Manual; // default property value
runtimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    Console.WriteLine(".NET runtime was upgraded");
};
```

> [!WARNING]  
> Try not to call any code that would load any new .NET BCL libraries in the event handler, as these libraries would already have been deleted during the recent runtime upgrade, and may cause a [`FileNotFoundException`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filenotfoundexception). If you do need to do any work in the event handler, it is safest to preload assemblies by referring to types in the assembly when the program starts, and cache any values that you may need later. For example, if you want to log the old .NET runtime version when it gets upgraded, it is safest to cache [`Environment.Version`](https://learn.microsoft.com/en-us/dotnet/api/system.environment.version) in a variable when the program starts, instead of trying to read it after the old runtime has already been deleted.

#### Automatically start a new process
This starts a duplicate copy of the current process, with the same arguments, working directory, and environment variables. However, it does not exit the current process, so you will probably want to shut it down yourself by listening for the `IRuntimeUpgradeNotifier.RuntimeUpgraded` event, otherwise there will be two instances of the program running.

```cs
runtimeUpgradeNotifier.RestartStrategy = RestartStrategy.AutoStartNewProcess;
runtimeUpgradeNotifier.RuntimeUpgraded += (_, evt) => {
    // tear down current process
};
```

#### Automatically restart process
When the .NET Runtime for your process is upgraded, this will start a new instance of your program with the same command-line arguments, environment variables, and working directory as the current process. It will then exit the old process automatically.

To control how the current process exits, including its exit code, see [Exit Strategy](#exit-strategy-how-to-stop-the-current-process).

Both the old and new process will be running for a brief period, so if any resources or actions can't be concurrent, you will need an interprocess synchronization technique like a [`Semaphore`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphore).

```cs
runtimeUpgradeNotifier.RestartBehavior = RestartBehavior.AutoRestartProcess;
```

#### Automatically stop process
Stop the current process when the .NET runtime is upgraded, but does not automatically start any new processes. This is useful if a watchdog will automatically restart your process when it exits.

To control how the process exits, including its exit code, see [Exit Strategy](#exit-strategy-how-to-stop-the-current-process).

```cs
runtimeUpgradeNotifier.RestartStrategy = RestartStrategy.AutoStopProcess;
runtimeUpgradeNotifier.ExitStrategy = new EnvironmentExit(1); // if the watchdog expects a certain exit code in order to restart
```

#### Automatically restart service
Tell the current background service/daemon to restart when the .NET runtime is upgraded. This works with both [systemd](https://www.nuget.org/packages/Microsoft.Extensions.Hosting.Systemd) and [Windows services](https://www.nuget.org/packages/Microsoft.Extensions.Hosting.WindowsServices/), and is equivalent to calling `systemctl restart $serviceName` or `Restart-Service $serviceName`.

> [!NOTE]  
> In practice, the official .NET installers on Windows (including through Windows Update) already automatically restart .NET processes without using this library, so this is only really needed on Linux. Cross-platform services can set this to `AutoRestartService` to avoid special cases, and Windows-only services don't need to use this library at all.

```cs
runtimeUpgradeNotifier.RestartStrategy = RestartStrategy.AutoRestartService;
```

### Exit Strategy: how to stop the current process
This is only used when the [Restart Strategy](#restart-strategy-what-to-do-when-the-net-runtime-is-upgraded) is either `AutoRestartProcess` or `AutoStopProcess`. Otherwise, when it is `Manual`, `AutoStartNewProcess`, or `AutoRestartService`, this property has no effect.

By default, the current process will be exited by calling [`Environment.Exit(Environment.ExitCode)`](https://learn.microsoft.com/en-us/dotnet/api/system.environment.exit). You can customize this behavior to shut down your program any way you want. Here are some techniques provided in the library, and you can also [make your own custom approach by implementing the `ExitStrategy` interface](#custom-exit-strategy).

#### Environment exit
This is the default strategy. It shuts down the current process by calling [`Environment.Exit`](https://learn.microsoft.com/en-us/dotnet/api/system.environment.exit).

By default, the exit code is [`Environment.ExitCode`](https://learn.microsoft.com/en-us/dotnet/api/system.environment.exitcode). To specify a custom exit code, set the `Environment.ExitCode` property, or construct a new instance of `EnvironmentExit` with the exit code as a constructor argument.

```cs
runtimeUpgradeNotifier.ExitStrategy = new EnvironmentExit(1);
```

#### App host
This stops the [`Microsoft.Extensions.Hosting`](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host) IOC container used by ASP.NET Core webapps and the .NET Generic Host, which is typically the only thing keeping the `Main` method from returning.

```cs
runtimeUpgradeNotifier.ExitStrategy = new HostedLifetimeExit(app); // app is IHost, such as WebApplication
// or 
runtimeUpgradeNotifier.ExitStrategy = new HostedLifetimeExit(app.Services.GetRequiredService<IHostApplicationLifetime>());
```

#### Cancellation token
If your program is blocking the `Main` method from returning using a [`CancellationToken`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtoken), you can cancel it to let the program exit.

```cs
runtimeUpgradeNotifier.ExitStrategy = new CancellationTokenExit(cancellationTokenSource);
```

#### Semaphore
If your program is blocking the `Main` method from returning using a [`SemaphoreSlim`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim), you can release it to let the program exit.

```cs
runtimeUpgradeNotifier.ExitStrategy = new SemaphoreExit(semaphore);
```

#### Windows Forms
This exits a Windows Forms program by calling [`Application.Exit`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.application.exit).

```cs
runtimeUpgradeNotifier.ExitStrategy = new FormsApplicationExit();
```

#### Windows Presentation Foundation
This exits a WPF program by calling [`Application.Current.Shutdown`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.shutdown). By default, the exit code is [`Environment.ExitCode`](https://learn.microsoft.com/en-us/dotnet/api/system.environment.exitcode), but you can change this by passing the exit code in the constructor shown below or by setting `Environment.ExitCode`.

```cs
runtimeUpgradeNotifier.ExitStrategy = new WpfApplicationExit(1);
```

#### Custom exit strategy
You can also define your own technique to exit the process by implementing the `ExitStrategy` interface. Here is an example that uncleanly kills the current process.

```cs
runtimeUpgradeNotifier.ExitStrategy = new KillExit();

public class KillExit: ExitStrategy {

    public void StopCurrentProcess() {
        Process.GetCurrentProcess().Kill();
    }

}
```

> [!TIP]
> Remember that if you only want to add some extra instructions before stopping the process and then use one of the predefined exit strategies, you can listen for the `IRuntimeUpgradeNotifier.RuntimeUpgraded` event instead of delegating to or subclassing an exit strategy.