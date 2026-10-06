using DSO.Core.Evoker.Api;
using DSO.Core.Evoker.Commands;
using DSO.Core.Evoker.Plugins.Management;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DSO.Core.Evoker.Plugins.Api
{
    /// <summary>
    /// Plugin API ayarları - appsettings.json'daki "Evoker:Plugins" bölümünden de okunabilir. Göreli yollar UYGULAMA
    /// KLASÖRÜNE (AppContext.BaseDirectory: bin/…/net6.0 ya da yayın klasörü) göredir. Varsayılan düzen:
    /// <code>
    /// &lt;uygulama klasörü&gt;/
    ///   MyApi.dll, appsettings.json ...
    ///   Host/       sandbox worker'ı (DSO.Core.Evoker.PluginHost.exe/.dll + bağımlılıkları)
    ///   Plugins/    plugin DLL'leri (alt klasörler serbest: Siparis/1.0, Stok/v2 ...)
    ///   App_Data/   kayıtlar (plugins.json) + crash.log
    /// </code>
    /// </summary>
    public sealed class EvokerPluginsApiOptions
    {
        /// <summary>Plugin DLL'lerinin kök klasörü. Kayıtlarda yollar buna GÖRELİ saklanır. Varsayılan: "Plugins".</summary>
        public string PluginsRoot { get; set; } = "Plugins";

        /// <summary>Kayıtların saklandığı JSON dosyası. Varsayılan: "App_Data/plugins.json".</summary>
        public string RegistrationsFile { get; set; } = "App_Data/plugins.json";

        /// <summary>
        /// Sandbox worker'ı (DSO.Core.Evoker.PluginHost) - .dll ise "dotnet x.dll", .exe ise doğrudan başlatılır.
        /// Boşsa sırayla uygulama klasörünün "Host" alt klasöründe, uygulama klasöründe ve "worker" alt klasöründe aranır.
        /// </summary>
        public string? WorkerPath { get; set; }

        /// <summary>Uygulama açılırken aktif plugin'ler arka planda yüklensin (ilk istek beklemesin).</summary>
        public bool WarmStart { get; set; } = true;

        public int StartupTimeoutMs { get; set; } = 15000;

        /// <summary>Sandbox çökmelerinin yazılacağı log dosyası (boş = yazılmaz). Varsayılan: "App_Data/crash.log".</summary>
        public string? CrashLogFile { get; set; } = "App_Data/crash.log";

        /// <summary>Listede her plugin'in public yüzey özeti (constructor/metot/property imzaları) gösterilsin (?summary= ile değişir).</summary>
        public bool ListIncludesSummary { get; set; } = true;
    }

    public static class EvokerPluginsApiExtensions
    {
        /// <summary>
        /// Plugin yönetim uçlarını ekler (api/plugins, api/plugin-files). AddEvokerApi'yi de çağırır (aynı EvokerCatalog;
        /// plugin'ler katalogda Kind="Plugin" olarak da görünür). PluginManager tekil olarak kaydedilir ve uygulama
        /// açılırken başlatılır, kapanırken tüm worker'ları kapatır.
        /// <code>
        /// builder.Services.AddControllers()
        ///     .AddEvokerApi()
        ///     .AddEvokerPluginsApi(o => builder.Configuration.GetSection("Evoker:Plugins").Bind(o));
        /// </code>
        /// </summary>
        public static IMvcBuilder AddEvokerPluginsApi(this IMvcBuilder mvc, Action<EvokerPluginsApiOptions>? configure = null)
        {
            mvc.AddEvokerApi();
            var options = new EvokerPluginsApiOptions();
            configure?.Invoke(options);
            var services = mvc.Services;
            services.TryAddSingleton(options);

            services.TryAddSingleton(sp =>
            {
                var o = sp.GetRequiredService<EvokerPluginsApiOptions>();
                string baseDir = AppContext.BaseDirectory;
                string root = Full(baseDir, o.PluginsRoot);
                Directory.CreateDirectory(root);
                string regFile = Full(baseDir, o.RegistrationsFile);
                var store = new PluginsRootConfigStore(new JsonFilePluginConfigStore(regFile), root);

                string? worker = FindWorker(baseDir, o.WorkerPath);
                var manager = new PluginManager(store, new PluginManagerOptions
                {
                    HostPath = worker ?? "",
                    HostIsDotnetDll = worker == null || !worker.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    StartupTimeoutMs = o.StartupTimeoutMs,
                    CrashLogFilePath = string.IsNullOrWhiteSpace(o.CrashLogFile) ? null : Full(baseDir, o.CrashLogFile!),
                    WarmStart = o.WarmStart
                }, sp.GetRequiredService<EvokerCatalog>());
                return new PluginApiService(manager, store, o, regFile, worker);
            });
            services.TryAddSingleton(sp => sp.GetRequiredService<PluginApiService>().Manager);
            services.AddHostedService<PluginManagerHostedService>();
            mvc.AddApplicationPart(typeof(EvokerPluginsApiExtensions).Assembly);
            return mvc;
        }

        private static string Full(string baseDir, string path) =>
            Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));

        private static string? FindWorker(string baseDir, string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return Full(baseDir, configured!);
            foreach (var dir in new[] { Path.Combine(baseDir, "Host"), baseDir, Path.Combine(baseDir, "worker") })
                foreach (var name in new[] { "DSO.Core.Evoker.PluginHost.dll", "DSO.Core.Evoker.PluginHost.exe" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return Path.GetFullPath(p);
                }
            return null;
        }
    }

    /// <summary>PluginManager'ı uygulama açılırken başlatır (kayıtları okur, aktifleri yükler), kapanırken worker'ları kapatır.</summary>
    internal sealed class PluginManagerHostedService : IHostedService
    {
        private readonly PluginApiService _api;
        private readonly ILogger<PluginManagerHostedService> _log;

        public PluginManagerHostedService(PluginApiService api, ILogger<PluginManagerHostedService> log)
        {
            _api = api;
            _log = log;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _api.Manager.InitializeAsync();
            var all = _api.Manager.GetStatuses();
            _log.LogInformation("Evoker plugin'leri: {Count} kayıt ({Active} aktif). Plugins: {Root}. Worker: {Worker}",
                all.Count, all.Count(s => s.IsActive), _api.PluginsRoot, _api.WorkerPath ?? "(bulunamadı - sandbox modu çalışmaz)");
            _api.Manager.PluginCrashed += (key, e) =>
                _log.LogWarning("Plugin {Key} sandbox worker'ı çöktü: {Reason} (yeniden başlıyor: {Restart})", key, e.Reason.Message, e.WillRestart);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _api.Manager.DisposeAsync();
        }
    }
}