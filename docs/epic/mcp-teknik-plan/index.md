---
kind: spec
title: "MCP — iki yüzey, tek protokol"
---

# MCP teknik planı

Ürüne **Model Context Protocol** desteği giriyor. İki ayrı yüzey, tek protokol
ve tek uyum kapısı:

| Yüzey | Kim kullanıyor | Ne yapıyor |
| --- | --- | --- |
| **`bizigo-sim`** | Geliştirici / ajan | Simülatörleri **çalışırken** yönetir: senaryo değiştir, filo bas, cihaz sustur |
| **`bizigo`** | Operatör / ajan / dış araç | Ürünün kendisi: log ara, alarm oku, RCA tetikle, kanıt paketi getir |

---

## 1 · Neden bu planın ilk cümlesi bir yasak

MCP bir **model** yüzeyi. Bu depoda modele giden her şey K6'nın ve T41/T42'nin
kapısından geçiyor, ve MCP o kapıyı **atlayabilecek** bir yol açıyor: bir
araç çağrısının dönüş değeri doğrudan modelin bağlamına giriyor.

> **MCP araçlarının log içeriği döndüren her çıktısı `RedactedPrompt`'tan
> geçmek zorunda.** Tip zaten bunu derleyiciye bağlıyor (T41: yapıcı
> `private`, yalnızca kapıdan çıkıyor); MCP katmanı `string` döndürüyorsa kapı
> **atlanmış** olur.

Ve K6 (T42) ikinci kapı: MCP istemcisi kurum dışındaysa hiçbir düzey geçmiyor.
MCP sunucusu kendi ağ sınırını **beyan etmek** zorunda, tıpkı model uçları
gibi — `Unspecified` reddediliyor.

Bu iki cümle planın geri kalanından önce yazıldı çünkü MCP'nin cazibesi tam
olarak burada: *"araç zaten veriyi getiriyor, modele de versin."*

---

## 2 · Protokol uyumu — neyin şart olduğu

Hedef **MCP 2.0** spesifikasyonuna tam uyum. Uyum bir iddia değil bir
**kapı** olmalı:

| Alan | Şart |
| --- | --- |
| Taşıma | `stdio` **ve** akışlanabilir HTTP. İkisi aynı araç kümesini sunuyor |
| Yetenek anlaşması | `initialize` el sıkışması, sürüm anlaşması, istemcinin desteklemediği yeteneğin **kullanılmaması** |
| Araçlar | JSON Schema girdi **ve** çıktı şeması; `outputSchema` yazılıysa çıktı ona **uyuyor** |
| Kaynaklar | URI şeması, abonelik, değişiklik bildirimi |
| İstemler | Parametreli, sunucu tarafında sürümlü |
| Hata | Protokol hatası ile **araç hatası** ayrı — araç hatası `isError` ile döner, protokol istisnası değil |
| İptal | `notifications/cancelled` **gerçekten** iptal ediyor |

> **Günlükleme bu tablodan çıkarıldı (M01).** İlk hâli `notifications/message`'ı
> bir şart olarak yazıyordu. Çivilediğimiz revizyonda (`2026-07-28`) o yetenek
> **kullanımdan kaldırıldı** (SEP-2577); C# SDK'sı onu okuyan kodu `MCP9005` ile
> işaretliyor ve depodaki `TreatWarningsAsErrors` bunu bir derleme hatasına
> çeviriyor — yani şart, kendi kapısını kuramıyor.
>
> Gerekçe yalnızca teknik değil. §8'in kuralı yetenek düzeyinde de geçerli:
> *tüketicisi olmayan bir tip tahmindir.* Bugün `notifications/message`'ı
> okuyacak bir tüketicimiz yok, ve kullanımdan kalkmış bir yeteneği tüketicisiz
> biçimde **yepyeni** bir yüzeye sokmak yarının borcunu bugün yazmak olurdu.
> Kapı tarafı daha da kötü: spesifikasyonun kaldırdığı bir şeyi şart koşan bir
> uyum kapısı, uyumu ölçmüyor — **kendi geçmişini** ölçüyor.
>
> Gerçek bir günlükleme ihtiyacı doğarsa ayrı bir ticket açılacak ve
> **tüketicisiyle** gelecek.

