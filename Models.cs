using System.Text.Json;
using DSO.Core.Evoker.Description;
using DSO.Core.Evoker.Plugins.Management;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Api
{
    // ============================ İstekler ============================

    /// <summary>
    /// POST api/plugins gövdesi. Sadece Path zorunlu; DLL'de tek uygun tip varsa TypeName de boş bırakılabilir.
    /// <code>
    /// { "path": "Siparis/1.0/Siparis.dll", "typeName": "Siparis.SiparisServisi", "name": "Merkez mağaza siparişleri",
    ///   "mode": "Sandbox", "isActive": true, "constructorArgs": { "magazaAdi": "Merkez", "kdvOrani": 0.20 } }
    /// </code>
    /// Hazır gövde: GET api/plugin-files/scan?path=... cevabındaki registerTemplate.
    /// </summary>
    public sealed class PluginRegisterRequest
    {
        /// <summary>Plugins klasörüne göre yol ("Stok/v2/Stok.dll"; "\" de olur).</summary>
        public string? Path { get; set; }
        public string? TypeName { get; set; }
        /// <summary>Sadece açıklama - boş olabilir, tekrar edebilir. Anahtar her zaman sistemin ürettiği Guid'dir.</summary>
        public string? Name { get; set; }
        public PluginExecutionMode Mode { get; set; } = PluginExecutionMode.Sandbox;
        /// <summary>true: kayıttan hemen sonra yüklenir. Varsayılan false (pasif; "aktif et" denince yüklenir).</summary>
        public bool IsActive { get; set; }
        /// <summary>Constructor argümanları: dizi (sıralı) ya da nesne (parametre adlarıyla). Her yüklemede kullanılır.</summary>
        public JsonElement? ConstructorArgs { get; set; }
        public bool IncludeNonPublic { get; set; }
        public int? MaxConcurrency { get; set; }
        public bool? AutoRestartOnCrash { get; set; }
        public int? HeartbeatIntervalMs { get; set; }
        public int? MissedHeartbeatsBeforeKill { get; set; }
        public int? DefaultTimeoutMs { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>PUT api/plugins/{key}/mode gövdesi.</summary>
    public sealed class PluginModeRequest
    {
        public PluginExecutionMode? Mode { get; set; }
    }

    // ============================ Cevaplar ============================

    /// <summary>Plugins klasöründeki bir DLL.</summary>
    public sealed class PluginFileView
    {
        /// <summary>Plugins klasörüne göre yol ("/" ayraçlı).</summary>
        public string Path { get; init; } = "";
        public string Folder { get; init; } = "";
        public string FileName { get; init; } = "";
        public long Size { get; init; }
        public DateTime LastWriteUtc { get; init; }
        public PluginKind Kind { get; init; }
        public string? AssemblyName { get; init; }
        public string? AssemblyVersion { get; init; }
        /// <summary>Bu dosyayı kullanan kayıtlar.</summary>
        public List<PluginRefView>? Registrations { get; init; }
    }

    public sealed class PluginRefView
    {
        public Guid Key { get; init; }
        public string? Name { get; init; }
        public string TypeFullName { get; init; } = "";
        public bool IsActive { get; init; }
        public PluginState State { get; init; }
    }

    /// <summary>GET api/plugin-files/scan cevabı.</summary>
    public sealed class PluginFileScanView
    {
        public string Path { get; init; } = "";
        public PluginKind Kind { get; init; }
        public PluginAssemblyDescriptor? Assembly { get; init; }
        public List<PluginTypeScanView> Types { get; init; } = new();
        public List<string>? Errors { get; init; }
    }

    public sealed class PluginTypeScanView
    {
        public string FullName { get; init; } = "";
        public EvokerTypeKind Kind { get; init; }
        /// <summary>Plugin olarak kaydedilebilir mi (sınıf/record/struct; abstract/static/interface/enum değil).</summary>
        public bool CanRegister { get; init; }
        public PluginSummary? Summary { get; init; }
        /// <summary>Her constructor için doldurulacak constructorArgs şablonu (isimli).</summary>
        public List<PluginConstructorTemplate>? ConstructorTemplates { get; init; }
        /// <summary>POST api/plugins için hazır gövde (doldurup gönderin).</summary>
        public JsonElement? RegisterTemplate { get; init; }
        /// <summary>Tam tanım (?type= verildiyse).</summary>
        public EvokerTypeDescriptor? Type { get; init; }
        public List<PluginRefView>? Registrations { get; init; }
    }

    public sealed class PluginConstructorTemplate
    {
        public string Signature { get; init; } = "";
        public JsonElement? ConstructorArgs { get; init; }
    }

    /// <summary>
    /// Plugin'in public yüzeyinin okunur özeti - plugin yüklü olmasa da (DLL çalıştırılmadan) listede gösterilir.
    /// </summary>
    public sealed class PluginSummary
    {
        public EvokerTypeKind Kind { get; init; }
        public List<string> Constructors { get; init; } = new();
        public List<string> Methods { get; init; } = new();
        public List<string> Properties { get; init; } = new();
        public List<string>? Fields { get; init; }
        public List<string>? Events { get; init; }
        public int AsyncMethodCount { get; init; }
    }

    /// <summary>Plugin kaydının liste/detay görünümü: durum + ayarlar (+ özet / tanım).</summary>
    public sealed class PluginView
    {
        public Guid Key { get; init; }
        public string? Name { get; init; }
        public string DisplayName { get; init; } = "";
        /// <summary>Plugins klasörüne göre yol (kökün dışındaysa tam yol).</summary>
        public string Path { get; init; } = "";
        public bool FileExists { get; init; }
        public string TypeFullName { get; init; } = "";
        public string? AssemblyVersion { get; init; }
        public PluginExecutionMode Mode { get; init; }
        public bool IsActive { get; init; }
        public PluginState State { get; init; }
        public bool IsRunning { get; init; }
        public int? ProcessId { get; init; }
        public int? Generation { get; init; }
        public string? LastError { get; init; }
        public DateTime? LastErrorUtc { get; init; }
        public DateTime? LastCrashUtc { get; init; }
        public string? LastCrashReason { get; init; }
        public bool? LastUnloadReleasedMemory { get; init; }
        public DateTime UpdatedUtc { get; init; }
        public string? Notes { get; init; }
        public PluginSettingsView Settings { get; init; } = new();
        public PluginSummary? Summary { get; init; }
        /// <summary>Durumdan çıkan, bu plugin'e şu an uygulanabilecek işlemler (ör. "activate", "execute").</summary>
        public List<string> Actions { get; init; } = new();
    }

    public sealed class PluginSettingsView
    {
        public JsonElement? ConstructorArgs { get; init; }
        public bool IncludeNonPublic { get; init; }
        public int MaxConcurrency { get; init; }
        public bool AutoRestartOnCrash { get; init; }
        public int HeartbeatIntervalMs { get; init; }
        public int MissedHeartbeatsBeforeKill { get; init; }
        public int? DefaultTimeoutMs { get; init; }
    }

    /// <summary>GET api/plugins/{key} cevabı.</summary>
    public sealed class PluginDetailView
    {
        public PluginView Plugin { get; init; } = new();
        public PluginAssemblyDescriptor? Assembly { get; init; }
        public EvokerTypeDescriptor? Type { get; init; }
        public EvokerValuesInfo? Values { get; init; }
        public List<string>? Warnings { get; init; }
    }
}