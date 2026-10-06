using System.Runtime.InteropServices;
using System.Text.Json;
using DSO.Core.Evoker.Api;
using DSO.Core.Evoker.Commands;
using DSO.Core.Evoker.Plugins.Management;
using Microsoft.AspNetCore.Mvc;

namespace DSO.Core.Evoker.Plugins.Api.Controllers
{
    /// <summary>
    /// Plugin kayıtları - anahtar her zaman Guid:
    /// <code>
    /// GET    api/plugins?active=true&amp;state=Running&amp;search=stok     liste (durum + ayarlar + public yüzey özeti)
    /// GET    api/plugins/system                                     kök klasör, worker, çalışma ortamı, sayılar
    /// POST   api/plugins                                            kayıt (gövde: GET api/plugin-files/scan → registerTemplate)
    /// GET    api/plugins/{key}?samples=true&amp;values=true             detay: tam tanım + komut şablonları + o anki değerler
    /// PATCH  api/plugins/{key}                                      güncelle (ad, not, yol/sürüm, constructorArgs, mod, ayarlar...)
    /// DELETE api/plugins/{key}                                      kaydı sil (yüklüyse önce boşaltılır)
    /// POST   api/plugins/{key}/activate   (ya da /load)             aktif et + yükle
    /// POST   api/plugins/{key}/deactivate                           pasif yap (bellekten atılır, listede kalır)
    /// POST   api/plugins/{key}/reload                               yeniden yükle (DLL değiştiyse; state sıfırlanır)
    /// POST   api/plugins/{key}/stop                                 bellekten at ama aktif bırak (ilk çağrıda yüklenir)
    /// PUT    api/plugins/{key}/mode       { "mode":"InProcess" }    canlı mod değişimi
    /// POST   api/plugins/{key}/execute    { "op":"invoke", ... }    JSON komut
    /// </code>
    /// </summary>
    [Route("api/plugins")]
    public sealed class PluginsController : EvokerApiControllerBase
    {
        private readonly PluginApiService _api;
        private PluginManager Manager => _api.Manager;

        public PluginsController(PluginApiService api) => _api = api;

        // ============================ Okuma ============================

