using DSO.Core.Evoker.Api;
using Microsoft.AspNetCore.Mvc;

namespace DSO.Core.Evoker.Plugins.Api.Controllers
{
    /// <summary>
    /// Plugins klasöründeki DLL'ler (yükleme/upload YOK - dosyalar sunucuya dağıtımla konur):
    /// <code>
    /// GET api/plugin-files?search=Stok                                         tüm DLL'ler (alt klasörler dahil) + onları kullanan kayıtlar
    /// GET api/plugin-files/scan?path=Stok/v2/Stok.dll                          DLL'i ÇALIŞTIRMADAN tarar: tipler, özet, kayıt şablonu
    /// GET api/plugin-files/scan?path=Stok/v2/Stok.dll&amp;type=Stok.StokServisi&amp;samples=true   bir tipin tam tanımı + komut şablonları
    /// </code>
    /// </summary>
    [Route("api/plugin-files")]
    public sealed class PluginFilesController : EvokerApiControllerBase
    {
        private readonly PluginApiService _api;

        public PluginFilesController(PluginApiService api) => _api = api;

        [HttpGet]
        public IActionResult List([FromQuery] string? search = null)
        {
            var files = _api.ListFiles(search);
            return Envelope(new { root = _api.PluginsRoot, files }, $"{files.Count} DLL");
        }

        [HttpGet("scan")]
        public IActionResult Scan([FromQuery] string? path, [FromQuery] string? type = null,
            [FromQuery] bool samples = true, [FromQuery] bool nonPublic = false)
        {
            var full = _api.ResolvePath(path);
            var result = _api.ScanFile(full, type, samples, nonPublic);
            int registrable = result.Types.Count(t => t.CanRegister);
            return Envelope(result, result.Kind == Scanning.PluginKind.ManagedDotNet
                ? $"{result.Types.Count} tip, {registrable} tanesi plugin olarak kaydedilebilir"
                : "Bu dosya bir .NET assembly'si değil");
        }
    }
}