**Uyumun bekçisi bir sözleşme testi olacak**, elle yazılmış bir liste değil:
sunucunun ilan ettiği her araç için şemanın geçerliliği ve örnek çağrının
şemaya uyması sınanacak. Bu deponun `Produces<T>` dersi burada birebir
geçerli — elle tutulan bir araç listesi, listede olmayan aracı kapıya
görünmez yapar.

### İptal ve zaman aşımı bu üründe özel

Ürünün sorguları ClickHouse'a iniyor ve uzun sürebiliyor. `cancelled`
bildirimi **sorguyu gerçekten iptal etmezse** MCP katmanı kaynak sızdırır ve
istemci "iptal ettim" sanır. Bu, deponun *"sessiz yanlış davranış"* sınıfının
protokol katmanındaki hâli ve ayrı bir bekçi istiyor.

---

## 3 · CLI paritesi — ve neden kopya değil

Her MCP aracının bir CLI karşılığı olacak. Ama **iki uygulama yazılmayacak**:
ortak bir komut çekirdeği, iki sunum katmanı.

Gerekçe bu deponun beş kez ödediği ders: iki liste sessizce ayrışıyor. Bir
araç MCP'de var CLI'da yok ise fark **bir karar** olmalı, kaza değil — ve
bunu tutan bir bekçi gerekiyor:

> Komut çekirdeğindeki her komutun ya bir MCP aracı ya bir **gerekçeli
> muafiyeti** var; muafiyet sayısı sabit.

`Exempt` kalıbı: muafiyet eklemek iki ayrı bilinçli hareket istiyor.

---

## 4 · `bizigo-sim` — simülatör kontrolü

FS'in ürettiği simülatörler bugün **başlarken** yapılandırılıyor: profil,
senaryo, filo. MCP bunu **çalışırken** değiştirilebilir yapıyor.

| Araç | Ne yapıyor |
| --- | --- |
| `sim.fleet.list` | Filodaki cihazlar, grupları, o anki senaryoları |
| `sim.scenario.set` | Bir cihazın senaryosunu değiştir (`kural-eklendi`, `sir-dondu`, `saat-kaymasi` …) |
| `sim.scenario.list` | Tanımlı senaryolar ve **hangi yüzeye** ait oldukları |
| `sim.syslog.burst` | Sayılı satır bas, hız ve profil parametreli |
| `sim.device.silence` | Bir cihazı sustur — sessizlik korelasyonunun tek gerçek sınavı |
| `sim.webhook.emit` | İmzalı değişiklik olayı gönder (S07) |
| `sim.state` | Simülatörün o anki hâli: hangi cihaz hangi senaryoda, ne kadar süredir |

**S04'ün yüzey ayrımı burada da geçerli ve protokolde görünmeli:**
`sim.scenario.set` bir config senaryosunu syslog cihazına uygularsa hata
*"bu senaryo başka bir yüzeye ait"* demeli, *"profilde yok"* değil. Aynı
cümle, MCP hata gövdesinde.

**Bu yüzey ürün verisi döndürmüyor** — simülatör durumu döndürüyor. Yani §1'in
redaksiyon kapısı burada `sim.syslog.burst`'ün **bastığı satırlara** değil,
`sim.state`'in döndürdüğü örneklere bakıyor. Ayrım yazılı olmalı, yoksa kapı
ya gereksiz yere her şeyi maskeler ya hiçbir şeyi.

---

## 5 · `bizigo` — ürünün kendisi

| Araç | Karşılığı |
| --- | --- |
| `logs.search` | Log arama; kapsam filtresi **zorunlu** |
| `logs.context` | Bir olayın çevresindeki pencere |
| `alerts.list` · `alerts.get` | Alarm okuma |
| `rca.trigger` | `POST /v1/rca` — **dış API tetikleyicisi** (K20), `Idempotency-Key` şart |
| `rca.runs` | `GET /v1/rca/runs` — üç yönlü ayrım (T46) |
| `evidence.bundle` | Kanıt paketi; **LLM'siz okunabilir** olması K22'nin sınavı |
| `inventory.list` | Cihaz envanteri, kapsamıyla |
| `catalog.parsers` | Parser kataloğu ve kapsama oranı |

