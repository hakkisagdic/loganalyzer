# 100 Mikroservislik Kurumsal Estate İçin Katmanlı Referans Mimari

*Altı mercek, altı adversarial doğrulama ve bir eksiklik taramasının sentezi. Doğrulama aşamasının düzelttiği her yerde düzeltilmiş hâli esas alındı; doğrulanamayan her sayı işaretlendi.*

---

## 1 · TEK EN ÖNEMLİ FİKİR

### Bir kenarın güvenilirliği, o kenarı yanlış tutmanın bedeliyle orantılıdır.

Yaygın formülasyon şudur: *"mikroservisler arası bağ sembollerden doğmaz; onu sözleşme ya da çalışma zamanı verir."* Bu doğru ama yetersiz — çünkü tarama, **sözleşme kaynaklarının çoğunun da çürüdüğünü** gösterdi. Elle yazılmış OpenAPI dosyası bir kenar değildir. `catalog-info.yaml` içindeki `dependsOn` bir kenar değildir. Elle küratörlü bir repo kümesi bir kenar değildir. Elle düzenlenmiş veri soy ağacı bir kenar değildir — DataHub bunu kendi dokümanına yazmış: elle eklenen ve programatik soy ağacı birbirini eziyor **[doğrulandı]**.

Sembol zincirinin `http.Post(...)` satırında bitmesi doğru bir gözlem. GitHub'ın kendi CodeQL izleyicisi bunu kabul ediyor: Python'da başlayıp Go'da biten bir akış için önerilen çözüm, arayüzü bir veritabanında *sink*, diğerinde *source* olarak **elle modellemek**. 100 mikroservis için bu, keşfetmesini istediğiniz topolojiyi elle çizmek demek. Ama "öyleyse sözleşmeden al" demek, hangi sözleşmeden alacağınızı söylemiyor.

Asıl ayrım bir seviye aşağıda. Kenarları kaynağına göre değil, **yaptırımına** göre sınıflandırın:

| Sınıf | Yanlış olursa ne olur | Örnekler | Güven |
|---|---|---|---|
| **Derleme-yaptırımlı** | Derleme kırılır | `go.mod` / `pom.xml` / `package.json` satırı, üretilen SDK bağımlılığı, Bazel/Nx hedefi, derlenmiş protobuf modülü | Yüksek |
| **Çalışma-zamanı-yaptırımlı** | İstek reddedilir | Kafka ACL, NetworkPolicy, Istio AuthorizationPolicy, gateway route, IAM, DB `GRANT` | Yüksek |
| **Yaptırımsız beyan** | Hiçbir şey olmaz | `dependsOn`, elle yazılmış OpenAPI/AsyncAPI, Copilot Space, Repo Cluster, elle düzenlenmiş lineage | Çürür |
| **Gözlem** | Yanlış olmaz, **eksik** olur | Trace kenarı, eBPF akışı, consumer group, sorgu logu | Alt sınır |

Bu tablonun üç sonucu var ve mimarinin tamamı bunlardan çıkıyor.

**Birincisi: işiniz çıkarım değil, toplama ve birleştirmedir.** Yaptırımlı kenarların büyük kısmı zaten var — manifest'lerde, SBOM'da, build grafında, IaC'de, broker konfigürasyonunda. Sözleşme merceğinin en iyi fikri ("manifest bir kenardır, ucuz bir ayrıştırma işi, neredeyse kimse yapmıyor") **yanlıştı**: bunu yapan olgun bir sanayi var — Dependency-Track portföydeki *her uygulamanın her sürümünde* bileşen kullanımını izliyor ve "ne etkilendi, nerede" sorusunu doğrudan cevaplıyor **[doğrulandı]**; GUAC'ın adı harfiyen "Graph for Understanding Artifact Composition" ve GraphQL + REST API sunuyor **[doğrulandı]**. Kendi manifest ayrıştırıcınızı yazmak, mevcut bir grafı farklı bir soruyla yeniden inşa etmektir.

**İkincisi: yaptırımlı gerçek ile yaptırımsız iddia aynı alanda duramaz.** Bu deponun kendi §8 kuralı ("bir gün kapanacak" ile "hiç kapanmayacak" aynı listede duramaz) graf ölçeğindeki tam karşılığı. Her kenar bir **köken etiketi** taşımalı: `derived-from: go.mod@sha` vs `declared-by: team-payments@2024-11`. Etiket yoksa "bu graf doğru mu" sorusunun cevabı asla evet olamaz.

**Üçüncüsü ve en tehlikelisi: eksik kenar, "hiçbir şey buna bağlı değil" diye okunur.** Bu alanda yokluk nötr değil. Örneklenmemiş bir trace, enstrümante edilmemiş bir servis, 2 saniyelik TTL'e takılan bir hop, `SizeMax`'ı aşan üretilmiş bir protobuf istemcisi — hepsi ekranda temiz bir harita ve eksik bir çizgi olarak görünür, ve "gerçekten çağıranı olmayan servis"ten **ayırt edilemez**. Bu, deponun §7'sinin tarif ettiği en pahalı hata sınıfı: hata yok, sayaç yok, belirti yok.

Bu yüzden mimarinin birinci gereksinimi hız değil, **kapsam farkındalığı**: her katman "kenar yok" ile "kapsamım yok"u ayırabilmeli. Bunu yapamayan bir katman, silme kararına girdi olamaz.

---

## 2 · KATMANLAR

### Genel görünüm

| # | Katman | Cevapladığı soru | Kenar sınıfı | Bağımlı olduğu |
|---|---|---|---|---|
| **L0** | Kimlik ve birleştirme anahtarı | "Bu sembol, bu paket, bu süreç aynı şeyin mi?" | — | — |
| **L1** | Sözcüksel / yapısal arama | "Bu dize, bu isim, bu kod şekli nerede geçiyor?" | yok (dizin) | — |
| **L2** | Repo-içi sembol grafı | "Bu fonksiyonu **bu repoda** kim çağırıyor?" | derleme-yaptırımlı | L0 |
| **L3** | Bağımlılık manifesti + SBOM + build grafı | "Bu paketi/şemayı hangi servisler kullanıyor?" | derleme-yaptırımlı | L0 |
| **L4** | Sözleşme kayıt defteri + kırılma kapısı | "Bu şemayı değiştirirsem kim derlenmez?" | derleme-yaptırımlı | L0, L3 |
| **L5** | Beyan edilmiş çalışma-zamanı topolojisi | "Bu servisi kim **çağırabilir**?" | çalışma-zamanı-yaptırımlı | L0 |
| **L6** | Gözlemlenmiş çalışma-zamanı topolojisi | "Bu kenar son N günde **kullanıldı mı**?" | gözlem (alt sınır) | L0 |
| **L7** | Sahiplik ve yönetişim (katalog) | "3'te kimi arayacağım? Bu servis hangi katman?" | beyan (yaptırımsız) | L3–L6 |
| **L8** | Karar kaydı ("neden") | "Neden böyle? Neyi bilerek yapmadık?" | tarihli kayıt | — |
| **L9** | Ajan yüzeyi (federe MCP) | — | — | L1–L8 |
| **L10** | Bekçiler (CI kapıları) | "Graf kırmızı yanabiliyor mu?" | — | L3–L6 |

Bağımlılık sırası: **L0 → (L1, L8 paralel) → (L2, L3, L5 paralel) → L4 → L6 → (L7, L10) → L9.**

---

### L0 — Kimlik ve birleştirme anahtarı

**Hangi soruyu cevaplar:** İki farklı katmanın gösterdiği iki şey aynı şey mi?

**Neden var:** Statik graf repo/modül/sembol ile anahtarlanır. Manifest grafı paket koordinatı ile. Çalışma zamanı grafı `service.name` ve pod etiketiyle. SBOM grafı PURL ile. Bu dört uzay hiçbir yerde otomatik kesişmez. Kesişmezse ne yaparsanız yapın, üstteki katmanların hiçbiri diğerine katılamaz — ve bu tek başına projeyi öldürür.

**Ne kurulur — üç koordinat sistemi + bir omurga + bir birleştirme tablosu:**

- **Omurga:** repo adı. Her şey ona asılır.
- **Kod sembolü için SCIP şeması.** `<scheme> <package-manager> <package-name> <version> <descriptor>+`. Bu, `100 tane config.ts` probleminin yayınlanmış cevabı: `npm example-app 1.0.0 config.` ile `npm other-app 2.0.0 config.` yapı gereği farklı sembollerdir. Ayrıca `local <id>` sembolleri belgenin dışına çıkmakla yapısal olarak yasaklı.
- **Artefakt için PURL.** CycloneDX, SPDX, Dependency-Track ve GUAC'ın hepsi buna anahtarlanıyor. Ekosistemler-arası artefakt kimliğinin fiili standardı bu ve altı merceğin hiçbirinde geçmiyor.
- **Çalışma zamanı için OTel resource attribute'ları.**
- **Birleştirme tablosu:** repo → {PURL listesi} → {service.name listesi} → {SCIP paket koordinatı}. Bir repo birden fazla artefakt ve birden fazla çalışma-zamanı servisi üretebilir; "tek evrensel ID" tavsiyesi bu yüzden yanlış.

**Neden alternatifi değil:** "Tek isim ilan et, CI'da zorla" tavsiyesi eksik. Ama merceğin *"hiçbir satıcı bunu sağlayamaz"* iddiası da fazla: Backstage'in varlık modeli, OTel Weaver'ın semconv politika kontrolleri ve SkyWalking'in "Service Hierarchy Relationship" özelliği (Kubernetes ↔ Istio mesh katmanları arasında mantıksal servis eşlemesi yapıyor) bu işin ürünleşmiş parçaları. Kimlik *şemasını* siz seçersiniz; *zorlamayı* mevcut araçlara yaptırabilirsiniz.

**İşletme maliyeti:** Kurulum 3–5 gün (şema kararı + tablo + CI kontrolü). Sürdürme ~0,05 FTE. *Bu bir mühendislik tahmini; taramada ölçülmüş bir kaynak yok.*

**Ne zaman atlanır:** Asla. Bunu atlarsanız diğer on katman birleştirilemeyen on ayrı graf üretir.

