using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.OpenMedia;

/// <summary>
/// DI-Hooks. Registriert beim Plugin-Start den LibraryAutoCreateService der dafuer sorgt
/// dass eine openmedia-Movies-Library existiert ohne dass der User sie manuell anlegt.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<LibraryAutoCreateService>();
    }
}