**Kaynaklar (resources) olarak sunulacaklar** araç değil veri: kanıt paketi
belgesi, RCA raporu, parser tanımı. Abonelik `rca.runs` için anlamlı — koşum
durum değiştirdiğinde bildirim.

**Kapsam her araçta.** Bu depoda kapsam bir sessiz-hata mıknatısı ve MCP yeni
bir kaçış yolu: bir aracın `owner_group` almayı unutması, bütün grupların
verisini modele açar. Kapsamın **tek kapıdan** geçtiğini tutan bekçi MCP
yüzeyini de kapsamalı.

---

## 6 · Kimlik

MCP istemcisi bir kullanıcı adına konuşuyor. `bizigo` yüzeyi Keycloak'tan
gelen kimliği **taşımak** zorunda; servis hesabıyla koşan bir MCP sunucusu
bütün kapsam kapılarını atlar.

Realm'de yalnızca `bizigo-claims` client scope var (`scope=openid` geçiyor,
`openid profile email` `invalid_scope` alıyor) — ölçüldü, MCP tarafı bunu
varsaymalı.

---

## 7 · Ticket'lar

| # | Ticket | Özü | Bağımlılık |
| --- | --- | --- | --- |
| **M01** | Protokol çekirdeği ve uyum kapısı | `initialize`, yetenek anlaşması, iki taşıma, sözleşme testi | — |
| **M02** | Komut çekirdeği ve CLI paritesi | Ortak çekirdek, iki sunum, muafiyet sabiti | M01 |
| **M03** | `bizigo-sim` araçları | §4'ün yedi aracı, yüzey hatası dahil | M01, FS-a |
| **M04** | `bizigo` okuma araçları | `logs.*`, `alerts.*`, `inventory.*`, `catalog.*` | M01, M02 |
| **M05** | `bizigo` RCA araçları | `rca.trigger`, `rca.runs`, `evidence.bundle` | M04, T46 |
| **M06** | Redaksiyon ve K6 kapısı | §1'in iki cümlesi mekanizmaya bağlanıyor | M04, T41, T42 |
| **M07** | Kaynaklar ve abonelik | Belge kaynakları, `rca.runs` bildirimi | M05 |
| **M08** | Kimlik taşıma | Keycloak kimliği MCP oturumundan uca | M04 |

**M06 sona bırakılmadı, sonda duruyor** — ama M04 ve M05 **onsuz sevk
edilmiyor**. Sıra bu: araçlar yazılır, kapı takılır, sonra ikisi birlikte
açılır. Kapıyı önce yazmak, tüketicisi olmayan bir tip yazmak olurdu (§8).

---

## 8 · Bitti tanımı

1. `initialize` el sıkışması **iki taşımada da** çalışıyor ve desteklenmeyen
bir yetenek **kullanılmıyor**.
2. Sunucunun ilan ettiği **her** aracın şeması geçerli ve örnek çağrısı
şemaya uyuyor — bekçi araçları **kendisi buluyor**, elle listeden değil.
3. `notifications/cancelled` uzun bir ClickHouse sorgusunu **gerçekten**
iptal ediyor; ölçüldü.
4. Komut çekirdeğindeki her komut ya MCP aracı ya **gerekçeli muafiyet**;
muafiyet sayısı sabitle tutuluyor.
5. Log içeriği döndüren hiçbir MCP aracı `RedactedPrompt` kapısını atlamıyor —
ve bu **derleyiciye** bağlı, bir çağrı alışkanlığına değil.
6. MCP sunucusu ağ sınırını **beyan ediyor**; `Unspecified` reddediliyor.
7. Kapsam filtresi MCP yüzeyinde de **tek kapıdan** geçiyor; bekçi bunu
kapsıyor.
8. Simülatör senaryosu yanlış yüzeye uygulandığında hata **yüzeyi** söylüyor,
profili değil.

---

## 9 · Bu belgenin bilmediği şey

- **MCP 2.0'ın hangi revizyonu.** Spesifikasyon sürümlü ve tarih damgalı;
M01 hangi revizyona uyduğunu **yazacak** ve sözleşme testi o revizyona karşı
koşacak. "En güncel" bir hedef değil, bir kayma.
- ~~**Akışlanabilir HTTP'nin oturum yönetimi**~~ — **karara bağlandı (M01).**
Aşağıya bakın.
- ~~**Araç sayısının modele maliyeti ölçülmedi.**~~ — **ölçüldü (M01).**
Aşağıya bakın.