---

### L1 — Sözcüksel ve yapısal arama

**Hangi soruyu cevaplar:** "Bu dize/isim/kod şekli 100 repoda nerede geçiyor?"

**Neden var:** Bir ajanın varsayılan hamlesi ripgrep'tir ve 100 reponun yerel kopyası yoktur. Bu katmanın tamamı o eksikliği kapatmak içindir — daha fazlası değil.

**Ne kurulur:** Zoekt sınıfı bir trigram indeksi. Ücretsiz taban: kendi Zoekt'iniz ya da Sourcebot Basic (tek Docker imajı, arama, ücretsiz **[doğrulandı]**). Estate github.com üzerindeyse Blackbird zaten çalışıyor ve marjinal maliyeti sıfır — ama GHES'te yok. Yapısal kol için `ast-grep` (MIT, tree-sitter tabanlı, `$METAVAR` desenleriyle AST şekli eşleme) — grep'in metin körlüğü olmadan, indeks maliyeti de olmadan.

**Neden alternatifi değil:** Repo başına gömme (embedding) indeksi **almayın**. Ama gerekçe, merceğin sunduğu gerekçe değil — o gerekçe doğrulamada çöktü:

- Amazon makalesi "~%94,5" demiyor, "%90'ın üzerinde" diyor; Aralık 2025 tarihli; PDF üzerinde, kod üzerinde değil **[doğrulandı: kunye yanlış]**.
- *"Is Grep All You Need?"*'in kendi sonucu "grep kazanır" değil: "hangi harness ve tool-calling stilini kullandığın, altta yatan veri aynıyken bile skoru güçlü biçimde belirliyor" **[doğrulandı]**.
- CORE-Bench'in asıl sonucu embedding'lerin işe yaramadığı değil, **basit supervised fine-tuning'in açığı ciddi biçimde kapattığı** **[doğrulandı]** — yani merceğin tezinin tersi.

Gömmeye karşı ayakta kalan gerçek argümanlar ikisi ve ikisi de ölçüm değil aritmetik: (a) **maliyetin çarpılması** — 100 repo × N geliştirici, aynı gömme işini paylaşılmayan biçimde tekrarlıyor; (b) **bayatlama** — gömme indeksi sessizce eskir ve eskidiğini söylemez.

**İşletme maliyeti:** Kurulum 2–4 gün. Sürdürme ~0,1 FTE (indeksleme izleme, disk, sürüm). Donanım için elinizde **doğrulanmış bir tablo yok**: GitLab'in Zoekt boyutlandırma tablosu OAuth duvarının arkasında teyit edilemedi ve iç tutarsız (r5.large'da vCPU'yu, r5.8xlarge'da fiziksel çekirdeği "cores" saymış); "100 servis <50GB git" varsayımı da kaynaksız. Kendi deponuzda ölçün.

**Ne zaman atlanır:** Ücretli katmanı, ajan yüzeyini L9'dan alıyorsanız atlayın. Ücretsiz tabanı asla atlamayın.

**Kritik uyarı (bu katmanın sessiz hatası):** Sourcebot'un `find_symbol_references` aracı bir regex çalıştırıyor: `\b{symbolName}\b rev:… lang:… case:yes`; tanımlar `sym:\b{symbolName}\b` ile ctags'ten geliyor; varsayılan kapsam **tek repo** **[doğrulandı: kaynak dokümanda birebir]**. Yani derleyici-kesin bir isim taşıyan bir arac, yorum satırlarını ve alakasız aynı-isimli sembolleri döndürüyor ve ajan bunu anlamıyor. Bu aracı ajana **kendi adıyla vermeyin** (bkz. §4).

---

### L2 — Repo-içi sembol grafı

**Hangi soruyu cevaplar:** "Bu fonksiyonu bu repoda kim çağırıyor? Bu tipi kim uyguluyor?"

**Neden var:** Repo *içinde* sembol çözümlemesi derleyici tarafından yaptırımlıdır ve ucuzdur. Repo *dışına* çıkmaz — bu katmanın tanımı budur.

**Ne kurulur, üç seçenek, artan maliyet sırasıyla:**

