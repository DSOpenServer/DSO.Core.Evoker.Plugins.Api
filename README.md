# DSO.Core.Evoker.Plugins.Api

> **Plugin yönetimi için hazır bir arka uç: tara, kaydet, aktif et, çalıştır, izle. Tek satırla.**

![.NET](https://img.shields.io/badge/.NET-6.0%20%7C%208.0-512BD4) ![E2E](https://img.shields.io/badge/u%C3%A7tan%20uca-81%20kontrol-success)

`DSO.Core.Evoker.Plugins.Api`, [DSO.Core.Evoker.Plugins](../DSO.Core.Evoker.Plugins/README.md)'ın `PluginManager`'ını
eksiksiz bir REST arka ucu olarak sunar. Uygulama klasörünüzdeki `Plugins` dizinine DLL bırakırsınız; gerisini API
halleder:

- DLL'leri **çalıştırmadan** tarar;
- hazır kayıt gövdesini üretir;
- sandbox ya da in-process modda çalıştırır;
- canlı mod geçişi yapar;
- sürüm değiştirir;
- komut çalıştırır;
- her şeyi tek tip bir JSON zarfıyla raporlar.

Bir yönetim paneli, bir otomasyon betiği ya da başka bir mikroservis bu uçlarla plugin dünyanızın tamamını yönetebilir.

---

## İçindekiler

- [Neden Plugins.Api?](#neden-pluginsapi)
- [Kurulum](#kurulum)
- [Klasör düzeni](#klasör-düzeni)
- [60 saniyede Plugins.Api](#60-saniyede-pluginsapi)
- [Uçlar](#uçlar)
- [Tipik akış](#tipik-akış)
- [Ayarlar](#ayarlar)
- [Cevap modelleri](#cevap-modelleri)
- [Programatik kullanım: PluginApiService](#programatik-kullanım-pluginapiservice)
- [Demo ve testler](#demo-ve-testler)
- [Notlar](#notlar)

---

## Neden Plugins.Api?

| Kendi yönetim API'nizi yazmak | Plugins.Api |
|---|---|
| Dosya listeleme, tarama, doğrulama, kayıt, durum makinesi, hata eşleme… haftalar süren iş | `AddEvokerPluginsApi()`: **15 uç** hazır ve uçtan uca testli |
| Kayıt formunu elle tasarlamak | `scan` ucu her tip için **doldurulmaya hazır kayıt gövdesi** ve her constructor için argüman şablonu verir |
| Plugin'in neler yapabildiğini dokümante etmek | Liste ucu, plugin **pasifken bile** public yüzeyin özetini (constructor, metot, property, event imzaları) verir |
| Komut ekranı için her metoda form | Detay ucu `?samples=true` ile her metot için hazır JSON komut verir |
| Yol güvenliği | `Plugins` kökü dışına çıkan her yol (`../`, başka disk) reddedilir |
| Taşınabilirlik | Kayıtlarda yollar `Plugins`'e **göreli** saklanır; klasörü taşıyın, kayıtlar çalışmaya devam eder |

---

## Kurulum

```xml
<ProjectReference Include="..\DSO.Core.Evoker.Plugins.Api\DSO.Core.Evoker.Plugins.Api.csproj" />
```

Bu paket [DSO.Core.Evoker.Api](../DSO.Core.Evoker.Api/README.md) ve
[DSO.Core.Evoker.Plugins](../DSO.Core.Evoker.Plugins/README.md)'a referans verir. Hedefler: `net6.0`, `net8.0`.

---

## Klasör düzeni

Varsayılan düzen, uygulamanızın çıktı klasörüdür (`bin/…/net6.0` ya da yayın klasörü):

```
<uygulama klasörü>/
  MyApi.exe, MyApi.dll, appsettings.json, DSO.Core.Evoker*.dll ...
  Host/        sandbox worker'ı: DSO.Core.Evoker.PluginHost (+ bağımlılıkları)
  Plugins/     plugin DLL'leri - alt klasörler serbest
     Siparis/1.0/Siparis.dll
     Stok/v1/Stok.dll
     Stok/v2/Stok.dll
  App_Data/    plugins.json (kayıtlar), crash.log
```

`Host/` klasörünü build sırasında doldurmak için uygulama projenize şu target'ı ekleyin. DemoApi'de hazır bir örneği var:

```xml
<PropertyGroup>
  <WorkerProject>..\DSO.Core.Evoker.PluginHost\DSO.Core.Evoker.PluginHost.csproj</WorkerProject>
</PropertyGroup>
<Target Name="CopyWorkerToHost" AfterTargets="Build">
  <MSBuild Projects="$(WorkerProject)" Targets="Restore" Properties="Configuration=$(Configuration);_EvokerRestore=true" RemoveProperties="TargetFramework;RuntimeIdentifier" />
  <MSBuild Projects="$(WorkerProject)" Targets="Build" Properties="Configuration=$(Configuration);TargetFramework=$(TargetFramework)" RemoveProperties="RuntimeIdentifier">
    <Output TaskParameter="TargetOutputs" ItemName="_WorkerOutput" />
  </MSBuild>
  <PropertyGroup><_WorkerDir>@(_WorkerOutput->'%(RootDir)%(Directory)')</_WorkerDir></PropertyGroup>
  <ItemGroup><_WorkerFiles Include="$(_WorkerDir)**\*" /></ItemGroup>
  <Copy SourceFiles="@(_WorkerFiles)" DestinationFiles="@(_WorkerFiles->'$(OutDir)Host\%(RecursiveDir)%(Filename)%(Extension)')" SkipUnchangedFiles="true" />
</Target>
```

---

## 60 saniyede Plugins.Api

```csharp
using DSO.Core.Evoker.Api;
using DSO.Core.Evoker.Plugins.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddEvokerApi(o => o.AllowTypesFrom = builder.Configuration.GetSection("Evoker:AllowTypesFrom").Get<List<string>>() ?? new())
    .AddEvokerPluginsApi(o => builder.Configuration.GetSection("Evoker:Plugins").Bind(o));

var app = builder.Build();
app.MapControllers();
app.Run();
```

```json
{
  "Evoker": {
    "AllowTypesFrom": [],
    "Plugins": {
      "PluginsRoot": "Plugins",
      "RegistrationsFile": "App_Data/plugins.json",
      "WorkerPath": "",
      "WarmStart": true
    }
  }
}
```

Bu kadar. Uygulama açılırken kayıtlar okunur ve aktif plugin'ler arka planda yüklenir; kapanırken tüm worker'lar
düzgünce kapatılır.

> `AddEvokerApi(...)`'yi kendi ayarlarınızla çağıracaksanız `AddEvokerPluginsApi`'den **önce** çağırın.
> `AddEvokerPluginsApi` her zaman `AddEvokerApi()`'yi varsayılan ayarlarla da çağırır; ayarlarda ilk kayıt geçerli olur.

---

## Uçlar

### Plugin dosyaları — `api/plugin-files`

| Uç | Açıklama |
|---|---|
| `GET /api/plugin-files?search=` | `Plugins` altındaki tüm DLL'ler (alt klasörler dahil): göreli yol, klasör, boyut, tarih, tür (`ManagedDotNet` / `Native` / `Unknown`), assembly adı ve sürümü, **bu dosyayı kullanan kayıtlar** |
| `GET /api/plugin-files/scan?path=Siparis/1.0/Siparis.dll` | DLL'i **çalıştırmadan** tarar. Her tip için `canRegister`, public yüzey özeti, constructor şablonları, **hazır kayıt gövdesi** (`registerTemplate`) ve mevcut kayıtlar. Servis tipleri DTO'ların önünde sıralanır |
| `GET /api/plugin-files/scan?path=…&type=Siparis.SiparisServisi&samples=true&nonPublic=false` | Bir tipin tam tanımı ve her üye için hazır komut şablonu |

### Plugin kayıtları — `api/plugins`

| Uç | Açıklama |
|---|---|
| `GET /api/plugins?active=&state=&mode=&search=&summary=` | Kayıtlar: durum, ayarlar, **public yüzey özeti** (pasifken de), uygulanabilir işlemler (`actions`) |
| `GET /api/plugins/system` | Plugins kökü, kayıt dosyası, worker yolu, `sandboxAvailable`, çalışma ortamı, process id, durum ve mod sayıları, izin listesi |
| `POST /api/plugins` | Kayıt. **201 + Location**. Doğrulama hatasında **422** (kayıt eklenmez) |
| `GET /api/plugins/{key}?samples=true&values=true&nonPublic=false` | Detay: durum + ayarlar + assembly bilgisi + tam tip tanımı + (çalışıyorsa) o anki değerler + komut şablonları |
| `PATCH /api/plugins/{key}` | Güncelleme: `name`, `notes`, `path` (sürüm değişimi), `typeName`, `constructorArgs`, `mode` (canlı), `isActive`, `includeNonPublic`, `maxConcurrency`, `autoRestartOnCrash`, `heartbeatIntervalMs`, `missedHeartbeatsBeforeKill`, `defaultTimeoutMs` |
| `DELETE /api/plugins/{key}` | Kaydı sil (yüklüyse önce boşaltılır) |
| `POST /api/plugins/{key}/activate` · `/load` | Aktif et ve yükle. Yüklenemezse **422** + sebep + güncel durum |
| `POST /api/plugins/{key}/deactivate` | Pasif yap: bellekten atılır, listede kalır |
| `POST /api/plugins/{key}/reload` | Yeniden yükle (DLL değiştiyse). Pasifse **409** |
| `POST /api/plugins/{key}/stop` | Bellekten at ama aktif bırak; ilk çağrıda kendiliğinden yüklenir |
| `PUT /api/plugins/{key}/mode` | `{ "mode": "InProcess" }`: **canlı** mod geçişi |
| `POST /api/plugins/{key}/execute` | JSON komut ([biçim](../DSO.Core.Evoker/README.md#commands--json-komutlar)). Pasifse **409 Inactive** |

Plugin'ler aynı Guid ile genel katalogda da (`Kind = "Plugin"`) bulunur. Bu yüzden
[`api/evoker/targets/{key}/execute`](../DSO.Core.Evoker.Api/README.md#uçlar) da çalışır.

---

## Tipik akış

```http
### 1) Ne var?
GET /api/plugin-files

### 2) DLL'de neler var? (çalıştırmadan) - cevaptaki registerTemplate'i kopyalayın
GET /api/plugin-files/scan?path=Siparis/1.0/Siparis.dll

### 3) Kaydet (pasif)
POST /api/plugins
Content-Type: application/json

{
  "path": "Siparis/1.0/Siparis.dll",
  "typeName": "Siparis.SiparisServisi",
  "name": "Merkez mağaza siparişleri",
  "mode": "Sandbox",
  "isActive": false,
  "constructorArgs": { "magazaAdi": "Merkez", "kdvOrani": 0.20, "ayarlar": { "maxSatir": 10 } }
}

### 4) Komut şablonlarını gör (pasifken bile)
GET /api/plugins/{key}?samples=true

### 5) Aktif et
POST /api/plugins/{key}/activate

### 6) Çalıştır - iç içe nesneler, isimli argümanlar
POST /api/plugins/{key}/execute
Content-Type: application/json

{ "op": "invoke", "member": "Olustur",
  "args": { "musteri": { "kod": "C001", "adres": { "il": "İzmir" } },
            "satirlar": [ { "urunKodu": "KLM-001", "adet": 2, "birimFiyat": 100 } ] } }

### 7) Güveniyorum: canlı olarak içeri al
PUT /api/plugins/{key}/mode
Content-Type: application/json

{ "mode": "InProcess" }

### 8) Yeni sürüme geç (aynı kayıt, aynı anahtar)
PATCH /api/plugins/{key}
Content-Type: application/json

{ "path": "Siparis/1.1/Siparis.dll" }
```

**Kayıt kuralları:**

- `isActive` varsayılanı **false**'tur: kayıt pasif eklenir, "aktif et" denince yüklenir. `scan` ucunun verdiği
  `registerTemplate` ise hemen kullanılsın diye `isActive: true` ile gelir.
- `typeName` boş bırakılırsa DLL'deki **tek** kaydedilebilir tip seçilir. Birden fazla varsa public metodu olan tek tip
  seçilir; DTO'lar ve EventArgs'lar elenir. Yine belirsizse 422 döner ve adaylar listelenir.
- `path` hem `/` hem `\` ayraçlarıyla yazılabilir ve `Plugins`'e göre çözülür.

**PATCH kuralları:**

- Bilinmeyen bir alan **400** ile reddedilir; kabul edilen alanlar hata mesajında listelenir.
- Geçersiz bir değişiklik **422** döner ve kayıt **bozulmaz**.
- Sadece `name` ya da `notes` değiştiyse plugin yeniden başlatılmaz.

---

## Ayarlar

`EvokerPluginsApiOptions` (`appsettings.json` → `Evoker:Plugins`):

| Ayar | Varsayılan | Açıklama |
|---|---|---|
| `PluginsRoot` | `"Plugins"` | DLL kök klasörü. Göreli yollar **uygulama klasörüne** göredir |
| `RegistrationsFile` | `"App_Data/plugins.json"` | Kayıt dosyası |
| `WorkerPath` | boş | Boşsa sırayla `Host/`, uygulama klasörü ve `worker/` altında `DSO.Core.Evoker.PluginHost.dll`/`.exe` aranır. `.exe` doğrudan, `.dll` `dotnet` ile başlatılır |
| `WarmStart` | `true` | Aktif plugin'ler açılışta arka planda yüklenir |
| `StartupTimeoutMs` | `15000` | Worker el sıkışma süresi |
| `CrashLogFile` | `"App_Data/crash.log"` | Çökme kaydı; boş bırakılırsa yazılmaz |
| `ListIncludesSummary` | `true` | Listede public yüzey özeti (`?summary=` ile istek bazında değişir) |

`AddEvokerPluginsApi` şu kayıtları yapar:

- `EvokerPluginsApiOptions`, `PluginApiService` ve `PluginManager`: hepsi singleton. Manager aynı `EvokerCatalog`'u
  kullanır.
- Uygulama yaşam döngüsüne bağlı bir hosted service: açılışta `InitializeAsync`, kapanışta `DisposeAsync` çağırır ve
  çökmeleri loglar.
- Controller'lar.

---

## Cevap modelleri

Tüm cevaplar [Evoker zarfını](../DSO.Core.Evoker.Api/README.md#cevap-zarfı-ve-http-kodları) kullanır:
`{ success, result, message, error, elapsedMs }`.

### PluginView (liste ve işlem cevapları)

```jsonc
{
  "key": "7eb25062-…",
  "name": "Merkez mağaza",
  "displayName": "Merkez mağaza (Siparis.SiparisServisi)",
  "path": "Siparis/1.0/Siparis.dll",
  "fileExists": true,
  "typeFullName": "Siparis.SiparisServisi",
  "assemblyVersion": "1.0.0.0",
  "mode": "Sandbox",
  "isActive": true,
  "state": "Running",                    // Inactive | Stopped | Starting | Running | Faulted | Crashed
  "isRunning": true,
  "processId": 7426,
  "generation": 1,
  "updatedUtc": "2026-10-06T10:12:00Z",
  "settings": {
    "constructorArgs": { "magazaAdi": "Merkez", "kdvOrani": 0.2 },
    "includeNonPublic": false,
    "maxConcurrency": 1,
    "autoRestartOnCrash": false,
    "heartbeatIntervalMs": 5000,
    "missedHeartbeatsBeforeKill": 3
  },
  "summary": {
    "kind": "Class",
    "constructors": [ "new SiparisServisi(string magazaAdi, decimal kdvOrani = 0.20, SiparisAyarlari ayarlar = null)" ],
    "methods": [ "Siparis Olustur(Musteri musteri, List<SiparisSatiri> satirlar)", "Task<Siparis> OnaylaAsync(int no)", "…" ],
    "properties": [ "decimal KdvOrani { get; set; }", "int SiparisSayisi { get; }", "…" ],
    "events": [ "event EventHandler<DurumDegistiEventArgs> DurumDegisti" ],
    "asyncMethodCount": 5
  },
  "actions": [ "execute", "reload", "stop", "deactivate", "mode:InProcess", "update", "delete" ]
}
```

`actions` alanı, bir yönetim ekranının hangi butonları göstereceğini doğrudan söyler.

### Diğer modeller

| Model | İçerik |
|---|---|
| `PluginFileView` | `path`, `folder`, `fileName`, `size`, `lastWriteUtc`, `kind`, `assemblyName`, `assemblyVersion`, `registrations` |
| `PluginFileScanView` | `path`, `kind`, `assembly`, `types[]`, `errors` |
| `PluginTypeScanView` | `fullName`, `kind`, `canRegister`, `summary`, `constructorTemplates[]` (`signature` + `constructorArgs`), `registerTemplate`, `type` (tam tanım), `registrations` |
| `PluginDetailView` | `plugin` (PluginView), `assembly`, `type` (EvokerTypeDescriptor), `values`, `warnings` |
| `PluginRegisterRequest` | `path`, `typeName`, `name`, `mode`, `isActive`, `constructorArgs`, `includeNonPublic`, `maxConcurrency`, `autoRestartOnCrash`, `heartbeatIntervalMs`, `missedHeartbeatsBeforeKill`, `defaultTimeoutMs`, `notes` |

---

## Programatik kullanım: PluginApiService

Uçların arkasındaki servis DI'dan alınıp kendi kodunuzda da kullanılabilir.

```csharp
public sealed class PanelServisi
{
    private readonly PluginApiService _api;
    public PanelServisi(PluginApiService api) => _api = api;

    public void Ornekler()
    {
        PluginManager mgr = _api.Manager;                       // tüm PluginManager API'si
        string kok = _api.PluginsRoot;
        string? worker = _api.WorkerPath;

        string tam = _api.ResolvePath("Stok/v2/Stok.dll");      // kök dışı -> BadRequest, yoksa -> TargetNotFound
        string goreli = _api.ToRelative(tam);
        bool icerde = _api.IsUnderRoot(tam);

        List<PluginFileView> dosyalar = _api.ListFiles(search: "Stok");
        PluginFileScanView tarama = _api.ScanFile(tam, typeName: null, samples: true, nonPublic: false);
        string tip = _api.PickSingleType(tam);                  // "Stok.StokServisi"
        PluginView gorunum = _api.View(key, includeSummary: true);

        PluginSummary ozet = PluginApiService.Summarize(tanim);  // EvokerTypeDescriptor -> okunur imzalar
    }
}
```

DLL çalıştırılmadan çıkarılan yapı, dosya yolu + değişiklik zamanı + boyut anahtarıyla önbelleklenir. Dosya
değiştiğinde önbellek kendiliğinden yenilenir.

---

## Demo ve testler

### DSO.Core.Evoker.Plugins.DemoApi

Çalışır örnek Web API (.NET 6, Swagger dahil). Build sırasında `Host/` ve `Plugins/` klasörlerini kendisi doldurur.
Örnek plugin'leri ve worker'ı restore edip derler, sonra kopyalar; solution'a eklenmeleri gerekmez. Örnek plugin'ler:

| Plugin | Dil | Gösterdikleri |
|---|---|---|
| **Siparis** `Siparis/1.0` | C# | Parametreli constructor (metin + optional decimal + iç içe ayar nesnesi), iç içe DTO'lar, enum, overload, `Task<T>`, `ValueTask<T>`, `Task.WhenAll` ile paralel tedarikçi sorgusu, 1000'e kadar paralel sipariş, event, doğrulama hataları |
| **Stok** `Stok/v1` | VB.NET | Tüm parametreleri `Optional` constructor, büyük/küçük harf duyarsız üye adları, `Sub` / `Function` |
| **Stok** `Stok/v2` | VB.NET | **Aynı assembly adı**, yeni sürüm: `Async Function` (depolar paralel), `ParamArray`, event, okunur/yazılır property |
| **Rapor** `Rapor/1.0` | C# | Hangi process'te ve hangi context'te çalıştığı (`Ortam()`), timeout, özel exception, `Environment.FailFast` ile **gerçek çökme** (in-process'te güvenlik için reddedilir), iç içe istek nesnesi |

Program.cs ayrıca dört plugin olmayan örnek hedef kaydeder: Sayaç (Singleton), Sepet (Scoped), Hesap (Transient) ve
SunucuSaati (Static). `demo.http` dosyası tüm akışı adım adım içerir.

### DSO.Core.Evoker.Plugins.DemoApi.E2ETest — 81 kontrol

Çalışan API'ye HTTP ile bağlanır, kendi oluşturduğu kayıtları sonunda siler. Kapsadığı senaryolar:

1. **Sistem:** worker'ın bulunması, 4 örnek DLL ve sürümlerinin listelenmesi, tarama (servis tipinin en üstte olması,
   özet, kayıt şablonu, iç içe DTO şablonu), kök dışı yol → 400, olmayan dosya → 404.
2. **Kayıt:**
   - uymayan constructor → 422;
   - 201 + Location ile pasif kayıt;
   - pasife komut → 409;
   - pasifken liste özeti ve detay şablonları.
3. **Sandbox komutları:**
   - iç içe DTO + isimli argüman ve constructor'dan gelen KDV;
   - overload;
   - çok adımlı async akış;
   - `Task.WhenAll` paralelliği;
   - 40 paralel görev;
   - `ValueTask` + enum;
   - plugin exception → 500 + `exceptionType`;
   - setter doğrulaması;
   - olmayan üye → 404, uymayan argüman → 422, bozuk JSON → 400;
   - `?values=true` ile o anki değer.
4. **Canlı mod geçişi:** aynı komutun iki modda **aynı JSON sonucunu** üretmesi, state'in sıfırlanması, geçersiz mod →
   400.
5. **VB.NET:**
   - Optional parametreler ve büyük/küçük harf duyarsızlığı;
   - v1 ve v2'nin aynı anda çalışması;
   - Async + `Task.WhenAll`;
   - `ParamArray`;
   - PATCH ile v1 → v2 geçişi;
   - geçersiz PATCH'in kaydı bozmaması.
6. **Rapor:**
   - `typeName`'siz kayıt;
   - ayrı pid;
   - timeout → 504 (beklemeden);
   - özel exception;
   - CSV rapor;
   - **çökme** → Crashed → yeni pid ile toparlanma;
   - InProcess'te host pid'i;
   - çökertmenin reddi.
7. **Katalog:**
   - Singleton'a 20 paralel istek;
   - Scoped ve Transient ömürleri;
   - static sınıf;
   - `params`;
   - plugin'in genel katalog ucundan çalışması;
   - izin listesi (boş ya da dolu).
8. **Yaşam döngüsü:**
   - stop → otomatik yükleme;
   - reload;
   - deactivate;
   - InProcess plugin'in API üzerinden kullanıldıktan sonra **bellekten gerçekten boşalması**;
   - filtreler;
   - kayıt dosyasında göreli yollar.
9. **Temizlik:** silinen kaydın hem API'den hem katalogdan kalkması.

```
# 1. terminal
dotnet run --project DSO.Core.Evoker.Plugins.DemoApi
# 2. terminal
dotnet run --project DSO.Core.Evoker.Plugins.DemoApi.E2ETest      # beklenen: 81 OK, 0 HATA
```

Daha alt seviyedeki testler (parite, unload, manager, performans) için:
[DSO.Core.Evoker.Plugins → Testler](../DSO.Core.Evoker.Plugins/README.md#testler-ve-örnekler).

---

## Notlar

- **Kimlik doğrulama yoktur.** Uçlar sunucuda kod çalıştırır; üretimde mutlaka yetkilendirmenin arkasına alın.
- **Dosya kilidi (Windows):** yüklü bir DLL'in üzerine yazılamaz. Önce `stop` ya da `deactivate`, sonra dosyayı
  değiştirip `reload` ya da `activate` çağırın.
- **Yayın (publish):** `Host/` ve `Plugins/` klasörlerini yayın klasörüne de koyun; örnekteki target build çıktısını
  doldurur.
- **Komut sonuçları:** çekirdeğin serializer'ıyla yazılır; MVC'nin JSON önbelleği plugin tiplerini tutmaz. Bu sayede
  in-process plugin'ler unload edilebilir (E2E testinde doğrulanır).