---

## 10 · M01'de karara bağlananlar

### MCP oturumu ile BFF oturumu ayrı katmanlar

MCP'nin akışlanabilir HTTP oturumu **SDK'nın taşıma oturumu** olarak duruyor ve
BFF'in `redis-session`'ından **bağımsız**. İkisi farklı şeyleri tutuyor: BFF
oturumu bir *tarayıcı* oturumu (erişim token'ı tarayıcıya inmesin diye var),
MCP oturumu bir *protokol* oturumu (akışın ve isteğe bağlı yeniden bağlanmanın
durumu).

İkisini şimdi birbirine bağlamak **M08'in kararını önden vermek** olurdu:
kimliğin MCP oturumundan uca nasıl taşınacağı o ticket'ın konusu, ve o karar
verilmeden "MCP oturumu BFF oturumunun üstünde durur" demek, doğrulanmamış bir
mekanizmayı belgeye yazmak olurdu.

M01'in bıraktığı hâl ölçülü: `/mcp` ucu `RequireAuthorization()` taşıyor ve
**anonim bir MCP oturumu açılamıyor** (`McpHttpTransportTests`). Bu M08'in ön
şartı — servis hesabıyla ya da kimliksiz koşan bir MCP sunucusu bütün kapsam
kapılarını atlardı (K17).

### Uyulan revizyon: `2026-07-28` — ve bir düzeltme

Spesifikasyonun **"2.0" diye bir sürümü yok**; yayınlanmış revizyonların hepsi
tarih damgalı (`2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`,
`2026-07-28`). "MCP 2.0" ifadesi C# SDK'sının sürümüne bakıyordu.

`McpRevision.Supported = "2026-07-28"` — bugünün en yeni *stable* revizyonu.

**Sabit bir tavan değil bir hedef.** İlk uygulamada `McpServerOptions.ProtocolVersion`
o değere sabitlenmişti ve **ölçüldü**: SDK onu tek desteklenen sürüm yapıyor,
`2025-11-25` konuşan istemci el sıkışmada reddediliyor. Yani çivi gibi görünen
satır, §2'de **şart** olan sürüm anlaşmasını kapatıyordu. Şimdi sunucu
anlaşmayı açık bırakıyor; sabitin doğruluğunu **ölçüm** tutuyor:
`McpComplianceTests` yazılı revizyonun el sıkışmada kabul edildiğini ve eski
istemcinin de bağlanabildiğini sınıyor. SDK bir gün o revizyonu bıraktığında
kapı kırmızı yanıyor.

### Araç şemalarının bağlam maliyeti — ölçüldü

`o200k_base` BPE ile, `tools/list` yanıtının tamamı üzerinden (sözlük pakete
gömülü, ağ yok):

| Ölçüm | Değer |
| --- | --- |
| `tools/list` toplam (1 araç) | **198 belirteç** (700 karakter) |
| Zarf (araçlar hariç) | ~4 belirteç |
| **Araç başına** (`server.info`) | **194 belirteç** |

Araç başına rakam asıl olan: yeni bir aracın fiyatı. `server.info` iki küçük
şema ve iki cümlelik bir açıklama taşıyor, yani bu sayı **alt sınıra yakın** —
`logs.search` gibi zengin bir filtre şeması daha pahalı olacak.

**On beş araç için bir sayı yazılmadı** çünkü o sayı bir ölçüm değil kurgu
olurdu; M04/M05 kendi araçlarını ekledikçe `McpSchemaBudgetTests` gerçek
rakamı basıyor ve tavan sabiti (`ToolListTokenCeiling`) büyümeyi görünür
kılıyor.

### Bilinen ayrışma: günlükleme yeteneği

§2'nin tablosu **günlüklemeyi** (`notifications/message`) bir şart olarak
yazıyor. Çivilediğimiz revizyonda (`2026-07-28`) o yetenek **kullanımdan
kaldırıldı** (SEP-2577) ve SDK onu okuyan kodu derleme hatasıyla işaretliyor.
İkisi aynı anda tutulamıyor; karar koordinatörde ve M01 bu konuda **hiçbir
iddia yazmadı**.