1. **Tree-sitter tabanlı, build gerektirmeyen graf** (graphify'ın oturduğu kat). Yayınlanan ölçüm: 31 repoda %83 cevap kalitesi, dosya-dosya keşfe kıyasla ~10× daha az token ve 2,1× daha az araç çağrısı — ama **karşılaştırma noktası %92**, yani ~dokuz puan *kayıp* karşılığında onda bir maliyet. Bu bir yükseltme değil, bir takas.
2. **LSP tabanlı sembol erişimi** (Serena sınıfı, MIT, ~24–28k yıldız, 40+ dil, MCP-yerli **[doğrulandı]**). Altı merceğin en büyük atlaması buydu: gerçek sembol çözümlemesi, ajan-yerli, ücretsiz, build gerektirmiyor (dil sunucusu gerektiriyor).
3. **SCIP indeksleyicileri** (derleyici-kesin). Sadece belirli dil aileleri için GA, ve derlenen diller için **çalışan bir build şart**.

**Neden alternatifi değil — ve bu tarama içindeki en önemli iç çelişki:** Kod-grafı merceği "build gerektiren araçlar derlenmeyen repoları sessizce düşürür, sessiz delikli bir bilgi katmanı hiç olmamasından kötüdür" diyerek CodeQL, Moderne ve GraphRAG'i reddetti — sonra SCIP'i "REQUIRED" ilan etti. SCIP derlenen dillerde build ister. Aynı kusur, ters karar. Doğru pozisyon: **SCIP'in kimlik şemasını benimseyin (L0), indeksleyicilerini yalnızca ölçülmüş fayda gördüğünüz dillerde çalıştırın.**

Ve "kimlik şemasını benimsemek bedava" ifadesi de yanlış: 100 repo için `package-name` ve `version` üretmek, her manifest formatını ayrıştırmayı, yayınlanmamış iç paketleri koordinatlandırmayı ve "deploy edilen ama yayınlanmayan bir servisin sürümü nedir" sorusunu cevaplamayı gerektirir. Bu, sıfır fiyatlanmış sürekli mühendisliktir.

**İşletme maliyeti:** Tree-sitter kolu: kurulum 3–5 gün, sürdürme ~0,1 FTE. SCIP kolu: **repo × dil başına bir CI işi**; 100 repo için gerçek maliyet 0,3–0,5 FTE'dir ve hiçbir mercekte fiyatlanmamıştır. *Tahmin.*

**Ne zaman atlanır:** Ajanlarınız yerel checkout ile tek repoda çalışıyorsa bu katmanı estate seviyesinde kurmayın — zaten ripgrep + LSP'leri var. GA dil ailesi dışındaki diller için SCIP'i atlayın; oradaki "precise navigation" sessizce bulanık aramaya düşer.

---

### L3 — Bağımlılık manifesti, SBOM ve build grafı ⭐ *ilk gerçek cross-repo katman*

**Hangi soruyu cevaplar:** "Bu paketi/şemayı/kütüphaneyi hangi servisler kullanıyor?" — portföy çapında **ters** arama.

**Neden var:** Bu, taramadaki en yüksek fayda/maliyet oranı. Kenar zaten yazılmış (`go.mod`, `pom.xml`, `package.json`, lockfile), zaten CI tarafından ayrıştırılıyor, ve **yaptırımlı**: yanlışsa derleme kırılır. Şema kayıt defteri kod üretiyorsa (bkz. L4), üreticinin kimliği tüketicinin manifest'ine sürüm-sabitli olarak yazılıyor.

**Ne kurulur:** Kendi ayrıştırıcınızı **yazmayın**. SBOM boru hattını ingest edin:
- **Dependency-Track** (Apache-2.0, ücretsiz): portföydeki her uygulamanın her sürümünde bileşen kullanımı; CycloneDX SBOM **ve VEX**'i hem tüketiyor hem üretiyor **[doğrulandı]**.
- **GUAC**: SBOM + metadata'yı yutup artefaktlar arası yönlü graf kuruyor; OSV, Scorecard, ClearlyDefined, End-of-Life toplayıcıları; belgelenmiş kullanım senaryosu "CLI ile yama planı üretme" **[doğrulandı]**.
- Build sistemi varsa **build grafı** ekleyin — ama asimetriyi bilerek: Bazel `cquery` analiz fazında çalışır, `select()` dallarını doğru modeller, **ama `rdeps()`/`allrdeps()` desteklemez**; ters bağımlılık için tüm `select()` dallarını birden alan `query`'ye düşersiniz **[doğrulandı]**. Yani aracın kesin olduğu yön "neye bağlıyım", bulanıklaştığı yön "bana kim bağlı" — ve istediğiniz ikincisi. Nx `affected` de tek workspace içinde kalır **[doğrulandı]**; 100 ayrı repo = 100 kopuk graf.

**Neden alternatifi değil:** GitHub Dependency Graph API zaten açık ve ücretsiz — bunu tabana koyun ve satın alacağınız her şeyi bunun *üstüne* eklemeyi gerekçelendirin. Riftmap ücretsiz katmanda 250 repoya kadar tam graf veriyor ama **self-host mevcut değil (planlı)**, **MCP gönderilmemiş (planlı)** ve ücretsiz katmanda **sürekli yeniden tarama yok** **[doğrulandı]** — yani üretilen graf tek seferlik bir enstantane, ve bu mimarinin kendi kuralına göre bayat bir graf sessizce yanlış bir graftır.

**VEX'e dair bir not, çünkü kimse fark etmemiş:** VEX, "beyan edilmiş ama geçerli olmayan kenar"ı kaydetmek için icat edilmiş bir formattır. Çalışma-zamanı merceği tam olarak buna ihtiyaç duyduğunu söyleyip "nitelikli olumsuzu ifade edecek bir yer yok" sandı. Var, standart, ve muhtemelen kurumunuzda zaten kurulu.

**İşletme maliyeti:** Zaten SBOM üretiyorsanız kurulum 3–5 gün (ingest + graf). Sıfırdan Dependency-Track: +3–5 gün. Sürdürme ~0,1 FTE. *Tahmin.*

**Ne zaman atlanır:** Build grafı kısmını, build sisteminiz graf üretmiyorsa atlayın. Manifest/SBOM kısmını **asla** atlamayın; bu, elinizdeki en ucuz cross-repo kenardır.

---

### L4 — Sözleşme kayıt defteri ve kırılma kapıları

**Hangi soruyu cevaplar:** "Bu şemayı değiştirirsem hangi servisler derlenmez / seri hâle getiremez?"

**Neden var:** L3 kenarı verir; L4 o kenar üzerindeki değişikliğin **güvenli olup olmadığını** verir, ve merge'den önce verir. Distributed tracing bunu asla veremez çünkü ancak üretimde çalışmış bir kenarı görebilir.

**Ne kurulur — ve buradaki maliyet ayrımı kritik:**

- **Protobuf:** `buf` CLI Apache-2.0 ve **ücretsiz** **[doğrulandı]**. `buf breaking --against .git#branch=main` kırıcı-değişiklik kapısını **BSR olmadan, $0'a** kurar. `buf generate` çıktısını kendi Artifactory/Nexus/GitHub Packages'ınızda yayınlarsanız L3'ün manifest kenarı da $0'a oluşur.
- **BSR (barındırılan kayıt defteri)** ayrı bir karardır. Fiyat **tip başına**: Teams $0,50/tip/ay, Pro $5,00/tip/ay ve **$3.000/ay taban**, Pro'da halka açık tipler de faturalanıyor **[doğrulandı]**. ~5.000 tiplik bir estate Pro'da ~$25.000/ay ≈ **~$300.000/yıl**. Kod-arama merceği bunu hesaplayıp aynı cümlede "strong-fit" dedi. Sattığı şey namespace, sunucu-tarafı onay akışı ve SLA — kırılma kapısı değil.
- **Kafka/Avro:** Apicurio (Apache-2.0, tek depo: Avro, Protobuf, JSON Schema, XSD, WSDL, OpenAPI, AsyncAPI, GraphQL; artefakt **referans grafı** var) ya da Karapace (Apache-2.0, ama API uyumu **Confluent SR 6.1.1 seviyesinde sabit** **[doğrulandı: README birebir]**). Confluent'in Stream Lineage'ı — bu merceğin tek gerçek "gözlemlenmiş üretici→topic→tüketici" kaynağı — **yalnızca Confluent Cloud'da** **[doğrulandı: ürün sayfasında birebir]**. Self-host edenler için sema deposu + uyumluluk kontrolünden ibaret, ve çekirdek Confluent Community License, açık kaynak değil.
- **OpenAPI:** yalnızca **koddan üretiliyorsa** bu katmana girer. Üretilmiş spec + `oasdiff` (Apache-2.0, 514 değişiklik kuralı, PR satır-içi anotasyon) ucuz ve etkilidir. Elle yazılmış spec bir iddiadır; ona kapı koymak, iki kurguyu sadakatle diff'lemektir.

**Neden alternatifi değil:** Tüketici-güdümlü sözleşme testi (Pact) tek gerçek "bu değişiklik *gerçek* bir tüketiciyi kırar mı" cevabı ama 100 serviste belgelenmiş arıza modları disiplin arızalarıdır ve servis sayısıyla kötüleşir: tekrar kullanılan sürümler `can-i-deploy`'ı **yalan söyletiyor** (yeşil ama yanlış bir kapı, kapısızlıktan kötüdür), aşırı-belirtim sahte kırılma üretip sinyali boğuyor, sızıntılı provider state en yaygın flake sebebi. Ve ~100 serviste Pact koşan yayınlanmış tek bir hesap bulunamadı.

**Önemli düzeltme — "%75 drift":** Sözleşme merceğinin kategorinin yarısını "gereksiz" ilan eden dayanağı olan APIContext çalışması (650M çağrı, 10.000+ uç, %75 varyans) **doğrulanamadı**; o başlıkta bir whitepaper listede yok **[doğrulanmadı]**. Yapısal argüman sayısız da ayakta duruyor: *kimsenin bağımlı olmadığı bir dokümanın tutarsız olacağı bir şey yoktur.* Ama sayıyı ölçülmüş gibi taşımayın.

**İşletme maliyeti:** Ücretsiz CLI kapıları: 100 repoya ekleme ~5–8 gün (asıl iş her serviste taban spec üretme boru hattı, oasdiff'in kendisi değil). Barındırılan kayıt defteri: ayrıca maliyet modellemesi + satın alma döngüsü. Apicurio self-host: **her üretici ve tüketicinin seri hâle getirme yolunda duran senkron bir bağımlılık** — bu bir nöbet yüzeyidir ve "Apache-2.0, ücretsiz" satırının altında görünmez. Redpanda'nın broker-içi kayıt defteri bu işletme maliyetini sıfıra indirir ve mercekte hiç geçmiyor.

**Ne zaman atlanır:** Barındırılan kayıt defterini, paylaşılan bir namespace'e ihtiyacınız yoksa atlayın; ücretsiz kırılma kapısını tutun. Spec'leriniz elle yazılıysa ve üretmeye geçmeyecekseniz OpenAPI kolunu **tamamen** atlayın — o kapı hiçbir şey ölçmez.

---

### L5 — Beyan edilmiş çalışma-zamanı topolojisi

**Hangi soruyu cevaplar:** "Bu servisi kim **çağırabilir**?" (potansiyel kenar — gerçekleşen değil)

**Neden var, ve neden altı mercek de bunu kaçırdı:** Bu, trace'ten ucuz, dağıtımdan **önce** var olan ve statik/çalışma-zamanı ikilisine **dik** olan bir katman. Kaynakları:

- **Kafka ACL'leri ve consumer group metadata'sı** — broker'dan iki komutla alınabilen, ayrıştırma ve trace gerektirmeyen üretici/tüketici kenar listesi. Sözleşme merceğinin "kayıt defteri subject bilir, servis bilmez" şikâyetinin doğrudan panzehiri: **ACL principal'ı servis kimliğidir.** *Uyarı:* bir consumer group ancak offset commit ettikten sonra görünür — yokluk yine belirsiz **[Kafka dokümanı bu turda teyit edilemedi]**.
- **NetworkPolicy / Istio AuthorizationPolicy / IAM / security group** — kimin kime bağlanmasına izin verildiği.
- **Gateway route konfigürasyonu** (Kong/Gravitee/Envoy) — ama kör noktasıyla: yalnızca gateway'den geçen trafiği görür, ki bu genelde north-south'tur; east-west servis-servis çağrıları görünmez. Kong ~$105/servis/ay **[doğrulanmadı]**; 100 serviste ~$126k/yıl. Bu maliyetle alınan şey kısmi bir görünümdür.
- **Terraform state / Helm / ArgoCD** — hangi servisin hangi veritabanına, hangi kuyruğa bağlandığı.
- **Veritabanı `GRANT`'leri ve migrasyon dosyaları** — bkz. aşağıdaki paylaşılan tablo problemi.

**En büyük tek boşluk — paylaşılan veritabanı kuplajı:** Kurumsal 100 servislik bir estate'te en yaygın gizli servisler-arası kenar, **iki servisin aynı tabloya yazması**. Altı mercekteki hiçbir araç bunu göremez: statik graf göremez (SQL bir dize), sözleşme kayıt defteri göremez (tablo bir API değil), trace göremez (iki ayrı DB çağrısı, aralarında kenar yok). Görebilecek olanlar: sorgu logları (`ACCESS_HISTORY`, `pg_stat_statements`, ClickHouse `query_log`), migrasyon araçları (Flyway/Liquibase/Atlas — migrasyon dosyası şemanın **sahipliğini** repo başına beyan eder) ve servis hesabı GRANT'leri. Bu, bu mimarinin diğerlerinden ayrıldığı en somut yer.

**İşletme maliyeti:** Kurulum 5–8 gün (kaynak başına 1 gün). Sürdürme ~0,05 FTE — konfigürasyon zaten değişiyor, siz sadece okuyorsunuz. *Tahmin.*

**Ne zaman atlanır:** Düz ağ, NetworkPolicy yok, ACL yok, mesh yok ise okunacak beyan da yok — atlayın. Ama o durumda güvenlik ekibinin de aynı boşluğu vardır ve bu katman iki bütçeden birden fonlanabilir.

---

### L6 — Gözlemlenmiş çalışma-zamanı topolojisi

**Hangi soruyu cevaplar:** "Bu kenar son N günde **gerçekten** kullanıldı mı, ve hangi örnekleme oranıyla?"

**Neden var:** Statik graf var olan her şeyin **üst sınırıdır** (yazılmış ama hiç çalışmayacak kod dahil). Çalışma zamanı **alt sınırdır** (yalnızca çalışan, yalnızca örneklenen, yalnızca enstrümante edilmiş). Gerçek ikisinin arasında sıkışıktır. Bu katmanın tek ürünü, statik grafın üretemeyeceği şeydir: **kenar ağırlığı** — hacim, hata oranı, son görülme.

**Ne kurulur, en ucuzdan başlayarak (ve sıra önemlidir):**

1. **Zaten ödenmiş olanı çıkarın.** 100 servislik bir estate'te Datadog / Dynatrace / Grafana / bir mesh muhtemelen zaten çalışıyor. Datadog'un MCP sunucusunda `search_datadog_service_dependencies` var; tanımı birebir "upstream/downstream servis bağımlılıklarını getirir" **[doğrulandı]** — bu, tüm taramadaki **tek doğrulanmış birinci-taraf "bu servisi kim çağırıyor" MCP aracı** ve çalışma-zamanı merceği onu kaçırdı. Sınır: günlük 5.000 çağrı (aylık 50k değil, bağlayıcı olan günlüktür) ve GovCloud uyumsuz **[doğrulandı]**.
2. **Broker consumer group'ları + gateway erişim logları.** Neredeyse bedava, ayrıştırma yok, örnekleme yok.
3. **Veritabanı sorgu logları** — paylaşılan tablo kenarı için tek kaynak.
4. **Trace tabanlı servicegraph** — ancak zaten bir Collector filosu işletiyorsanız. Bileşen **alpha**; kenarların güvenilir olması için trace-ID'ye göre shard yapan **iki katmanlı** Collector dağıtımı gerekiyor (katman 1 loadbalancing exporter, katman 2 metrik üretimi) **[doğrulandı: README birebir]**; varsayılanlar `ttl 2s` ve `max_items 1000` **[doğrulandı]** ve yetersiz — yavaş hop'lar pencereden düşer ve kenar **sessizce** kaybolur. Tempo'nun MCP sunucusundaki sekiz aracın **hiçbiri** servis grafı aracı değil **[doğrulandı]**; ajan "bunu kim çağırıyor" için Prometheus'a gitmek zorunda.
5. **eBPF** — yalnızca Cilium'u zaten işletiyorsanız. Düzeltme: Cilium OSS'te **akış dışa aktarımı var** (`--hubble-export-file-path` ve arkadaşları **[doğrulandı]**); yani "90 gündür kim çağırdı" sorusu Enterprise gerektirmiyor, akışı mevcut log deponuza akıtarak cevaplanabilir. Enterprise-only olan **sorgulanabilir tarihsel arayüz**. OBI/Beyla ise 21 Ağustos 2026 itibarıyla **v0.12.1**, 1.0 yok, ve **en son sürüm private-stack çekirdeklerde kernel panic düzeltiyor** **[doğrulandı]**. 100 üretim servisine ayrıcalıklı eBPF DaemonSet dağıtmanın maliyeti kurulum değil, çekirdek matrisi ve her yükseltmedeki node-seviyesi risktir.

**Neden alternatifi değil:** Jaeger'in Spark tabanlı bağımlılık işi 2026'da yanlış operasyonel şekil — servicegraph aynı kenarları akış metriği olarak üretiyor. Pixie teknolojik olarak iyi ama son sürüm Ocak 2025 (Temmuz 2024 değil — **[doğrulandı: mercek yanlıştı]**), yani ~19 ay sürüm yok; kurumsal bağımlılık için diskalifiye.

**İşletme maliyeti:** Mevcut APM'den ihraç: 2–3 gün. Broker + gateway + DB log: 5 gün. Kendi Collector filonuz: **durumlu, sharding yapan bir katman** — kurulum 2 hafta, sürdürme 0,2–0,3 FTE, artı TSDB'de kenar × histogram bucket ile büyüyen **yinelenen** kardinalite faturası. *Tahmin; hiçbir mercek bunu para veya kişi olarak saymadı.*

**Ne zaman atlanır:** Collector filonuz yoksa trace-tabanlı kolu atlayın; broker + gateway + DB log ile başlayın. Cilium işletmiyorsanız eBPF'yi OBI 1.0'a kadar tamamen atlayın.

---

### L7 — Sahiplik ve yönetişim (katalog)

**Hangi soruyu cevaplar:** "Sabah 3'te kimi arayacağım? Bu servis hangi katman, hangi yaşam döngüsünde, hangi uyumluluk kapsamında?"

**Neden var:** Bu bilgi koddan türetilemez. Türetilebilenlerin hepsi zaten L3–L6'da.

**Mimari tersine çevirme — bu katmanın tek önemli kararı:** Standart Backstage kurulumu 100 takımdan 100 YAML dosyası yazmasını ve **sonsuza kadar bakmasını** ister. "Otomatik keşif" bunu çözmez: GitHub/GitLab discovery provider'ları **zaten `catalog-info.yaml` içeren** repoları tarar; dosyası olmayan repolar için varlık üretmek hâlâ açık bir özellik talebi (#18218). Ve `spec.dependsOn`/`providesApis`/`consumesApis` **elle yazılmış insan iddialarıdır**; kod değişince hiçbir şey onları yalanlamaz.

Doğrusu: **yapıyı türet, sahipliği beyan et.** L3–L6'yı bir *katalog varlık sağlayıcısı* olarak bağlayın; bağımlılık ve API ilişkisi alanlarını graf yazsın. Elle yazılan `catalog-info.yaml`'da yalnızca **yalnızca insanın bildiği** alanlar kalsın: owner, tier, lifecycle, on-call, uyumluluk kapsamı. Bu, en hızlı çürüyen alanı denklemden çıkarır.

**Al mı yap mı:** 100 serviste Backstage'i **self-host etmeyin**. Bağımsız olduğunu belirten bir maliyet modeli yıl-1 için $200k–$1,2M ve kalıcı 1–2 FTE veriyor **[doğrulanmadı — ve bu merceğin adversarial doğrulaması hiç yapılmadı, aşağıya bakın]**; yönetilen alternatifler bunun onda birinde. Roadie $24/geliştirici/ay, MCP giriş katmanında dahil, **50 geliştirici minimum** **[doğrulanmadı]**. OpsLevel'ın MCP'si tek "dependencies **ve** dependents" çifti sunan yüzey ama **self-host MCP'yi kullanımdan kaldırdı** — yani katalog trafiği ağınızdan çıkmak zorunda **[doğrulanmadı]**.

**Sahiplik verisinin kendisi çürük — ölçülmüş:** CODEOWNERS'ta 3 MB üstü dosya hiç yüklenmiyor; **son eşleşen desen kazanır**; geçersiz sözdizimli satır **sessizce atlanıyor**; `!` negasyonu ve `[ ]` aralığı desteklenmiyor **[doğrulandı]**. "Son eşleşen kazanır" + "geçersiz satır sessizce atlanır" birleşimi, bu deponun §7'sindeki CSV olayının birebir aynısı — ve kazanan şey yine kapsamın kendisi. Sourcegraph Own sahipliği **yalnızca CODEOWNERS'tan** çıkarıyor ve kendi dokümanı büyük repolarda davranışının doğrulanmadığını söylüyor **[doğrulandı]**.

**Skorkartlara dair:** Hakemli bir çoklu-kaynak literatür taraması (88 kaynak, 44 hakemli, Şubat 2026'ya kadar) skorkartların işe yaradığına dair **hiçbir hakemli kanıt bulamadı** — oysa skorkart bu kategorideki her satıcının birincil yönetişim mekanizması. Aynı tarama "IDP kullananların %89'u Backstage" rakamının Port'un kendi n=100 anketinden geldiğini ve bağımsız doğrulaması olmadığını da tespit etti.

**İşletme maliyeti:** Yönetilen: satın alma + 5–8 gün entegrasyon + ~0,1 FTE. Self-host: yukarıdaki model. Varlık sağlayıcısı yazımı: 3–5 gün.

**Ne zaman atlanır:** Çalışan bir on-call sahiplik kaydınız zaten varsa (PagerDuty servis dizini, IdP grupları) katalog **ürününü** atlayın ve o kaydı L0 birleştirme tablosuna bağlayın. Katalog bir ürün değil, bir alandır.

**Kesin atlanacak:** Atlassian Compass — 2026-05-13'ten beri yeni müşteriye kapalı, tüm erişim 2027-12-31'de bitiyor. configure8 — 2026'da sıfır ürün haberi, sıfır MCP.

---

### L8 — Karar kaydı ("neden" katmanı)

**Hangi soruyu cevaplar:** "Neden böyle yapıldı? Neyi bilerek yapmadık? Neyi refactor etmemeliyiz?"

**Neden var, ve neden altı mercekte hiç geçmedi:** Bütün graflar "ne neye bağlı"yı cevaplıyor. Hiçbiri "bunu 2024 kesintisi yüzünden kasten çoğalttık" bilgisini taşımıyor. Blast radius hesaplayabilen ama bunu bilmeyen bir ajan, bir kararı güvenle bozar.

**Bu katmanın taramadaki eşsiz özelliği:** **ADR'nin bayatlaması tasarım gereği sorun değil.** ADR tarihli bir kayıttır, mevcut durum hakkında bir iddia değil. Katalog YAML'ının çürümesi yalan üretir; ADR'nin eskimesi üretmez. Yani ajana beslenebilecek **en güvenli artefakt sınıfı** budur — ve hiç konuşulmamış.

**Ne kurulur:** Ürün gerekmiyor. Üç kaynak, hepsi zaten var:
- **ADR** (Nygard/MADR formatı, repo içinde `docs/adr/`). Standart bir makine-okunur format yok **[doğrulandı: adr.github.io bir araç dizini tutuyor ama merkezi index/format yok]** — yani şemayı siz belirlersiniz, ve bu iyi bir şey.
- **PR review yorumları** — zaman damgalı, dosya bazlı, yapılandırılmış, ve "neden *öyle* yapmadık" bilgisinin rutin olarak yazıldığı tek yer. 100 repoda hazır duran ve kimsenin indekslemeyi önermediği bir korpus.
- **Olay/postmortem verisi** — aynı olaylarda birlikte görünen iki servis, kod ne derse desin kuplajlıdır. Bu, elinizdeki **en yüksek sinyalli kenar ağırlıklandırması** ve hiç önerilmemiş.

**İşletme maliyeti:** Kurulum 3–4 gün (konvansiyon + index + arama). Sürdürme ~0. *Tahmin.*

**Ne zaman atlanır:** Asla. En ucuz katman, ve tek başına "neden" boyutunu kapatan katman.

---

### L9 — Ajan yüzeyi

Ayrıntısı §4'te. Özet: **tek federe MCP sunucusu, az sayıda geniş araç, mekanizmasıyla adlandırılmış.**

---

### L10 — Bekçiler

**Hangi soruyu cevaplar:** "Bu graf kırmızı yanabiliyor mu? Yanlışlaştığında bunu bana ne söyleyecek?"

**Neden var:** Bu deponun §5 dersi burada birebir geçerli: kapı eklemek işin yarısıdır; okunmayan kapı, olmayan kapıyla aynı sonucu verir ve üstüne "bu soru sorulmuş" yanılsaması bırakır. Pasif bir indeks yerine **okunan bir kapı** kurun.

**Ne kurulur — dört bekçi:**

1. **Bilinmeyen kenar kapısı.** Her PR'da: bu değişiklik grafın bilmediği bir kenar mı yarattı? Yeni bir HTTP host literal'i, yeni bir topic adı, yeni bir manifest satırı, yeni bir DB tablosu → kırmızı, ya kaydettir ya gerekçelendir.
2. **Kimlik kapısı.** repo adı ↔ `service.name` ↔ katalog varlığı ↔ PURL eşleşmiyorsa kırmızı.
3. **Tazelik kapısı.** Repo başına "son başarılı indeksleme" metriği; eşik aşılırsa alarm. Bu, terk edilmiş indeksi yakalayan tek mekanizma.
4. **Kırmızı testi (periyodik).** Bilinen bir cross-repo kenarı sil → ajanın blast-radius cevabının değiştiğini doğrula → geri koy. **Ölç, sonra geri al.** Bu testi geçmeyen bir graf, "bilmiyorum"u "hiçbir şey buna bağlı değil"e çeviren bir makinedir.

**İşletme maliyeti:** Kurulum 5–8 gün. Sürdürme ~0,1 FTE (kırmızıları okumak dahil — ki asıl maliyet budur).

**Ne zaman atlanır:** Asla. Bu katman olmadan üstteki her şey sessizce çürür.

---

## 3 · AL vs YAP vs ALMA

### AL (satın al)

| Katman | Ne | Fiyat | Gerekçe |
|---|---|---|---|
| L7 Katalog | Yönetilen Backstage veya SaaS IDP | Roadie $24/gel/ay, 50 gel. min **[doğrulanmadı]**; OpsLevel medyan ~$28,8k/yıl, Cortex medyan ~$75k/yıl **[üçüncü taraf, doğrulanmadı]** | Self-host'un tek çıktısı aynı katalog; maliyet farkı 4–40× ve bunun ~%90'ı headcount |
| L6 Gözlem | Zaten sahip olduğunuz APM | Mevcut sözleşme | Kenar listesi zaten var; eksik olan satın alma değil **ihraç ve birleştirme** |
| L4 (koşullu) | Barındırılan şema kayıt defteri | BSR Teams $0,50/tip/ay, Pro $5/tip/ay + $3.000/ay taban **[doğrulandı]** | Yalnızca paylaşılan namespace + sunucu-tarafı onay akışı gerekiyorsa |
| L1 (koşullu) | Sourcebot Pro | $20/kullanıcı/ay **[doğrulandı]** | **Koltuk matematiğini yapın:** 60 mühendiste ~$14,4k/yıl, 67 mühendiste ~$16k — yani Sourcegraph'in $16K tabanına yakınsıyor, üstelik orada gerçek precise navigation varken burada regex var |

**Sourcegraph Enterprise özel bir durum.** Yayınlanmış tek rakam "Enterprise, Starting at $16K" **[doğrulandı]**; $49–59/koltuk ve $50–75k taban **[doğrulanmadı, alıcı beyanı]**. Aldığınız şey gerçek: cross-repo `go_to_definition` derleyici-kesin, MCP resmî, OAuth DCR var. Almadığınız şey de gerçek: **100 CI boru hattında SCIP indeksleyicisi işletmek** ve indeks bayatladığında **hiçbir uyarı olmadan bulanık aramaya düşmek**. Ve kendi flagship instance'larında 2,8M reponun 45.000'inde SCIP indeksi var — **%1,6**. Satıcı kendi örneğinde precise navigation'ı canlı tutamıyorsa, "derleyici-kesin" mutlu-yol tarifidir, kararlı durum değil. Ayrıca `sourcegraph-public-snapshot` 2024 sonbaharında arşivlendi — OSS kaçış yolu kapalı, ve satıcı 14 ayda iki kez mevcut kullanıcıların aleyhine tek taraflı karar aldı.

### YAP (içeride yaz)

| Ne | Neden içeride | Tahmini büyüklük |
|---|---|---|
| **L0 birleştirme tablosu ve CI kapısı** | Şema kararı sizin; satıcı sizin repo↔servis↔artefakt eşlemenizi bilemez | 3–5 gün |
| **L9 federe MCP sunucusu** | MCP sunmayan katmanları (Zoekt, Dependency-Track, oasdiff, Apicurio, ACL'ler, DB logları) tek yüzeyde birleştiren şey; hiçbir satıcı bu birleşimi satmıyor | 2–3 hafta |
| **L10 bekçiler** | Sizin doğruluk tanımınız | 5–8 gün |
| **Anlaşmazlık yüzeyi** | Beyan edilmiş kenar ile gözlemlenmiş kenar çeliştiğinde **kazananı seçmeyin, çelişkiyi gösterin**. DataHub bunu yapmayıp üzerine-yazma yaşadı **[doğrulandı]** | 3–5 gün |
| **L8 karar kaydı indeksi** | Konvansiyon + index; ürün yok | 3–4 gün |

**Toplam iç geliştirme tahmini: ~8–10 hafta mühendis-zamanı, ardından ~0,5–0,8 FTE sürdürme.** *Bu bir tahmindir. Ve "mabl'ın 850 satırı katalog bakım tuzağıdır" diye reddedilen alternatifin maliyeti hiçbir mercekte fiyatlanmadı — sizin parser filonuzun satır sayısı da sıfır değil.*

### ALMA (kurmayın)

| Ne | Neden |
|---|---|
| **Repo başına gömme indeksi** | Maliyet 100 × N geliştirici ile çarpılıyor, paylaşılmıyor, sessizce bayatlıyor. *Kalite gerekçesi kullanmayın — o kanıt doğrulamada çöktü.* |
| **Kod üzerinde GraphRAG** | Bakım modunda, doküman-yönelimli, tree-sitter'ın deterministik ve bedava verdiğini pahalı LLM çağrılarıyla yeniden türetiyor |
| **Stack Graphs** | 2025-09-09'da GitHub tarafından arşivlendi **[doğrulandı]**. 2026'da bunu pazarlayan biri ya özel bir fork'a bakıyordur ya kontrol etmemiştir |
| **Bloop** | Depo arşivlenmiş, şirket kapanmış. Onu canlı listeleyen her karşılaştırma sayfası bayat içeriktir |
| **Atlassian Compass** | Yeni satış kapalı, servis 2027-12-31'de bitiyor |
| **Elle küratörlü bağlam kapları** (Copilot Spaces, Repo Clusters) | Yaptırımsız beyan; çürür ve çürüdüğünü söylemez. Ayrıca GitHub'ın bulut ajanı hâlâ **repo-kapsamlı** bir `GITHUB_TOKEN` ile çalışıyor, kardeş repoları okuyamıyor |
| **Glean'i kod grafı için** | Kurumsal arama, doğru şekilde öyle. "Bu servisin karar kaydı nerede" için alın, "bu fonksiyonu kim çağırıyor" için asla. *Ve isim çakışmasına dikkat: Meta'nın açık kaynak `Glean`'i tamamen farklı bir sistem* |
| **Auto-üretilmiş wiki'yi ground truth olarak** | CodeWikiBench: DeepWiki %64,06 belge kalitesi, C/C++'ta ~%53 **[doğrulandı]**. Belgelenmiş herkese açık hatalar: LibreOffice'e yanlış build sistemi atfetmek, LLVM'de TableGen'i tamamen atlamak. Tanımadığınız bir servise ilk bakış için mükemmel, cevap olarak güvensiz |

**Lisans nedeniyle bloke:** GitNexus mimari olarak bu taramadaki en yakın eşleşme (global registry + grup seviyesi araçlar + cross-repo `group impact`) ama **PolyForm Noncommercial 1.0.0**. Doğrulama iki önemli düzeltme yaptı: (a) merceğin "Go/Kotlin parser boşlukları fatal" ve "cross-repo desteklenmiyor" iddiaları **güncel depoyla çelişiyor** — Go, Kotlin, Rust tam destekli ve cross-repo açıkça var; (b) README ticari lisanslama yolunun var olduğunu söylüyor, yani sıfırdan pazarlık değil. Yine de bu bir satın alma kapısıdır. **Ve bu düzeltmenin daha büyük bir sonucu var:** kod-grafı merceği "piyasa bunu henüz inşa etmedi, fırsat burada" diye bitiriyordu. Piyasa inşa etti. Eksik olan lisans, kod değil.

---

## 4 · AJAN YÜZEYİ

### Şekil kararı, ürün kararından önemli

**100 repo için ölümcül hata: repo başına bir MCP sunucusu.** Bu aritmetik, ölçülmüş bir sayıya ihtiyaç duymuyor: 100 sunucu × N araç = ajanın ilk kullanıcı mesajından önce yediği bir bağlam vergisi. (Token vergisinin spesifik rakamları — araç tanımı başına 200–500 token, 5–10 sunucu için 50–75k token, sisirilmis arac setinde ~3× seçim doğruluğu kaybı — **hiçbiri doğrulanmadı**; ama argüman rakama gerek duymuyor.)

Doğru şekil: **tek federe sunucu, birkaç geniş araç.** Microsoft'un Azure SRE Agent'ının 100+ dar araçtan ~5 geniş CLI aracına indiği rapor edildi **[doğrulanmadı]** ama yön, bağımsız olarak MCP'nin bağlam ekonomisinden çıkıyor.

### Hangi katman MCP sunuyor, hangisi sunmuyor

| Katman | MCP durumu |
|---|---|
| L1 Sourcebot | Var, **ücretli katmanda** (Basic'te yok) **[doğrulandı]** |
| L1 GitHub | `github-mcp-server` MIT, `search_code` var. *Ama REST code search API'si kimlik doğrulanmış kullanıcıda **dakikada 10 istek** — çok sorgu atan ajan için gerçek bir tavan* |
| L2 Serena | Var, MCP-yerli, MIT **[doğrulandı]** |
| L3 Dependency-Track / GUAC | **Yok** — REST / GraphQL var |
| L4 Buf BSR | Var, resmî — **ve YAZMA yetkili**: token, onaylayan kullanıcının erişimini taşıyor, web UI'dan çağrılabilen her RPC MCP'den de çağrılabiliyor |
| L4 oasdiff / vacuum / Microcks / Apicurio / Karapace | **Yok** — CLI / REST |
| L5 IaC / ACL / NetworkPolicy | **Yok** — kubectl / API |
| L6 Datadog | Var, **`search_datadog_service_dependencies` dahil** **[doğrulandı]** — bu kategorideki tek doğrulanmış upstream/downstream aracı |
| L6 Tempo | Var, 8 araç, **hiçbiri servis grafı değil** **[doğrulandı]** |
| L6 Kiali | Var; `manage_istio_config` "UI-only actions payload" döndürüyor, doğrudan mutasyon yapmıyor **[doğrulandı]** |
| L6 Dynatrace | Remote var; **yerel MCP kullanımdan kaldırıldı (2.1.2 son sürüm)** **[doğrulandı]** |
| L6 Cilium Hubble | Yok — gRPC Observer API var (kendi köprünüzü yazmak için en temiz hedef) |
| L7 Backstage | Var (`@backstage/plugin-mcp-actions-backend`) ama **varsayılan olarak hiçbir şey açığa çıkarmıyor**; yapılandırma şart; ve **graf gezinme aracı yok** |
| L8 ADR/PR/postmortem | Yok — sizin işiniz |

**Boşluğu kapatma:** Federe sunucunuz, MCP sunmayan katmanların REST/GraphQL/PromQL/Cypher yüzeylerini sarar. Bu bir avuç araç, 100 değil.

### Önerilen araç seti (adları mekanizmasıyla)

| Araç | Ne yapar | Hangi katmanlardan |
|---|---|---|
| `search_code_lexical` | Trigram/regex arama, kapsam bilgisiyle | L1 |
| `search_code_structural` | AST şekli eşleme | L1 |
| `who_depends_on_artifact` | Manifest/SBOM ters arama, sürüm ve köken etiketiyle | L3 |
| `contract_change_impact` | Şema/spec değişiminin derlenmeyecek tüketicileri | L3+L4 |
| `who_may_call` | Beyan edilmiş potansiyel kenar (ACL/policy/route) | L5 |
| `who_did_call` | Gözlemlenmiş kenar + **gözlem penceresi + örnekleme oranı + kapsam bayrağı** | L6 |
| `who_owns` | Sahiplik, tier, on-call | L7 |
| `why_is_this_like_this` | ADR + PR yorumu + postmortem | L8 |
| `blast_radius` | Yukarıdakileri birleştiren tek çağrı, köken etiketli, **anlaşmazlıkları göstererek** | L3–L6 |

**En yüksek kaldıraçlı ve tamamen bedava karar burada:** araçları **vaat ettikleri şeye değil, yaptıkları şeye göre adlandırın.** `\b{sembol}\b` regex'i çalıştıran bir araç `find_symbol_references` diye adlandırılamaz. Bu, otonom bir tüketiciye yalan söyleyen bir ölçüm aracıdır ve ajanın mod değiştiğini anlamasının hiçbir yolu yoktur — "14 çağıranın hepsini buldum" der. `grep_symbol_word_boundary` adı ajanın davranışını doğru yönde değiştirir. Aynı şey precise-navigation'ı bulanık aramaya düşen her yüzey için geçerli: **mod göstergesi cevabın içinde olmalı**, dokümanda değil.

### Gömme mi graf mı — soru tipine göre yönlendirin

| Soru tipi | Doğru modalite | Kanıt durumu |
|---|---|---|
| "Bu dizeyi/ismi bilen dosya" | Sözcüksel | Güçlü — ajan zaten yapıyor |
| "Bu kod şekli nerede" | Yapısal (AST) | Güçlü — ast-grep üretimde |
| "Bunu kim çağırıyor / neyi kırarım" | Graf | Bu kategorinin var olma sebebi; **ama estate ölçeğinde ölçülmedi** |
| "Adını bilmediğim şeyi bulmak" | Gömme | **Zayıf ve iki yönlü belirsiz**: ContextBench semantik aramayı önde gösteriyor ama yazar bir vektör DB satıyor; CORE-Bench ince ayarın açığı kapattığını söylüyor |

Kararı ölçüme değil, üç aritmetik gerçeğe dayandırın: gömme indeksi **paylaşılmıyor** (100 × N), **bayatlıyor**, ve graf kenarı **yaklaşıklanamıyor** — bir çağrı ilişkisi ya vardır ya yoktur, benzerlik skoru yoktur.

**Ve en önemli ajan bulgusu, kalite değil benimseme:** CodeCompass çalışmasında grafa erişimi olan denemelerin **%58'i hiç araç çağrısı yapmadı** — ajan grafa sahipti ve yine grep'ledi **[iddia doğrulandı: makale gerçek, arXiv 2602.20048, tek yazar, 30 görev, hakemsiz]**. Ve aynı çalışmanın %99,4 rakamıyla bu bulgu iç tutarsız: denemelerin %58'i grafa dokunmadıysa %99,4'ün paydası nedir? İki bulgu birbirini yiyor ve mercek ikisini de olumlu alıntıladı. **Yine de operasyonel sonuç sağlam:** üstün bir grafa büyük para harcamadan önce bir hafta araç açıklamalarına ve sistem promptuna harcayın, ve **ajanın grafı gerçekten çağırıp çağırmadığını ölçün.** Değer = kalite × benimseme, ve ikinci çarpan neredeyse hiç ölçülmüyor.

### Güvenlik — bu mimarinin ürettiği yeni risk sınıfı

MCP'nin kendi güvenlik dokümanı confused deputy, token passthrough (`MUST NOT`), session hijacking (`MUST NOT use sessions for authentication`), SSRF ve kapsam minimizasyonunu sayıyor **[doğrulandı]**. Kod indeksleyen bir MCP'nin **ek bir sınıfı** var ve hiçbir mercekte geçmiyor:

**İndekslenen içerik bir prompt injection taşıyıcısıdır.** 100 repoluk ortak bir indeks, bunu **repolar-arası bir saldırı yüzeyine** çevirir: tek bir repoya yazma yetkisi olan biri, başka bir takımın ajanına talimat geçirebilir. Bu, grafın "correctness artifact" olduğu iddiasının güvenlik ikizidir.

İkinci sızıntı: **yetki.** Graf sorgusu, çağıranın erişemediği repolardaki kenarları döndürüyor mu? Sourcegraph repo-seviyesi ACL'i sorguya yansıtır; ev yapımı bir Neo4j katmanı **yansıtmaz**. Ev yapımı grafın en olası uyumluluk arızası budur ve altı mercekte hiç sorulmadı.

Üçüncüsü: **yazma yetkili MCP'ler.** Buf BSR'nin MCP'si onaylayan kullanıcının tüm erişimini taşıyor; Gravitee'nin yönetim API'si üzerinden bir ajan API sağlayabiliyor, politika güncelleyebiliyor, kimlik bilgisi döndürebiliyor. Bunlar üretim-mutasyon yetenekleridir ve bir config dosyasındaki token değil, sert bir yetkilendirme sınırı gerektirir.

---

## 5 · BAŞARISIZLIK MODLARI

Her biri sessizce yanlış çalışır. Her biri için **tespit mekanizması zorunludur** — mekanizma yoksa katman kurulmuş sayılmaz.

### 1 · Bayatlayan katalog
**Nasıl olur:** Geliştirici bir çağrıyı siler, gönderir, CI yeşil geçer; katalog artık var olmayan bir bağımlılığı iddia etmeye devam eder. Yaptırım yok, dolayısıyla düzeltme baskısı yok.
**Nasıl fark edilir:** Beyan edilmiş her kenarı L3/L4/L5/L6'dan en az bir türetilmiş kenarla eşleştirin. Hiçbiri desteklemiyorsa → *"yetim beyan"* raporu, haftalık, sahibi olan bir dashboard'da. Ve bu raporu **okuyan biri** olmalı; okunmayan kapı yok kapıdır.

### 2 · İsim çakışması
**Nasıl olur:** 100 repoda 100 `config.ts`, üç serviste `UserService`, iki serviste `ProcessPayment`. Repo yolu ile kapsamlanmış bir graf, birleştirilene kadar çalışır; birleştirildiğinde çakışmalar **hata vermez**, yanlış kenar üretir.
**Nasıl fark edilir:** L0'ın SCIP-şekilli kimliği bunu yapı gereği önler. Önlendiğini **ölçün**: kısa adı estate genelinde belirsiz olan sembollerin sayacını yayınlayın, ve belirsiz bir ada yapılan cross-repo sorgu ayrıştırma olmadan sonuç döndürüyorsa uyarı verin.

### 3 · Eksik enstrümantasyon
**Nasıl olur:** Bir servisin etiketleri kusurlu, trace'i yok, ya da mesh dışında. Haritada **yok**. "Çağıranı olmayan servis" ile birebir aynı görünür.
**Nasıl fark edilir:** L6'nın düğüm kümesini L7'nin katalog düğüm kümesiyle karşılaştırın. Fark **her zaman** bir liste üretir: "katalogda var, çalışma zamanında yok". Bu listedeki her servis ya ölüdür ya enstrümante değildir — ve hangisi olduğunu **bilmek zorundasınız**. `uninstrumented` birinci sınıf bir durum olarak modellenmeli, asla yokluk olarak değil. (Grafana'nın Ağustos 2026'da servis ve workload varlıklarını ayırdığı ve gerekçesinin "kusurlu enstrümantasyonlu bir servis graftan tamamen kaybolabiliyordu" olduğu iddia edildi **[doğrulanamadı]** — ama arıza sınıfı gerçek.)

### 4 · Terk edilmiş indeks ⚠️ *mimariyi sessizce öldüren mod*
**Nasıl olur:** İndeksleme işi kırılır, kimse fark etmez, sorgular eski enstantaneden **makul görünen** cevaplar döndürmeye devam eder. Hiçbir şey hata vermez.
**Nasıl fark edilir:** İki mekanizma, ikisi de zorunlu. (a) **Her cevap indeks zaman damgası ve kapsam bilgisi taşır** — ajan bunu görür, kullanıcı bunu görür. (b) Repo başına "son başarılı indeksleme" tablosu, sahibi olan bir alarm, ve tazelik SLA'i. 100 repodan webhook fan-in'i kaç dakikada kapanıyor — bu sayı yazılı olmalı.

### 5 · Sessiz kırpma
**Nasıl olur:** Zoekt'in `SizeMax` varsayılanı 2 MB (1 MB değil — **[doğrulandı: `index/builder.go:327`]**); `ShardMax` 100 MB **[doğrulandı]**; GitLab dağıtımında proje başına 500k dosya, dosya başına 20k trigram, 30 dk indeksleme zaman aşımı. Bir mikroservis estate'inde bu eşikleri aşan dosyalar **üretilmiş protobuf ve OpenAPI istemcileridir** — yani servisler-arası sözleşmeyi kodlayan tam o artefaktlar. Cross-repo aramanız en çok görmek istediğiniz sınır için sıfır sonuç döndürür, ve sıfır sonuç "hiçbir şey buna bağlı değil" diye okunur.
**Nasıl fark edilir:** Zoekt her atlanan dokümana `SkipReason = SkipReasonTooLarge` yazıyor **[doğrulandı]** — sinyal **var**, sadece yüzeye çıkmıyor. Atlanan doküman sayacını yayınlayın. Ve azaltıcı önlemi kullanın: `LargeFiles` / `IgnoreSizeMax(name)` glob deseniyle (`**/*.pb.go`, `**/schema.d.ts`) muafiyet tanıyor **[doğrulandı]** — yani bu tuzak tek satırlık bir config düzeltmesidir, kaçınılmaz değildir.

### 6 · Örnekleme körlüğü
**Nasıl olur:** %1 head sampling nadir yolları siler. servicegraph'in `ttl 2s` varsayılanı yavaş hop'ları düşürür. Trace-ID sharding yoksa eşleşmeyen span'lar sessizce sayılmaz. Hepsinin sonucu: eksik çizgi, temiz harita.
**Nasıl fark edilir:** Her "trafik yok" hükmünün yanına **gözlem penceresi + örnekleme oranı + enstrümantasyon kapsamı** yazın. Niteliksiz bir "90 gündür kullanılmıyor", verinin destekleyemeyeceği bir iddiadır. Ve kural olarak: **çalışma zamanı kanıtı silme adaylarını SIRALAR, silmeyi YETKİLENDİRMEZ.**

### 7 · Sahiplik çürümesi
**Nasıl olur:** CODEOWNERS'ta son eşleşen desen kazanır, geçersiz satır sessizce atlanır, 3 MB üstü dosya hiç yüklenmez **[doğrulandı]**. Yeniden yapılanma sonrası `owner` alanı artık var olmayan bir takımı gösterir.
**Nasıl fark edilir:** `owner` alanını IdP grup listesine **join edin** (bu depoda Keycloak grupları hazır duruyor). Canlı bir gruba çözülmeyen her sahip → kırmızı. Ayrıca CODEOWNERS parse hatalarını CI'da okuyun — GitHub bunu UI ve API'de gösteriyor ama **merge'i durdurmuyor**.

### 8 · Beyan-gözlem uyuşmazlığının sessizce çözülmesi
**Nasıl olur:** İki kaynak çelişir, sistem birini seçer, çelişki kaybolur. DataHub bunu dokümanına yazmış: elle ve programatik soy ağacı birbirini eziyor **[doğrulandı]**. Bu, komşu kategoride **üretimde yanmış** bir arıza.
**Nasıl fark edilir:** Asla uzlaştırmayın. Beyan edilmiş kenar ile gözlemlenmiş kenar çeliştiğinde **ikisini de saklayın ve çelişkiyi bir rapor kalemi yapın**. Beyan edilmiş ama hiç gözlemlenmemiş kenar = bu kategorinin üretebileceği en değerli bulgu; onu "çözerseniz" tek işe yarar sinyali yok etmiş olursunuz.

### 9 · Ajanın grafı hiç çağırmaması
**Nasıl olur:** Araç oradadır, ajan grep'ler. Ölçülmüş: %58.
**Nasıl fark edilir:** MCP sunucunuzu enstrümante edin. Oturum başına ve soru sınıfı başına araç çağrısı sayın. Çağrı yoksa grafın kalitesi ilgisizdir.

### 10 · Prompt injection ve yetki sızıntısı
**Nasıl fark edilir:** İndekslenen içeriği ajanın **veri** olarak gördüğünden emin olun, talimat olarak değil; ve graf cevaplarını çağıranın repo izinlerine göre filtreleyin. İkisi de test edilebilir: bilinen bir repoya zararsız bir talimat metni koyun ve ajanın davranışının değişmediğini ölçün; erişimi olmayan bir kullanıcıyla sorgu atın ve kenarların gizlendiğini ölçün.

### Meta-bekçi: kırmızı testi
Bilinen bir cross-repo kenarı silin → ajanın blast-radius cevabının değiştiğini doğrulayın → geri koyun. Bu testi **çalıştırmadan** hiçbir katmanı "hazır" ilan etmeyin. Geçen bir test geçtiğini kanıtlamaz; kırılabildiğini göstermek kanıtlar.

---

## 6 · 90 GÜNLÜK SIRA

Her adımın sonunda **cevaplanabilir hâle gelen somut soru** yazılı. Cevaplanamıyorsa adım bitmemiştir.

### Gün 1–10 · L0 kimlik + envanter
- Repo envanteri; her repo için: dil(ler), build durumu (CI'da **yeşil derleniyor mu**), üretilen artefaktlar (PURL), çalışma-zamanı servis adları, manifest formatları.
- Birleştirme tablosu + CI kapısı (repo ↔ service.name ↔ katalog varlığı ↔ PURL).
- Paralel başlat: L8 (ADR konvansiyonu + index) — bedava, kimseyi bloke etmiyor.

> **Cevaplanabilir olur:** *"Kaç repomuz var, kaçı derleniyor, kaçının çalışma-zamanı adı katalog adıyla eşleşiyor, ve hangi dillerin kuyruğunu taşıyoruz?"*
> Bu dördü, sonraki her satın alma kararının girdisidir ve şu an **hiçbiri bilinmiyor**.

### Gün 10–25 · L1 sözcüksel taban + L3 manifest/SBOM ingest
- Zoekt sınıfı indeks (ücretsiz katman), `LargeFiles` muafiyetleri ayarlı, atlanan doküman sayacı yayında.
- SBOM boru hattı: varsa ingest edin, yoksa Dependency-Track kurun. GitHub Dependency Graph API'yi taban olarak bağlayın.

> **Cevaplanabilir olur:** *"X paketini / şemasını / kütüphanesini hangi servisler kullanıyor?"* — portföy çapında ters arama. Bu, ilk gerçek cross-repo cevaptır ve maliyeti neredeyse sıfırdır.

### Gün 25–45 · L4 sözleşme kapıları + L5 beyan edilmiş topoloji
- `buf breaking --against .git#branch=main` 100 repoda (ücretsiz).
- Spec üretimi olan servislerde oasdiff PR kapısı. **Elle yazılmış spec'lere kapı koymayın.**
- ACL, NetworkPolicy, gateway route, IaC state, DB GRANT ve migrasyon dosyalarını ingest edin.

> **Cevaplanabilir olur:** *"Bu şemayı değiştirirsem hangi servisler derlenmez?"* ve *"Bu servisi kim çağırabilir?"*
> Bu iki soru, blast radius'un yaptırımlı yarısını kapatır — ve buraya kadar **satın alınan hiçbir şey yok**.

### Gün 45–65 · L6 gözlem, en ucuzdan
Sıra önemli: (1) mevcut APM'den kenar listesi ihracı, (2) broker consumer group'ları, (3) gateway erişim logları, (4) DB sorgu logları. **Trace tabanlı servicegraph'i yalnızca zaten Collector filosu işletiyorsanız** ekleyin; eklerseniz iki katmanlı trace-ID sharding şart ve `ttl`/`max_items` varsayılanlarını kutudan çıktığı gibi bırakmayın.

> **Cevaplanabilir olur:** *"Bu kenar son 30 günde gerçekten kullanıldı mı — hangi gözlem penceresi, hangi örnekleme oranı, hangi kapsamla?"*
> Ve ilk kez: **beyan edilmiş ama gözlemlenmemiş kenar listesi.** Bu, tüm kategorinin en değerli çıktısıdır ve iki kaynağın *anlaşmazlığından* doğar.

### Gün 65–80 · L9 federe MCP + L10 bekçiler
- Tek sunucu, yukarıdaki dokuz araç, **mekanizmasıyla adlandırılmış**, her cevap köken etiketi + indeks zaman damgası + kapsam bayrağı taşıyor.
- Dört bekçi CI'da, sahibi ve alarmı belli.
- Paralel: L7 katalog — türetilmiş alanları graf yazsın, elle yazılan dosyada yalnızca owner/tier/lifecycle/on-call kalsın.

> **Cevaplanabilir olur:** *"Bir ajan bu soruların hepsini birkaç yüz token'la cevaplayabiliyor mu, ve cevabının hangi kaynaktan geldiğini söylüyor mu?"*

### Gün 80–90 · Kırmızı testi + benimseme ölçümü
- Bilinen bir cross-repo kenarı silin, blast-radius cevabının değiştiğini ölçün, geri koyun. Rapora **"ölçtüm"** yazın.
- MCP sunucusunun çağrı sayaçlarını okuyun: ajan grafı gerçekten çağırıyor mu, hangi soru sınıflarında çağırmıyor?
- Kararlar: hangi ücretli katman gerçekten gerekiyor, ve kaç koltuk için.

> **Cevaplanabilir olur:** *"Bu graf kırmızı yanabiliyor mu, ve ajan onu gerçekten çağırıyor mu?"*
> Bu ikisi cevaplanmadan hiçbir ücretli katmanı satın almayın — çünkü ikisi de "hayır" ise satın alma sonucu değiştirmez.

**Neden bu sıra:** Yaptırımlı kenarlar önce, çünkü ucuzlar ve bayatlamıyorlar. Gözlem sonra, çünkü pahalı ve yorum gerektiriyor. Katalog sonra, çünkü türetilen alanları beslemeyi bekliyor. Ajan yüzeyi en son, çünkü ne sunacağı henüz belli değildi. Ve **satın alma en sona kalıyor**, çünkü ilk 65 gün hangi koltuk sayısı ve hangi kapsam için pazarlık edeceğinizi öğretiyor.

---

## 7 · NE ÖLÇÜLMEDİ / NE BİLİNMİYOR

### En önemli meta-gözlem

**Altı mercekte hiçbir araç kurulmadı, hiçbir sorgu çalıştırılmadı.** Hepsi doküman okuması. Bu deponun kendi §6 kültürüne göre bu, "geçtiğini gördüm" bile değil. Aşağıdaki mimari, doğrulanmış *belgelere* dayanıyor — doğrulanmış *davranışa* değil.

Ve ikincisi: **servis-katalog merceğinin (L7) adversarial doğrulaması hiç yapılmadı** (`dogrulama: null`). O merceğin bütün fiyatları, Backstage sınırlamaları, Roadie/Cortex/OpsLevel rakamları ve #18218 referansı **tek kaynaklı ve çürütülmemiş** durumda. Diğer beş mercekte doğrulama, iddiaların önemli bir kısmını çürüttü — bu merceğin çürütülmemiş olması güvenilir olduğu anlamına gelmiyor, sadece **bakılmadığı** anlamına geliyor.

### Doğrulanamayan sayılar — mimarinin ağırlık taşıyanları

| İddia | Durum | Neyi taşıyordu |
|---|---|---|
| APIContext "%75 OpenAPI drift", 650M çağrı | **Bulunamadı**; o başlıkta whitepaper listede yok | Sözleşme merceğinin "katalog katmanı gereksiz" hükmünün tamamı |
| mabl vaka çalışması (79 repo, 850 satır, %17→%70, PR +%291, drift %40→<%5) | **Bulunamadı**; mabl blogunda böyle bir yazı yok | Ajan merceğinin key_insight'ının birinci ayağı |
| Meta "6.000 → 200 token" | **Bulunamadı** | "200 token şartnamedir" normatif hedefi |
| Azure SRE Agent "%45 → %75" | **Bulunamadı** | "Az sayıda geniş araç" tavsiyesi |
| GitLab Zoekt boyutlandırma tablosu | **OAuth duvarı arkasında**, teyit edilemedi; ayrıca çekirdek birimlerinde iç tutarsız | "ONE small VM" kategori hükmünün tamamı |
| "100 servis <50GB git" | **Kaynaksız varsayım** | Aynı hükmün ikinci girdisi |
| Backstage self-host $200k–1,2M / yıl-1 | Tek modelden; hakemli MLR bakım yükü hakkında **hiçbir titiz veri bulamadı** | "Self-host etme" tavsiyesinin maliyet ayağı |
| MCP token vergisi (200–500 tok/araç, 50–75k tok, %72 pencere, 3× doğruluk kaybı) | **Hiçbiri doğrulanmadı** | Federasyon tavsiyesinin nicel gerekçesi |
| Netflix 150 subgraph / 3.000 tip | Birincil kaynaklar **403 / 404** | "Bu merceğin en iyi ölçek kanıtı" |
| Kong ~$105/servis/ay, Glean $350–480k/yıl TCO, CodeRabbit 2M repo, Blackbird 480TB/180M repo | **Doğrulanmadı** | Çeşitli fit hükümleri |

### Merceklerin kendi içinde ve birbirleriyle çeliştiği yerler

- **CodeCompass:** %58 sıfır-araç-çağrısı ile %99,4 tamamlama aynı çalışmadan ve birbirini yiyor. Mercek ikisini de olumlu alıntıladı.
- **Codebase-Memory:** makale 66 dil diyor, README 158; 31 repo **ayrı ayrı** değerlendirildi, federe değil — yani "yayınlanmış coklu-repo ölçümü olan tek seçenek" ifadesi yanlış, cross-repo kenarları **ölçülmedi**.
- **Grep vs RAG:** üç dayanaktan üçü de zayıfladı (AAAI ">%90" ve PDF; "Is Grep All You Need" harness sonucu; CORE-Bench fine-tuning sonucu). Mimarinin gömme-almama kararı bu yüzden **ölçüme değil aritmetiğe** dayandırıldı.
- **Kod-grafı merceği build-free'yi hem taşıyıcı ilke yaptı hem SCIP'i REQUIRED ilan etti** — aynı kusur, ters karar.
- **Sourcebot** bir mercekte "strong-fit", kendi doğrulamasında find-references'ı regex çıktı; kategorinin var olma gerekçesini karşılamıyor.
- **GitNexus** bir mercekte "fatal parser gaps + cross-repo yok" dendi, doğrulama **ikisinin de yanlış** olduğunu buldu; geriye tek gerçek engel olarak lisans kaldı — ve bu, "piyasa henüz inşa etmedi" tezini çürütüyor.
- **Sözleşme merceği "manifest'i neredeyse kimse ayrıştırmıyor"** dedi; SBOM sanayisi tam olarak bunu yapıyor ve portföy çapında ters arama sunuyor.
- **Çalışma-zamanı merceği "hiçbir satıcı join key sağlayamaz"** dedi; Backstage varlık modeli, OTel Weaver ve SkyWalking Service Hierarchy bu işin ürünleşmiş parçaları.

### Ürünler hakkında bayat / yanlış bilgi (düzeltilmiş)

- Pixie'nin son sürümü **Ocak 2025**, Temmuz 2024 değil.
- Continue deposu **arşivlenmedi**; 2026-08-20'de push aldı. Ürün sonlandırıldı, depo canlı.
- Dynatrace'in **yerel** MCP'si kullanımdan kaldırıldı (2.1.2 son sürüm).
- OBI/Beyla **v0.12.1** (mercek v0.8.0'daydı); son sürüm kernel panic düzeltiyor.
- Cilium OSS'te **akış dışa aktarımı var**; "tarih Enterprise gerektirir" abartılıydı.
- Zoekt `SizeMax` **2 MB**, `ShardMax` **100 MB**, `LargeFiles` muafiyeti **var**, `SkipReasonTooLarge` işareti **var**.
- Specmatic fiyatı **yayınlanmış** (50 koltuk min. $50/koltuk/ay → 2000+ koltukta $10) — mercek "yayınlanmamış" demişti; ve bu, koltuk-başı model olduğu için merceğin başka araçlarda mahkûm ettiği modelin aynısı.
- Apollo ücretsiz self-hosted router **dakikada 60 istek** ile sınırlı; geliştirici koltuk tavanları var.
- Gravitee'nin async yeteneği **ayrı bir ürün hattı** (+$1.250/ay giriş).
- Riftmap self-host **yok** (planlı), MCP **yok** (planlı), ücretsiz katmanda **sürekli yeniden tarama yok**.
- Semgrep Team'de "20.000+ Pro kural" değil, kaynak **600+** diyor.
- Meta Glean ve Glean Inc. **farklı ürünler**; "Glean MCP" araması yanlış satıcıya götürüyor.

### Bilinmeyen — ve her biri tavsiyeyi değiştiriyor

1. **Kaç geliştirici?** 100 servisi 30 mühendis mi 300 mühendis mi işletiyor? Her koltuk-başı fiyat, her minimum (Roadie 50, Harness 20, Port 15) ve Sourcebot-vs-Sourcegraph yakınsaması bu sayıya bağlı. **Sorulmadı.**
2. **100 reponun kaçı bugün CI'da yeşil derleniyor?** SCIP, CodeQL, Moderne ve her build-gerektiren katmanın gerçek maliyet belirleyicisi bu. **Sorulmadı.**
3. **Kod nerede?** github.com mu, GHES mi, GitLab mı? Blackbird GHES'te yok; GitLab exact search Premium/Ultimate gerektiriyor. **Sorulmadı.**
4. **Dil kuyruğu ne?** "8 GA dil ailesi" deniyor; kurumsal estate genelde o sekizin dışında bir kuyruk taşır. Kapsam yüzdesi hesaplanmadı.
5. **Mesh var mı? GraphQL var mı? Kafka var mı?** Kiali sadece Istio'yu görür; Apollo Federation sadece GraphQL'i; Stream Lineage sadece Confluent Cloud'u.
6. **Tazelik SLA'i ne olmalı?** Hiçbir mercekte "graf ne kadar bayat olabilir" sorusu yok.
7. **Yanlış blast-radius'un geri alma planı ne?** Silme kanıtı bu taramanın en çok övdüğü çıktı ve arıza planı yok.
8. **Grafın sahibi kim, kırmızısını kim okuyacak?** Bir correctness artifact'in sahibi yoksa kırmızısı okunmaz.
9. **Kişisel veri boyutu.** CODEOWNERS + git geçmişi + bus-factor analizi = **çalışan verisi işleme**. Avrupa'da satın alma bunu sorar; taramada tek kelime yok.
10. **Alternatif hiç fiyatlanmadı.** "100 repoyu grafla modellemek mi, monorepo'ya/Copybara ile senkron aynaya taşımak mı ucuz?" Dürüst bir alıcı bunu sorar; altı mercekte bu soru hiç yok, dolayısıyla grafın maliyeti **karşılaştırmasız** kaldı.
11. **Conway boyutu.** 100 mikroservis 100 takım değil — tipik olarak 15–25 takım. Blast radius teknik olarak 12 servis olabilir ama organizasyonel olarak 3 takımdır, ve maliyeti belirleyen ikinci sayıdır. Hiçbir araç onu veremiyor çünkü sahiplik her yerde elle giriliyor ve o elle girilen şey de kanıtlandığı üzere sessizce çürüyor.

### Ve bu mimarinin kendisi hakkında dürüst olan şey

Yukarıdaki on bir katmanın hiçbiri 100 repoluk bir estate'te **ölçülmüş** değil. Yayınlanmış tek nicel çoklu-repo kanıtı, tek yazarlı hakemsiz bir preprint (30 görev) ve bir projenin kendi yazarlarının 31-repo değerlendirmesi — ve o değerlendirme de federe değil, tek tek. Bu mimarinin gerekçesi ölçüm değil, **mekanizma**: yaptırımlı kenar yanlış olduğunda bir şey kırılır, yaptırımsız kenar çürür, gözlem eksilir. Bu üç mekanizma tek tek doğrulandı; birleşiminin 100 serviste ne ürettiği **ölçülmedi**.

O yüzden 90 günün son adımı ölçümdür, kurulum değil. Ve o ölçüm kırmızı yanamıyorsa, elinizde bir bilgi katmanı değil, "bilmiyorum"u "hiçbir şey buna bağlı değil"e çeviren bir makine vardır.