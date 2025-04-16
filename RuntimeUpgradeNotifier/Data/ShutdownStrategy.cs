using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RuntimeUpgrade.Notifier.Data;

public interface ShutdownStrategy {

    void StopCurrentProcess();

}

public class EnvironmentExit(int exitCode = 0): ShutdownStrategy {

    public void StopCurrentProcess() {
        Environment.Exit(exitCode);
    }

}

#if WINDOWS

public class FormsApplicationExit: ShutdownStrategy {

    public void StopCurrentProcess() {
        Application.Exit();
    }

}

public class WpfApplicationExit(int exitCode = 0): ShutdownStrategy {

    public void StopCurrentProcess() {
        System.Windows.Application.Current.Shutdown(exitCode);
    }

}

#endif

public class HostedLifetimeExit(IHostApplicationLifetime applicationLifetime): ShutdownStrategy {

    public HostedLifetimeExit(IHost host): this(host.Services.GetRequiredService<IHostApplicationLifetime>()) { }

    public void StopCurrentProcess() {
        applicationLifetime.StopApplication();
    }

}