        [HttpGet]
        public IActionResult List([FromQuery] bool? active = null, [FromQuery] PluginState? state = null,
            [FromQuery] PluginExecutionMode? mode = null, [FromQuery] string? search = null, [FromQuery] bool? summary = null)
        {
            bool withSummary = summary ?? _api.Options.ListIncludesSummary;
            var items = Manager.GetStatuses()
                .Where(s => active == null || s.IsActive == active)
                .Where(s => state == null || s.State == state)
                .Where(s => mode == null || s.Mode == mode)
                .Where(s => string.IsNullOrWhiteSpace(search)
                            || (s.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                            || s.TypeFullName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                            || s.FilePath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(s => _api.View(s.Key, withSummary))
                .ToList();
            int act = items.Count(i => i.IsActive), run = items.Count(i => i.IsRunning);
            return Envelope(items, $"{items.Count} plugin ({act} aktif, {run} yüklü)");
        }

        [HttpGet("system")]
        public IActionResult SystemInfo()
        {
            var all = Manager.GetStatuses();
            return Envelope(new
            {
                pluginsRoot = _api.PluginsRoot,
                registrationsFile = _api.RegistrationsFile,
                workerPath = _api.WorkerPath,
                sandboxAvailable = _api.WorkerPath != null && System.IO.File.Exists(_api.WorkerPath),
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                processId = Environment.ProcessId,
                counts = new
                {
                    total = all.Count,
                    active = all.Count(s => s.IsActive),
                    running = all.Count(s => s.IsRunning),
                    byState = all.GroupBy(s => s.State).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                    byMode = all.GroupBy(s => s.Mode).ToDictionary(g => g.Key.ToString(), g => g.Count())
                },
                allowTypesFrom = Manager.Catalog.AllowedPatterns
            });
        }

        [HttpGet("{key:guid}")]
        public async Task<IActionResult> Detail(Guid key, [FromQuery] bool samples = false, [FromQuery] bool values = true,
            [FromQuery] bool nonPublic = false)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            var view = _api.View(key, includeSummary: true);
            if (!view.FileExists)
                return Envelope(new PluginDetailView { Plugin = view, Warnings = new() { "Plugin dosyası bulunamadı - tanım okunamadı." } });
            var d = await Manager.DescribeAsync(key, values, samples, nonPublic);
            return Envelope(new PluginDetailView
            {
                Plugin = view,
                Assembly = d.Assembly,
                Type = d.Type,
                Values = d.Values,
                Warnings = d.Warnings
            });
        }

        // ============================ Kayıt ============================

        [HttpPost]
        public async Task<IActionResult> Register([FromBody] JsonElement body)
        {
            var req = EvokerApiJson.Read<PluginRegisterRequest>(body);
            var full = _api.ResolvePath(req.Path);
            string type = string.IsNullOrWhiteSpace(req.TypeName) ? _api.PickSingleType(full) : req.TypeName!.Trim();

            var reg = new PluginRegistration
            {
                Name = req.Name,
                FilePath = full,
                TypeFullName = type,
                ConstructorArgs = NullIfEmpty(req.ConstructorArgs),
                Mode = req.Mode,
                IsActive = req.IsActive,
                IncludeNonPublic = req.IncludeNonPublic,
                DefaultTimeoutMs = req.DefaultTimeoutMs,
                Notes = req.Notes
            };
            if (req.MaxConcurrency is { } mc) reg.MaxConcurrency = mc;
            if (req.AutoRestartOnCrash is { } ar) reg.AutoRestartOnCrash = ar;
            if (req.HeartbeatIntervalMs is { } hb) reg.HeartbeatIntervalMs = hb;
            if (req.MissedHeartbeatsBeforeKill is { } mh) reg.MissedHeartbeatsBeforeKill = mh;

            var result = await Manager.RegisterAsync(reg);
            if (!result.Success)
                return Fail(EvokerErrorCodes.InvalidArguments, result.Message);

            var key = result.Key!.Value;
            Response.Headers["Location"] = $"/api/plugins/{key}";
            return Envelope(_api.View(key, includeSummary: true), result.Message, 201);
        }

        [HttpPatch("{key:guid}")]
        public async Task<IActionResult> Update(Guid key, [FromBody] JsonElement body)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            if (body.ValueKind != JsonValueKind.Object) return Fail(EvokerErrorCodes.BadRequest, "Gövde bir JSON nesnesi olmalı.");

            var changes = new List<Action<PluginRegistration>>();
            var done = new List<string>();
            PluginExecutionMode? newMode = null;
            bool? newActive = null;

            foreach (var p in body.EnumerateObject())
            {
                var v = p.Value;
                switch (p.Name.ToLowerInvariant())
                {
                    case "name": { var x = Str(v); changes.Add(r => r.Name = x); done.Add("ad"); break; }
                    case "notes": { var x = Str(v); changes.Add(r => r.Notes = x); done.Add("not"); break; }
                    case "path": { var x = _api.ResolvePath(Str(v)); changes.Add(r => r.FilePath = x); done.Add("dosya"); break; }
                    case "typename": { var x = Str(v) ?? ""; changes.Add(r => r.TypeFullName = x.Trim()); done.Add("tip"); break; }
                    case "constructorargs":
                        {
                            JsonElement? x = v.ValueKind == JsonValueKind.Null ? null : v.Clone();
                            changes.Add(r => r.ConstructorArgs = x);
                            done.Add("constructor argümanları");
                            break;
                        }
                    case "includenonpublic": { bool x = v.GetBoolean(); changes.Add(r => r.IncludeNonPublic = x); done.Add(p.Name); break; }
                    case "maxconcurrency": { int x = Int(v); changes.Add(r => r.MaxConcurrency = x); done.Add(p.Name); break; }
                    case "autorestartoncrash": { bool x = v.GetBoolean(); changes.Add(r => r.AutoRestartOnCrash = x); done.Add(p.Name); break; }
                    case "heartbeatintervalms": { int x = Int(v); changes.Add(r => r.HeartbeatIntervalMs = x); done.Add(p.Name); break; }
                    case "missedheartbeatsbeforekill": { int x = Int(v); changes.Add(r => r.MissedHeartbeatsBeforeKill = x); done.Add(p.Name); break; }
                    case "defaulttimeoutms": { int? x = v.ValueKind == JsonValueKind.Null ? null : Int(v); changes.Add(r => r.DefaultTimeoutMs = x); done.Add(p.Name); break; }
                    case "mode": newMode = ParseMode(v); break;
                    case "isactive": newActive = v.GetBoolean(); break;
                    default:
                        return Fail(EvokerErrorCodes.BadRequest,
                            $"'{p.Name}' güncellenemez. Alanlar: name, notes, path, typeName, constructorArgs, mode, isActive, includeNonPublic, " +
                            "maxConcurrency, autoRestartOnCrash, heartbeatIntervalMs, missedHeartbeatsBeforeKill, defaultTimeoutMs.");
                }
            }

            if (changes.Count > 0)
            {
                try { await Manager.UpdateAsync(key, r => { foreach (var c in changes) c(r); }); }
                catch (ArgumentException ex) { return Fail(EvokerErrorCodes.InvalidArguments, ex.Message + " - değişiklik uygulanmadı."); }
            }
            if (newMode is { } m) { await Manager.SetModeAsync(key, m); done.Add("mod: " + m); }
            if (newActive is { } a)
            {
                if (a) await Manager.ActivateAsync(key); else await Manager.DeactivateAsync(key);
                done.Add(a ? "aktifleştirildi" : "pasifleştirildi");
            }

            var view = _api.View(key, includeSummary: true);
            string msg = done.Count == 0 ? "Değişiklik yok." : "Güncellendi: " + string.Join(", ", done) + ".";
            if (view.State == PluginState.Faulted) msg += " UYARI: yüklenemedi - " + view.LastError;
            return Envelope(view, msg);
        }

