using Jellyfin.Plugin.OpenMedia.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OpenMedia;

/// <summary>
/// DI-Hooks. Registriert:
///  * <see cref="LibraryAutoCreateService"/> — legt die Jellyfin-Library beim Start an.
///  * <see cref="ILibrarySyncRunner"/> + <see cref="LibrarySyncRunner"/> — gemeinsame
///    Sync-Logik fuer ScheduledTask und Polling-Service.
///  * <see cref="LibraryPollingService"/> — 15s-Polling gegen /jellyfin/library/version.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ILibrarySyncRunner, LibrarySyncRunner>();
        serviceCollection.AddHostedService<LibraryAutoCreateService>();
        serviceCollection.AddHostedService<LibraryPollingService>();
    }
}
