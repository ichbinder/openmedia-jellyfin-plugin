using Jellyfin.Plugin.OpenMedia.MediaSources;
using Jellyfin.Plugin.OpenMedia.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia;

/// <summary>
/// DI-Hooks. Registriert:
///  * <see cref="LibraryAutoCreateService"/> — legt die Jellyfin-Library beim Start an.
///  * <see cref="ILibrarySyncRunner"/> + <see cref="LibrarySyncRunner"/> — gemeinsame
///    Sync-Logik fuer ScheduledTask und Polling-Service.
///  * <see cref="LibraryPollingService"/> — 15s-Polling gegen /jellyfin/library/version.
///  * <see cref="OpenMediaMediaSourceProvider"/> — liefert pro .strm-Item eine zusaetzliche
///    HTTP-MediaSource fuer nativen Download.
///  * <see cref="PrecacheStateStore"/> — persistenter State fuer Pre-Cache-Worker.
///  * <see cref="PrecacheDownloader"/> — Download-Mechanik mit Range-Resume und SHA256.
///  * <see cref="PrecacheWorker"/> — HostedService: Poll → Download → Verify → Refresh.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ILibrarySyncRunner, LibrarySyncRunner>();
        serviceCollection.AddHostedService<LibraryAutoCreateService>();
        serviceCollection.AddHostedService<LibraryPollingService>();
        serviceCollection.AddSingleton<IMediaSourceProvider, OpenMediaMediaSourceProvider>();

        // PrecacheWorker and dependencies
        serviceCollection.AddSingleton<PrecacheStateStore>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<PrecacheStateStore>>();
            return new PrecacheStateStore(paths.DataPath, logger);
        });

        serviceCollection.AddSingleton<PrecacheDownloader>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<PrecacheDownloader>>();
            return new PrecacheDownloader(httpFactory.CreateClient("PrecacheDownload"), logger);
        });

        serviceCollection.AddHostedService<PrecacheWorker>();

        // TTL Cleanup scheduled task
        serviceCollection.AddTransient<IScheduledTask>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PrecacheTtlCleanupService>>();
            var store = sp.GetRequiredService<PrecacheStateStore>();
            return new PrecacheTtlCleanupService(logger, store);
        });
    }
}