        [HttpDelete("{key:guid}")]
        public async Task<IActionResult> Delete(Guid key)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            var name = Manager.GetStatus(key).DisplayName;
            await Manager.UnregisterAsync(key);
            return Envelope(new { key }, $"{name} silindi.");
        }

        // ============================ Yaşam döngüsü ============================

        [HttpPost("{key:guid}/activate")]
        [HttpPost("{key:guid}/load")]
        public async Task<IActionResult> Activate(Guid key)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            var s = await Manager.ActivateAsync(key);
            var view = _api.View(key, includeSummary: false);
            if (s.State == PluginState.Running)
                return Envelope(view, $"{s.DisplayName} aktif ve yüklü ({s.Mode}{(s.ProcessId is { } pid ? ", pid " + pid : "")}).");
            return PartialFail(view, EvokerErrorCodes.InvalidOperation,
                $"{s.DisplayName} aktif edildi ama YÜKLENEMEDİ: {s.LastError}");
        }

        [HttpPost("{key:guid}/deactivate")]
        public async Task<IActionResult> Deactivate(Guid key)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            var s = await Manager.DeactivateAsync(key);
            return Envelope(_api.View(key, includeSummary: false), $"{s.DisplayName} pasif - bellekten atıldı, listede duruyor.");
        }

        [HttpPost("{key:guid}/reload")]
        public async Task<IActionResult> Reload(Guid key)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            if (!Manager.GetStatus(key).IsActive)
                return Fail(EvokerErrorCodes.Inactive, "Plugin pasif - önce aktifleştirin (POST …/activate).");
            var s = await Manager.ReloadAsync(key);
            var view = _api.View(key, includeSummary: false);
            return s.State == PluginState.Running
                ? Envelope(view, $"{s.DisplayName} yeniden yüklendi (sürüm {s.AssemblyVersion}).")
                : PartialFail(view, EvokerErrorCodes.InvalidOperation, $"{s.DisplayName} yeniden YÜKLENEMEDİ: {s.LastError}");
        }

        [HttpPost("{key:guid}/stop")]
        public async Task<IActionResult> Stop(Guid key)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            await Manager.StopAsync(key);
            return Envelope(_api.View(key, includeSummary: false), "Bellekten atıldı; aktif - ilk çağrıda yeniden yüklenecek.");
        }

        [HttpPut("{key:guid}/mode")]
        public async Task<IActionResult> SetMode(Guid key, [FromBody] JsonElement body)
        {
            if (!Manager.Contains(key)) return NotFoundFail(key);
            var req = EvokerApiJson.Read<PluginModeRequest>(body);
            if (req.Mode is not { } mode) return Fail(EvokerErrorCodes.BadRequest, "'mode' gerekli: \"Sandbox\" ya da \"InProcess\".");
            var before = Manager.GetStatus(key);
            await Manager.SetModeAsync(key, mode);
            var view = _api.View(key, includeSummary: false);
            string msg = before.Mode == mode ? $"Zaten {mode}."
                : before.IsRunning ? $"Canlı geçiş: {before.Mode} → {mode} (plugin state'i sıfırlandı)."
                : $"Mod {mode} olarak kaydedildi (bir sonraki yüklemede geçerli).";
            return Envelope(view, msg);
        }

        // ============================ Komut ============================

        [HttpPost("{key:guid}/execute")]
        public async Task<IActionResult> Execute(Guid key, [FromBody] JsonElement command)
        {
            if (!TryReadCommand(command, out var cmd, out var error)) return error!;
            var result = await Manager.Catalog.ExecuteAsync(key, cmd, HttpContext.RequestAborted);
            return CommandResult(result);
        }

        // ============================ Yardımcılar ============================

        private IActionResult NotFoundFail(Guid key) =>
            Fail(EvokerErrorCodes.TargetNotFound, $"'{key}' anahtarlı plugin kaydı yok.");

        private IActionResult PartialFail(object result, string code, string message)
        {
            var r = ApiResponse.Fail(code, message, ElapsedMs);
            r.Result = result;
            return new JsonResult(r, EvokerApiJson.Options) { StatusCode = EvokerErrorCodes.HttpStatus(code) };
        }

        private static JsonElement? NullIfEmpty(JsonElement? e) =>
            e is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } x ? x.Clone() : null;

        private static string? Str(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => v.GetString(),
            _ => throw new ArgumentException($"Metin bekleniyordu: {v.GetRawText()}")
        };

        private static int Int(JsonElement v) =>
            v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i) ? i : v.GetInt32();

        private static PluginExecutionMode ParseMode(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Number) return (PluginExecutionMode)v.GetInt32();
            if (Enum.TryParse<PluginExecutionMode>(v.GetString(), ignoreCase: true, out var m) && Enum.IsDefined(m)) return m;
            throw new ArgumentException($"Geçersiz mode: {v.GetRawText()} (\"Sandbox\" ya da \"InProcess\").");
        }
    }
}