using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DSO.Core.Evoker.Commands;
using DSO.Core.Evoker.Description;
using DSO.Core.Evoker.Plugins.Management;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Api
{
    /// <summary>
    /// Plugin API'nin iş kısmı (controller'lar ince): Plugins kökü altında güvenli yol çözme, DLL listesi/tarama,
    /// görünüm modelleri ve DLL çalıştırılmadan çıkarılan özetlerin önbelleği (dosya değişince kendiliğinden yenilenir).
    /// </summary>
    public sealed class PluginApiService
    {
        private readonly ConcurrentDictionary<string, PluginDescriptor> _structureCache = new(StringComparer.OrdinalIgnoreCase);

        public PluginManager Manager { get; }
        public PluginsRootConfigStore Store { get; }
        public EvokerPluginsApiOptions Options { get; }
        /// <summary>Plugins klasörünün tam yolu.</summary>
        public string PluginsRoot => Store.RootPath;
        public string RegistrationsFile { get; }
        public string? WorkerPath { get; }

        public PluginApiService(PluginManager manager, PluginsRootConfigStore store, EvokerPluginsApiOptions options,
            string registrationsFile, string? workerPath)
        {
            Manager = manager;
            Store = store;
            Options = options;
            RegistrationsFile = registrationsFile;
            WorkerPath = workerPath;
        }

        // ============================ Yollar ============================

        /// <summary>
        /// Plugins köküne göre verilen yolu tam yola çevirir. Kökün dışına çıkan yollar ("../", başka disk) reddedilir
        /// (BadRequest). mustExist=true ise dosya yoksa TargetNotFound.
        /// </summary>
        public string ResolvePath(string? relativePath, bool mustExist = true)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new EvokerCommandException(EvokerErrorCodes.BadRequest, "'path' boş olamaz (Plugins klasörüne göre DLL yolu, ör. \"Siparis/1.0/Siparis.dll\").");
            var p = relativePath.Trim().Replace('\\', '/').TrimStart('/');
            var full = Path.GetFullPath(Path.Combine(PluginsRoot, p.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnderRoot(full))
                throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"'{relativePath}' Plugins klasörünün dışında - sadece Plugins altındaki DLL'ler kullanılabilir.");
            if (mustExist && !File.Exists(full))
                throw new EvokerCommandException(EvokerErrorCodes.TargetNotFound, $"'{p}' Plugins klasöründe bulunamadı.");
            return full;
        }

        public bool IsUnderRoot(string fullPath)
        {
            var root = PluginsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(root, cmp);
        }

        public string ToRelative(string fullPath) => Store.ToRelativePath(fullPath);

        // ============================ Dosyalar ============================

        public List<PluginFileView> ListFiles(string? search)
        {
            if (!Directory.Exists(PluginsRoot)) return new List<PluginFileView>();
            var regs = Manager.GetStatuses();
            var list = new List<PluginFileView>();
            foreach (var file in Directory.EnumerateFiles(PluginsRoot, "*.dll", SearchOption.AllDirectories))
            {
                var rel = ToRelative(file);
                if (!string.IsNullOrWhiteSpace(search) && rel.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var fi = new FileInfo(file);
                string? asmName = null, asmVersion = null;
                var kind = PluginScanner.DetectKind(file);
                if (kind == PluginKind.ManagedDotNet)
                {
                    try
                    {
                        var an = AssemblyName.GetAssemblyName(file);
                        asmName = an.Name;
                        asmVersion = an.Version?.ToString();
                    }
                    catch { /* bozuk metadata: Kind yine görünür */ }
                }
                var users = regs.Where(r => SamePath(r.FilePath, file)).Select(RefView).ToList();
                int slash = rel.LastIndexOf('/');
                list.Add(new PluginFileView
                {
                    Path = rel,
                    Folder = slash < 0 ? "" : rel.Substring(0, slash),
                    FileName = fi.Name,
                    Size = fi.Length,
                    LastWriteUtc = fi.LastWriteTimeUtc,
                    Kind = kind,
                    AssemblyName = asmName,
                    AssemblyVersion = asmVersion,
                    Registrations = users.Count == 0 ? null : users
                });
            }
            return list.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>DLL'i çalıştırmadan tarar. typeName verilirse o tipin tam tanımı da (samples ile) eklenir.</summary>
        public PluginFileScanView ScanFile(string fullPath, string? typeName, bool samples, bool nonPublic)
        {
            var rel = ToRelative(fullPath);
            var scan = PluginScanner.Scan(fullPath);
            var errors = scan.Errors.Select(e => (e.TypeName != null ? e.TypeName + ": " : "") + e.Message).ToList();
            if (scan.Kind != PluginKind.ManagedDotNet)
                return new PluginFileScanView { Path = rel, Kind = scan.Kind, Errors = errors.Count > 0 ? errors : new List<string> { "Bir .NET assembly'si değil - şu an sadece managed plugin'ler destekleniyor." } };

            IEnumerable<PluginTypeInfo> types = scan.Types;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                types = types.Where(t => string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!types.Any())
                    throw new EvokerCommandException(EvokerErrorCodes.TargetNotFound, $"'{typeName}' tipi '{rel}' içinde bulunamadı (ya da public değil).");
            }

            var regs = Manager.GetStatuses();
            PluginAssemblyDescriptor? asm = null;
            var views = new List<PluginTypeScanView>();
            foreach (var t in types)
            {
                PluginDescriptor structure;
                try { structure = DescribeStructure(fullPath, t.FullName); }
                catch (Exception ex) { errors.Add($"{t.FullName}: {ex.Message}"); continue; }
                asm ??= structure.Assembly;

                bool canRegister = structure.Type.Kind is EvokerTypeKind.Class or EvokerTypeKind.Record or EvokerTypeKind.Struct;
                var ctorTemplates = structure.Type.Constructors?
                    .Where(c => c.Visibility == MemberVisibility.Public)
                    .Select(c => new PluginConstructorTemplate
                    {
                        Signature = ConstructorSignature(structure.Type.Name, c),
                        ConstructorArgs = c.Parameters is { Count: > 0 } ? c.Sample : null
                    }).ToList();

                EvokerTypeDescriptor? full = null;
                if (!string.IsNullOrWhiteSpace(typeName))
                    full = PluginInspector.Describe(fullPath, t.FullName,
                        new PluginDescribeOptions { IncludeNonPublic = nonPublic, IncludeSamples = samples, IncludeReferences = false }).Type;

                var users = regs.Where(r => SamePath(r.FilePath, fullPath) && string.Equals(r.TypeFullName, t.FullName, StringComparison.Ordinal))
                    .Select(RefView).ToList();
                views.Add(new PluginTypeScanView
                {
                    FullName = t.FullName,
                    Kind = structure.Type.Kind,
                    CanRegister = canRegister,
                    Summary = Summarize(structure.Type),
                    ConstructorTemplates = canRegister && ctorTemplates is { Count: > 0 } ? ctorTemplates : null,
                    RegisterTemplate = canRegister ? RegisterTemplate(rel, structure.Type) : null,
                    Type = full,
                    Registrations = users.Count == 0 ? null : users
                });
            }
            return new PluginFileScanView
            {
                Path = rel,
                Kind = scan.Kind,
                Assembly = asm,
                Types = views.OrderByDescending(v => v.CanRegister).ThenByDescending(v => v.Summary?.Methods.Count ?? 0).ThenBy(v => v.FullName).ToList(),
                Errors = errors.Count > 0 ? errors : null
            };
        }

        /// <summary>
        /// TypeName verilmediğinde DLL'deki plugin tipi: kaydedilebilir tek tip, ya da public metodu olan tek tip
        /// (DTO'lar elenir). Birden çoksa ya da hiç yoksa açıklayıcı hata (aday listesiyle).
        /// </summary>
        public string PickSingleType(string fullPath)
        {
            var scan = ScanFile(fullPath, null, samples: false, nonPublic: false);
            var candidates = scan.Types.Where(t => t.CanRegister).Select(t => t.FullName).ToList();
            if (candidates.Count == 1) return candidates[0];
            // DTO'lar / EventArgs'lar da sınıf - public metodu olan TEK tip varsa servis odur
            var services = scan.Types.Where(t => t.CanRegister && t.Summary is { Methods.Count: > 0 }).Select(t => t.FullName).ToList();
            if (services.Count == 1) return services[0];
            if (services.Count > 1) candidates = services;
            if (candidates.Count == 0)
                throw new EvokerCommandException(EvokerErrorCodes.InvalidArguments, $"'{scan.Path}' içinde plugin olarak kaydedilebilecek bir tip yok.");
            throw new EvokerCommandException(EvokerErrorCodes.InvalidArguments,
                $"'{scan.Path}' içinde birden fazla tip var - 'typeName' verin: {string.Join(", ", candidates)}");
        }

        // ============================ Görünümler ============================

        public PluginView View(Guid key, bool includeSummary)
        {
            var s = Manager.GetStatus(key);
            var r = Manager.GetRegistrationCopy(key);
            PluginSummary? summary = null;
            if (includeSummary && File.Exists(r.FilePath))
            {
                try { summary = Summarize(DescribeStructure(r.FilePath, r.TypeFullName).Type); }
                catch { /* özet alınamadıysa liste yine döner */ }
            }
            return new PluginView
            {
                Key = s.Key,
                Name = s.Name,
                DisplayName = s.DisplayName,
                Path = ToRelative(r.FilePath),
                FileExists = File.Exists(r.FilePath),
                TypeFullName = s.TypeFullName,
                AssemblyVersion = s.AssemblyVersion,
                Mode = s.Mode,
                IsActive = s.IsActive,
                State = s.State,
                IsRunning = s.IsRunning,
                ProcessId = s.ProcessId,
                Generation = s.Generation,
                LastError = s.LastError,
                LastErrorUtc = s.LastErrorUtc,
                LastCrashUtc = s.LastCrashUtc,
                LastCrashReason = s.LastCrashReason,
                LastUnloadReleasedMemory = s.LastUnloadReleasedMemory,
                UpdatedUtc = s.UpdatedUtc,
                Notes = s.Notes,
                Settings = new PluginSettingsView
                {
                    ConstructorArgs = r.ConstructorArgs is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? r.ConstructorArgs : null,
                    IncludeNonPublic = r.IncludeNonPublic,
                    MaxConcurrency = r.MaxConcurrency,
                    AutoRestartOnCrash = r.AutoRestartOnCrash,
                    HeartbeatIntervalMs = r.HeartbeatIntervalMs,
                    MissedHeartbeatsBeforeKill = r.MissedHeartbeatsBeforeKill,
                    DefaultTimeoutMs = r.DefaultTimeoutMs
                },
                Summary = summary,
                Actions = ActionsFor(s)
            };
        }

        private static List<string> ActionsFor(PluginStatus s)
        {
            var a = new List<string>();
            if (!s.IsActive) { a.Add("activate"); a.Add("delete"); a.Add("update"); return a; }
            a.Add("execute");
            a.Add(s.IsRunning ? "reload" : "load");
            if (s.IsRunning) a.Add("stop");
            a.Add("deactivate");
            a.Add(s.Mode == PluginExecutionMode.Sandbox ? "mode:InProcess" : "mode:Sandbox");
            a.Add("update");
            a.Add("delete");
            return a;
        }

        public static PluginRefView RefView(PluginStatus s) => new()
        {
            Key = s.Key,
            Name = s.Name,
            TypeFullName = s.TypeFullName,
            IsActive = s.IsActive,
            State = s.State
        };

        // ============================ Özet / şablon ============================

        // Yapı (değersiz, şablonlu, sadece public) - dosya yolu + son yazma zamanı + boyut ile önbellekli.
        // DİKKAT: dönen nesne paylaşılır, değiştirilmemeli (değerli tanımlar her seferinde yeniden üretilir).
        private PluginDescriptor DescribeStructure(string fullPath, string typeName)
        {
            var fi = new FileInfo(fullPath);
            string key = $"{fi.FullName}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{typeName}";
            if (_structureCache.TryGetValue(key, out var d)) return d;
            d = PluginInspector.Describe(fullPath, typeName,
                new PluginDescribeOptions { IncludeNonPublic = false, IncludeSamples = true, IncludeReferences = true });
            if (_structureCache.Count > 512) _structureCache.Clear();
            _structureCache[key] = d;
            return d;
        }

        public static PluginSummary Summarize(EvokerTypeDescriptor t)
        {
            var methods = (t.Methods ?? new()).Where(m => m.Visibility == MemberVisibility.Public).ToList();
            var fields = (t.Fields ?? new()).Where(f => f.Visibility == MemberVisibility.Public)
                .Select(f => $"{(f.IsConst == true ? "const " : f.IsStatic == true ? "static " : "")}{(f.IsReadOnly == true ? "readonly " : "")}{f.Type} {f.Name}"
                             + (f.IsConst == true ? " = " + FormatValue(f.ConstValue) : "")).ToList();
            var events = (t.Events ?? new()).Where(e => e.Visibility == MemberVisibility.Public)
                .Select(e => $"{(e.IsStatic == true ? "static " : "")}event {e.HandlerType} {e.Name}").ToList();
            return new PluginSummary
            {
                Kind = t.Kind,
                Constructors = (t.Constructors ?? new()).Where(c => c.Visibility == MemberVisibility.Public)
                    .Select(c => ConstructorSignature(t.Name, c)).ToList(),
                Methods = methods.Select(m => (m.IsStatic == true ? "static " : "") + (m.Signature ?? m.Name)).ToList(),
                Properties = (t.Properties ?? new())
                    .Where(p => p.Getter == MemberVisibility.Public || p.Setter == MemberVisibility.Public)
                    .Select(PropertySignature).ToList(),
                Fields = fields.Count > 0 ? fields : null,
                Events = events.Count > 0 ? events : null,
                AsyncMethodCount = methods.Count(m => m.IsAsync == true)
            };
        }

        private static string PropertySignature(EvokerPropertyDescriptor p)
        {
            var acc = new List<string>();
            if (p.Getter == MemberVisibility.Public) acc.Add("get;");
            if (p.Setter == MemberVisibility.Public) acc.Add(p.IsInitOnly == true ? "init;" : "set;");
            else if (p.Setter != null) acc.Add(Lower(p.Setter.Value) + " set;");
            string name = p.IndexerParameters is { Count: > 0 } ip
                ? "this[" + string.Join(", ", ip.Select(x => x.Type + " " + x.Name)) + "]"
                : p.Name;
            return $"{(p.IsStatic == true ? "static " : "")}{p.Type} {name} {{ {string.Join(" ", acc)} }}";
        }

        private static string Lower(MemberVisibility v) => v switch
        {
            MemberVisibility.ProtectedInternal => "protected internal",
            MemberVisibility.PrivateProtected => "private protected",
            _ => v.ToString().ToLowerInvariant()
        };

        public static string ConstructorSignature(string typeName, EvokerConstructorDescriptor c) =>
            "new " + typeName + "(" + string.Join(", ", (c.Parameters ?? new()).Select(ParameterText)) + ")";

        private static string ParameterText(EvokerParameterDescriptor p)
        {
            string s = (p.IsParams == true ? "params " : "") + (p.Direction is { } d && d != EvokerParameterDirection.In ? d.ToString().ToLowerInvariant() + " " : "")
                       + p.Type + " " + p.Name;
            if (p.IsOptional == true) s += " = " + (p.HasDefaultValue == true ? FormatValue(p.DefaultValue) : "default");
            return s;
        }

        private static string FormatValue(object? v) => v switch
        {
            null => "null",
            string s => "\"" + s + "\"",
            bool b => b ? "true" : "false",
            char ch => "'" + ch + "'",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? ""
        };

        /// <summary>POST api/plugins için doldurulacak gövde: en az parametreli public constructor'ın şablonuyla.</summary>
        private static JsonElement RegisterTemplate(string relativePath, EvokerTypeDescriptor t)
        {
            var ctor = (t.Constructors ?? new())
                .Where(c => c.Visibility == MemberVisibility.Public)
                .OrderBy(c => c.Parameters?.Count(p => p.IsOptional != true) ?? 0)
                .FirstOrDefault();
            bool needsArgs = ctor?.Parameters != null && ctor.Parameters.Any(p => p.IsOptional != true);
            var o = new Dictionary<string, object?>
            {
                ["path"] = relativePath,
                ["typeName"] = t.FullName,
                ["name"] = "",
                ["mode"] = "Sandbox",
                ["isActive"] = true
            };
            if (needsArgs && ctor!.Sample is { } sample) o["constructorArgs"] = sample;
            return JsonSerializer.SerializeToElement(o);
        }

        // ============================ Yardımcılar ============================

        public static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}