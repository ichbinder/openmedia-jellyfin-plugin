using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OpenMedia;

/// <summary>
/// DI-Hooks. In S01 leer — ab S03 wird hier OpenMediaApiClient registriert.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // S03: serviceCollection.AddSingleton<OpenMediaApiClient>();
    }
}
