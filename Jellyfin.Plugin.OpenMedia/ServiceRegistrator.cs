using System;
using System.Net.Http;
using Jellyfin.Plugin.OpenMedia.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OpenMedia;

/// <summary>
/// DI-Hooks. Registriert den typed HttpClient für OpenMediaApiClient mit AllowAutoRedirect=false,
/// damit GetStreamUrlAsync den 302 Location-Header lesen kann.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient<OpenMediaApiClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
    }
}
