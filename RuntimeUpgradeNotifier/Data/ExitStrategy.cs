using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RuntimeUpgrade.Notifier.Data;

/// <summary>
/// How the current process should exit if this library needs to restart it after a .NET runtime upgrade.
/// </summary>
public interface ExitStrategy {

    /// <summary>
    /// Exit the currently running process.
    /// </summary>
    void StopCurrentProcess();

}

/// <summary>
/// Stop the current process by calling <see cref="Environment.Exit"/>.
/// </summary>
/// <param name="exitCode">The exit code that the current process should exit with, such as 0 or 1, or <c>null</c> to use <see cref="Environment.ExitCode"/>.</param>
public class EnvironmentExit(int? exitCode): ExitStrategy {

    /// <inheritdoc />
    public virtual void StopCurrentProcess() {
        Environment.Exit(exitCode ?? Environment.ExitCode);
    }

}

#if WINDOWS
/// <summary>
/// Exit a Windows Forms application by calling <see cref="Application.Exit()"/>. This assumes that the program exits after the Forms application it's running exits.
/// </summary>
public class FormsApplicationExit: ExitStrategy {

    /// <inheritdoc />
    public virtual void StopCurrentProcess() {
        Application.Exit();
    }

}

/// <summary>
/// Exit a Windows Presentation Foundation application by calling <see cref="System.Windows.Application.Shutdown()"/>
/// </summary>
/// <param name="exitCode">The exit code that the current process should exit with, such as 0 or 1, or <c>null</c> to use <see cref="Environment.ExitCode"/>.</param>
public class WpfApplicationExit(int? exitCode): ExitStrategy {

    /// <inheritdoc />
    public virtual void StopCurrentProcess() {
        System.Windows.Application.Current.Shutdown(exitCode ?? Environment.ExitCode);
    }

}
#endif

/// <summary>
/// Exit a program running the .NET Generic Host, such as an ASP.NET Core webapp or a service/console app with the Microsoft IoC container.
/// </summary>
/// <param name="applicationLifetime">The <see cref="IHostApplicationLifetime"/> from the DI context.</param>
public class HostedLifetimeExit(IHostApplicationLifetime applicationLifetime): ExitStrategy {

    /// <summary>Exit a program running the .NET Generic Host, such as an ASP.NET Core webapp or a service/console app with the Microsoft IoC container.</summary>
    /// <param name="host">The Generic Host, such as a <c>WebApplication</c></param>
    public HostedLifetimeExit(IHost host): this(host.Services.GetRequiredService<IHostApplicationLifetime>()) {}

    /// <inheritdoc />
    public virtual void StopCurrentProcess() {
        applicationLifetime.StopApplication();
    }

}

/// <summary>
/// Exit a program by cancelling a <see cref="CancellationToken"/>, which the program must have been waiting for before it exits.
/// </summary>
/// <param name="cancellationTokenSource">Source for a <see cref="CancellationToken"/> that the program is waiting for before exiting.</param>
public class CancellationTokenExit(CancellationTokenSource cancellationTokenSource): ExitStrategy {

    /// <inheritdoc />
    public void StopCurrentProcess() {
        cancellationTokenSource.Cancel();
    }

}

/// <summary>
/// Exit a program by releasing a <see cref="SemaphoreSlim"/>, which the program must have been waiting for before it exits.
/// </summary>
/// <param name="semaphore">Semaphore that the program is waiting for before exiting.</param>
public class SemaphoreExit(SemaphoreSlim semaphore): ExitStrategy {

    /// <inheritdoc />
    /// <exception cref="SemaphoreFullException"></exception>
    public void StopCurrentProcess() {
        semaphore.Release();
    }

}

/// <summary>
/// Exit a program by completing the <see cref="Task"/> in <see cref="StopRequested"/>, which the program must have been waiting for before it exits.
/// </summary>
public class TaskExit: ExitStrategy {

    private readonly TaskCompletionSource _tcs = new();

    /// <summary>
    /// Will be completed when the runtime is upgraded and this program is ready to exit. You can await this in <c>Main</c>, or use <see cref="Task.WhenAny(System.Threading.Tasks.Task[])"/> to allow your program to also exit in other ways.
    /// </summary>
    public Task StopRequested => _tcs.Task;

    /// <inheritdoc />
    public void StopCurrentProcess() {
        _tcs.SetResult();
    }

}