# NE ATLANDI — boşluk taraması

**Uyarı (önce bu):** WebSearch bütçesi bu oturumda tükenmişti (200/200). Aşağıdakiler doğrudan doküman çekmeyle (WebFetch) doğrulanan noktalar + model bilgisi karışımı. Doğrulanan her iddianın yanına kaynak koydum; doğrulanamayanları **[teyit edilmedi]** diye işaretledim. Sentez aşaması bu ayrımı korumalı — altı merceğin kendisi de aynı hastalıktan muzdarip (aşağıda C bölümü).

---

## A · Hiç taranmamış araç kategorileri

### A1. Build grafı — kategorinin en büyük boşluğu, çünkü tek *zorlanan* graf orada

Bazel, Buck2, Pants, Gradle, Nx, Turborepo, Moon, Please: hiçbiri altı mercekte geçmiyor. Bu tuhaf, çünkü "değişiklik etki analizi" sorusunun 15 yıllık, üretimde çalışan cevabı burada.

Somut ve sentez için değerli olan asimetri: Bazel `cquery` analiz fazında çalışır, `select()` dallarını ve build seçeneklerini doğru modeller — yani **kesin** graf odur; ama `rdeps()`, `allrdeps()` **desteklemez**. Ters bağımlılık için `query`'ye düşmek zorundasınız, o da bütün `select()` dallarını birden alarak **aşırı-yaklaşım** yapar ([bazel.build/query/cquery](https://bazel.build/query/cquery)). Yani: aracın kesin olduğu yön "neye bağlıyım", belirsizleştiği yön "bana kim bağlı" — ve alıcının istediği tam olarak ikincisi. Bu, kod-arama merceğindeki "isim anlamsal, gerçek sözdizimsel" bulgusunun build tarafındaki tam kardeşi ve kimse yan yana koymamış.

Nx `affected`: git diff → project graph → bağımlı projeler; **tek workspace içi**, repo sınırını geçmiyor ([nx.dev](https://nx.dev/features/ci-features/affected)). Yani 100 ayrı repoda Nx/Bazel size 100 kopuk graf verir.

Asıl kavramsal katkı: build grafı, bu taramadaki **tek yaptırımlı (enforced) graf**. Kenar yanlışsa derleme kırılıyor. Sözleşme merceğinin manifest fikri de aynı sebepten çalışıyor. Bu ikisi tesadüf değil, bir **seçim kriteri**: *kenarı yanlış tutmanın bir bedeli var mı?* Yoksa kenar çürüyor. Taramanın bulduğu tek gerçek yasa bu ve hiçbir yerde genel kural olarak yazılmamış.

Ayrıca: bzlmod / `MODULE.bazel` + uzak registry, cross-repo kenarı yine bir manifest kenarına çeviriyor — sözleşme merceğinin tarifini build sistemi zaten uyguluyor.

### A2. Test etki analizi — ölçülebilir tek "blast radius" ürünü

Develocity Predictive Test Selection, Microsoft TIA, Launchable, Ekstazi/STARTS. Develocity'nin PTS'i **graf tabanlı değil, ML tabanlı**: Build Scan geçmişinden model eğitiyor, yeni/değişmiş/başarısız/flaky testleri her zaman seçiyor, Conservative/Standard/Fast profilleri var ve dokümanı açıkça "tam kapsamı hıza takas ediyorsun" diyor; **belgelenmiş bir güvenlik/fallback garantisi yok**, çözüm olarak pipeline'ın ilerisinde "remaining tests" aşaması öneriliyor ([docs.develocity.ai](https://docs.develocity.ai/predictive-test-selection/)).

Sentez için önemli olan metodoloji: bu kategori **kaçırılan hata (missed failure) oranıyla** notlanıyor. Kod grafı kategorisinin hiç sahip olmadığı şey bu. Bir cross-repo etki grafı da düğüm sayısıyla değil, "gerçekten kırılan şeylerin recall'ü" ile ölçülmeli — ve bu metriğin nasıl hesaplandığı hazır duruyor.

### A3. SBOM / tedarik zinciri grafı — kategorinin *zaten var olan* cross-repo grafı

Hiç geçmiyor: GUAC, OWASP Dependency-Track, Snyk, Endor Labs, Socket, deps.dev, SLSA, in-toto, Sigstore, SPDX 3.0 ilişki modeli, CycloneDX 1.6 + VEX, PURL.

- **GUAC** adı harfiyen "Graph for Understanding Artifact Composition"; SBOM ve yazılım metadata'sını yutup artefaktlar arası yönlü graf kuruyor, GraphQL + REST API sunuyor, toplayıcı/sertifikacıları arasında OSV, Scorecard, ClearlyDefined, End-of-Life var; belgelenen kullanım senaryoları "tedarik zinciri olayına tepki" ve "CLI ile yama planı üretme" ([guac.sh](https://guac.sh/), [docs.guac.sh](https://docs.guac.sh/guac/guac-use-cases/)).
- **Dependency-Track** portföydeki *her uygulamanın her sürümünde* bileşen kullanımını izliyor, "neyin etkilendiğini ve nerede olduğunu" sorusunu doğrudan cevaplıyor, CycloneDX SBOM **ve VEX** hem tüketiyor hem üretiyor ([docs.dependencytrack.org](https://docs.dependencytrack.org/)).

İki sonuç. Birincisi: sözleşme merceğinin en iyi fikri ("manifest kenardır, ucuz bir ayrıştırma işi, neredeyse kimse yapmıyor") **yanlış** — bunu yapan olgun bir sanayi var, portföy çapında ters arama dahil. Manifest→kenar ayrıştırıcısı yazmak, Dependency-Track'in bağımlılık grafını farklı bir soruyla yeniden inşa etmektir. Ya ingest katmanı olarak kullanılır ya da neden kullanılmadığı yazılır.

İkincisi ve daha ilginci: **VEX, "beyan edilmiş ama geçerli olmayan kenar"ı kaydetmek için icat edilmiş bir format.** Runtime merceği tam olarak buna ihtiyaç duyduğunu söyledi ("nitelikli olumsuzu ifade edecek bir yer") ve olmadığını sandı. Var, ve standart.

Düzenleyici bacak da eksik: EU Cyber Resilience Act SBOM'u fiilen zorunlulaştırıyor **[teyit edilmedi]** — yani bu graf birçok kurumda zaten bütçelenmiş durumda ve kod grafı onun üstüne binebilir.

### A4. Veri katmanı: soy ağacı, veri sözleşmesi ve **paylaşılan veritabanı kuplajı**

dbt, OpenLineage, Marquez, DataHub, OpenMetadata, Amundsen, Apache Atlas, Egeria, Collibra/Alation, Unity Catalog, Data Contract Specification/ODCS, Great Expectations/Soda — hiçbiri yok.

- **OpenLineage**: Job/Run/Dataset + facet modeli, tutarlı isimlendirme stratejisiyle kimlik; Airflow, Spark, Flink, Hive, dbt, Trino emitter'ları; Marquez referans implementasyon ([openlineage.io/docs](https://openlineage.io/docs/)).
- **DataHub**: tablo düzeyi + **kolon düzeyi** + sistemler arası soy ağacı; üç yolla dolduruluyor — otomatik connector, **elle UI düzenlemesi**, API. Ve kendi dokümanında yazan uyarı: *elle eklenen ve programatik soy ağacı birbiriyle çakışıp istenmeyen üzerine-yazmalara yol açabilir* ([docs.datahub.com](https://docs.datahub.com/docs/features/feature-guides/lineage)). Bu, sözleşme merceğinin "bir kaynak diğerinin yerine geçmesin, anlaşmazlık görünür kalsın" kuralının **üretimde çoktan yanmış** hâli. Kural teorik değil; komşu kategoride belgelenmiş bir arıza.

**Ve en büyük tek boşluk:** kurumsal 100 servisli bir mülkte en yaygın gizli servisler-arası kenar, *iki servisin aynı tabloya yazması*. Altı mercekteki hiçbir araç bunu görmüyor. Statik graf göremez (SQL string), sözleşme registry'si göremez (tablo bir API değil), trace göremez (iki ayrı DB çağrısı, aralarında kenar yok). Görebilecek olanlar: sorgu logları (Snowflake `ACCESS_HISTORY`, `pg_stat_statements`, ClickHouse `query_log`), migrasyon araçları (Flyway/Liquibase/Atlas/Bytebase — migrasyon dosyaları şemanın *sahipliğini* repo başına beyan eder), servis hesabı GRANT'leri, SchemaSpy. mabl'ın el yapımı grafında "DB table OWNERSHIP" vardı ve ai-ajan merceği bunu alıntılayıp geçti.

### A5. IaC / beyan edilmiş dağıtım topolojisi

`terraform graph` ve state, Pulumi, Crossplane, Helm, Kustomize, ArgoCD, Kubernetes NetworkPolicy, Istio VirtualService/AuthorizationPolicy, Envoy/Kong route config, bulut IAM, security group'lar. Riftmap bir kez anıldı, kategori hiç açılmadı.

Bu katman "beyan edilmiş runtime topolojisi": trace'ten ucuz, dağıtımdan **önce** var, ve statik/runtime ikilisinin ikisine de dik. Kritik ayrım: NetworkPolicy/IAM *kimin çağırabileceğini* (potansiyel kenar), trace *kimin çağırdığını* (gerçekleşen kenar) verir. Aradaki fark, güvenlik ekibi için tek başına satın alınabilir bir çıktı ve silme kanıtının da üçüncü bacağı.

Aynı sınıfta: **Kafka ACL'leri ve consumer group metadata'sı** broker'dan iki komutla alınabilen, neredeyse ground-truth bir üretici/tüketici kenar listesi — hiç parse, hiç trace gerektirmiyor. Her zamanki uyarıyla: bir grup ancak offset commit ettikten sonra görünür, yani **yokluk yine belirsiz** **[teyit edilmedi — Kafka doküman sayfası çekilemedi]**.

Bir de **feature flag'ler** (LaunchDarkly/OpenFeature): kodda var görünen ama runtime'da kapalı yollar hem statik hem runtime grafını ters yönlerde yanıltıyor. Hiç geçmiyor.

### A6. ADR / karar kayıtları — "neden" katmanı tamamen yok

Nygard ADR, MADR, adr-tools, Log4brains, adr-manager. adr.github.io bir araç dizini tutuyor ama **standart bir makine-okunur format ya da merkezi index yok** ([adr.github.io](https://adr.github.io/)).

Bu, ajan için en ağır içerik boşluğu. Altı mercekteki her graf "ne neye bağlı"yı cevaplıyor; hiçbiri "neden böyle ve neyi yapmamamız gerektiği" sorusuna bakmıyor. Blast radius hesaplayabilen ama "bunu 2024 kesintisi yüzünden kasten çoğalttık" bilgisi olmayan bir ajan, bir kararı güvenle refactor eder.

Ve ADR'nin bu taramada eşsiz bir özelliği var: **bayatlaması tasarım gereği sorun değil.** ADR tarihli bir kayıttır, mevcut durum hakkında bir iddia değil. Katalog YAML'ının çürümesi yalan üretir; ADR'nin eskimesi üretmez. Yani ajan'a beslenmesi en güvenli artefakt sınıfı, ve hiç konuşulmamış.

### A7. Kod sahipliği — merceklerde sadece bir katalog alanı olarak geçti

- **CODEOWNERS**: 3 MB üstü dosya hiç yüklenmiyor (sahip bilgisi yok, review request tetiklenmiyor); **son eşleşen desen kazanır**; geçersiz sözdizimi olan satır **atlanıyor** (hata UI ve API'den görülebiliyor ama merge durmuyor); `!` negasyonu ve `[ ]` karakter aralığı **desteklenmiyor** ([docs.github.com](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners)). "Son eşleşen kazanır" + "geçersiz satır sessizce atlanır" birleşimi, CSV'de son satırın sessizce kazanması olayının (§7) birebir aynısı — ve kazanan şey yine kapsamın kendisi.
- **Sourcegraph Own**: sahipliği **yalnızca CODEOWNERS'tan** çıkarıyor, git geçmişi/katkı sinyali kullanmıyor; ve dokümanı "büyük repolarda veya büyük CODEOWNERS kural setlerinde iyi çalıştığı tam doğrulanmadı" diyor ([sourcegraph.com/docs/own](https://sourcegraph.com/docs/own)).
- **CodeScene** sınıfı davranışsal analiz (knowledge map, key personnel, bus factor, takımlar arası kuplaj — git geçmişinden türetilir) hiç geçmiyor **[teyit edilmedi — sayfa 404]**.

Yani: taramadaki *bütün* sahiplik verisi elle yazılmış bir dosyadan geliyor, ve o dosyanın kendisi sessiz-yanlış davranış üretiyor. Katalog merceğinin "sahiplik beyan edilir, yapı türetilir" ayrımı doğru ama beyanın kendisinin çürük olduğu ölçülmemiş.

### A8. Kurumsal bilgi / iç dokümantasyon ve olay verisi

TechDocs, Confluence+Rovo, Notion AI, Guru, Slab, Document360 kategori olarak yok (Glean/Swimm/Unblocked sadece ajan yüzeyi olarak listelenmiş). Daha önemlisi iki kaynak hiç anılmıyor:

- **PR review yorumları**: zaman damgalı, dosya bazlı, yapılandırılmış, ve "neden *öyle* yapmadık" bilgisinin rutin olarak yazıldığı tek yer. 100 repoda hazır duran ve kimsenin indekslemeyi önermediği bir korpus.
- **Olay/postmortem verisi** (PagerDuty, incident.io, FireHydrant, Blameless): aynı olaylarda birlikte görünen iki servis, kod ne derse desin kuplajlıdır. Bu, mevcut en yüksek sinyalli **kenar ağırlıklandırması** ve hiç önerilmemiş.

### A9. Ontoloji / metadata katmanı — birleşik grafın ŞEMASI hiç sorulmadı

Palantir Foundry Ontology (object type + link type + action type, veri kaynaklarının üstünde "kurumun dijital ikizi", granüler güvenlik ve yönetişim — [palantir.com/docs/foundry/ontology](https://www.palantir.com/docs/foundry/ontology/overview)), Apache Atlas/Egeria tip sistemleri, RDF/OWL/SHACL, W3C PROV.

Altı mercek de kenar *üretmekten* bahsediyor; hiçbiri bir servis düğümü, bir tablo düğümü, bir topic, bir SBOM bileşeni, bir CODEOWNERS takımı ve bir trace servisinin aynı grafta nasıl yan yana duracağını tanımlayan varlık/ilişki modelini adlandırmıyor.

Ve kimlik tarafında somut bir eksik var: SCIP (kod sembolü) ve OTel `service.name` (runtime) önerilmiş, ama **PURL / package-url hiç geçmiyor** — hâlbuki CycloneDX, SPDX, Dependency-Track ve GUAC'ın hepsi ona anahtarlanıyor; ekosistemler-arası artefakt kimliğinin fiili standardı o. Doğru öneri "tek evrensel id" değil: **üç kimlik şeması (SCIP / PURL / OTel resource) + aralarındaki join'in açıkça beyan edildiği bir tablo.**

### A10. Repo topolojisi — ve hiç sorulmayan alternatif satın alma

Sapling/EdenFS, Scalar/VFS for Git, Gerrit, submodule/subtree, `repo`, **Copybara**. Copybara önemli çünkü sorunun *modellenmesi* yerine *ortadan kaldırılması* seçeneğini temsil ediyor: 100 repoyu tek bir ağaca (ya da senkron bir aynaya) taşımak. Dürüst bir alıcı "grafı satın almak mı, monorepo'ya geçmek mi ucuz" diye sorar; altı mercekte bu soru hiç yok, dolayısıyla grafın maliyeti karşılaştırmasız kalıyor.

### A11. Ölü kod / kullanılmayan bağımlılık dedektörleri

knip, ts-prune, depcheck, Vulture, `-Wunused`, unused-deps. Runtime merceğinin "silme kanıtı" fikrinin **ucuz statik yarısı** — sıfır altyapıyla, örnekleme belirsizliği olmadan aday listesinin bir kısmını üretiyor. Hiç anılmamış.

### A12. Değerlendirme / benchmark — kategorinin kendisi yok

CrossCodeEval, RepoBench, SWE-bench (+Multimodal, +Live), LongCodeArena, DevEval. CrossCodeEval: Python/Java/TypeScript/C# gerçek repolarda cross-file bağlam; ilgili cross-file bağlam **yokken** benchmark "aşırı zor", bağlam eklendiğinde iyileşiyor ama **en iyi modelle bile tavana yaklaşılamıyor** ([arXiv 2310.11248](https://arxiv.org/abs/2310.11248)).

Tarama boyunca tek nicel karşılaştırma CodeCompass — 30 SWE-bench-lite görevi. Bu kategori **ölçülmüş kanıtla satın alınamaz**; alıcı kendi eval'ini kurmak zorunda, ve o eval'in neye benzeyeceği hiçbir mercekte tarif edilmiyor.

---

## B · Sorulmayan sorular (kurumsal alıcının soracakları)

1. **Tazelik SLA'i.** Graf ne sıklıkla yeniden kuruluyor? Incremental mi, tam mı? 100 repodan webhook fan-in kaç dakikada kapanıyor? Blast-radius cevabının doğruluğu doğrudan buna bağlı ve hiçbir mercekte "ne kadar bayat" sorusu yok.
2. **Yetkilendirme sızıntısı.** Graf sorgusu, kullanıcının erişemediği repolardaki kenarları sızdırıyor mu? Sourcegraph repo-level ACL'i sorguya yansıtır; ev yapımı bir Neo4j katmanı **yansıtmaz**. Ev yapımı grafın en muhtemel uyumluluk arızası bu ve hiç konuşulmamış.
3. **MCP yüzeyinin güvenliği.** MCP spesifikasyonunun kendi güvenlik dokümanı confused deputy, token passthrough (`MUST NOT`), session hijacking (`MUST NOT use sessions for authentication`), SSRF, yerel sunucu ele geçirme ve scope minimizasyonunu sayıyor ([modelcontextprotocol.io](https://modelcontextprotocol.io/specification/2025-06-18/basic/security_best_practices)). Kod indeksleyen bir MCP'nin buna ek bir sınıfı var: **indekslenen içerik prompt injection taşıyıcısıdır.** 100 repoluk ortak bir index bunu repo-lar-arası bir saldırı yüzeyine çevirir — tek bir repoya yazma yetkisi olan biri, başka bir takımın ajanına talimat geçirebilir. Bu, grafın "correctness artifact" olduğu iddiasının güvenlik ikizi ve taramada hiç yok.
4. **Veri ikametgâhı / hava boşluğu.** Kod ağın dışına çıkıyor mu? Katalog merceği OpsLevel'ın self-host'u kaldırdığını yazmış ama sonucunu çıkarmamış: kategori buluta kayıyor, düzenlenmiş sektörlerde bu tek başına eleme kriteri.
5. **Kişisel veri.** CODEOWNERS + git geçmişi + knowledge map = **çalışan verisi işleme**. GDPR kapsamında, ve "bus factor" analizi İK amaçlı kullanılırsa çalışan izleme sınırına yaklaşır. Avrupa'da satın alma bunu sorar; taramada tek kelime yok.
6. **Indexer önkoşulu.** SCIP indexer'ları çoğu dilde **derleme gerektirir.** 100 reponun kaçı bugün CI'da yeşil derleniyor? SCIP maliyetinin gerçek belirleyicisi bu ve hiç sorulmamış.
7. **Dil kuyruğu.** "8 GA dil ailesi" deniyor; kurumsal 100 servis genelde o sekizin dışında bir kuyruk taşır (COBOL, ABAP, PL/SQL, Delphi, eski .NET Framework). Kapsam yüzdesi hesaplanmamış.
8. **Grafın kendi bakımı ve sahibi.** 100 repo × N dil × değişen build sistemleri = kırılgan bir extractor filosu. mabl'ın 850 satırı "catalog bakım tuzağı" diye reddedildi; peki önerilen parser filosunun satır sayısı kaç, kim bakacak, kaç FTE? Alternatifin maliyeti hiç fiyatlanmamış.
9. **Fiyat modeli.** $16K / ~$29k / $150k sayıları geçiyor ama seat mi, repo mu, LOC mu, hangi ölçekte belirsiz.
10. **Çıkış / lock-in.** Graf dışa aktarılabilir mi (SCIP, PURL, GraphML, OpenLineage)? SCIP'in vendor-nötrleşmesi bu sorunun cevabıydı ama soru açıkça hiç sorulmadı.
11. **Yanlış cevabın geri alınması.** Graf yanlış blast-radius verdiğinde ne oluyor? Ona dayanarak yapılmış bir silmenin geri alma planı nedir? Silme kanıtı bu taramanın en çok övdüğü çıktı ve arıza planı yok.
12. **Tüketici kim: ajan mı insan mı?** Meta'nın 200 token'ı ajan içindir; mimarın istediği görsel bir haritadır. Bunlar farklı ürünler ve tarama boyunca karıştırılıyor.
13. **Graf yanlışlaştığında bunu ne söyleyecek?** Öneri (ve deponun kültürüne uyanı): grafı CI'ya **bekçi** olarak bağla — her PR'da "bu değişiklik grafın bilmediği bir kenar mı yarattı" (yeni HTTP host literal'i, yeni topic adı, yeni manifest satırı) → kırmızı. Pasif index yerine okunan bir kapı.

---

## C · Kanıtsız kalan iddialar

En kritik olan meta-gözlem: **altı mercekte hiçbir araç kurulmadı, hiçbir sorgu çalıştırılmadı.** Hepsi doküman okuması. Deponun kendi §6 kültürüne göre bu, "geçtiğini gördüm" bile değil.

Tek tek:

- **Sourcebot'un `find_symbol_references`'ının `\b{symbol}\b` regex'i olduğu.** Taramanın en güçlü ve en eyleme dönük iddiası, ve **ölçülmemiş**. Mercek kendi §6 testini öneriyor ama uygulamamış. Doğrulama sırasında birinci öncelik bu olmalı.
- **"%75 drift"** (sözleşme merceği): kaynak yok, popülasyon yok, "neyin %75'i" yok.
- **mabl sayıları** (%17→%70, PR hızı +%291, drift %40→<%5): tek şirketin kendi yayını, kontrol grubu yok, aynı dönemde model kuşağı da değişti. Nedensellik atfı desteklenmiyor.
- **Meta'nın 6.000→200 token'ı**: tek sorgu, hangi sorgu belirsiz; token düşüşü ancak cevap kalitesi korunuyorsa değerli, korunduğu gösterilmemiş.
- **Azure SRE Agent "Intent Met %45→%75"**: dahili metrik, tanımı yok.
- **CodeCompass %99.4 / %76.2 / %78.2**: 30 SWE-bench-lite görevi (Python, tek repo) — "100 mikroservis"e genellenmesi kanıtsız. Ve iç tutarsızlık: denemelerin **%58'i grafa hiç dokunmadıysa**, %99.4'ün paydası nedir? Aynı çalışmanın iki bulgusu birbirini yiyor ve mercek ikisini de olumlu alıntılıyor.
- **Codebase-Memory %83 vs %92, 10× token**: kimin, hangi soru setinde ölçtüğü yok.
- **"ripgrep, RAG kalitesinin ~%94'ünü veriyor"**: kaynaksız — ve "per-repo semantic indexing bir maliyet merkezidir" sonucunun tamamı buna dayanıyor. Taramanın en kırılgan yük taşıyan iddiası.
- **Zoekt limitleri** (1 MB, 500k dosya, 20k trigram, 30 dk): sürüm ve dağıtım yapılandırmasına bağlı; "kendi tesisinde ölç" notu yok.
- **"Sourcegraph 8 GA dil ailesi"** ve **"SCIP 2026-01-09'da scip-code org'una taşındı, Core Steering Committee + SEP"**: ikisi de tek noktadan, doğrulanmamış.

---

## D · İnsan / organizasyon boyutu — en zayıf halka

**Conway yasası hiç geçmiyor.** 100 mikroservis 100 takım demek değil; tipik olarak 15–25 takım, servis/takım oranı 4–7. Grafın gerçek tüketicisi servis sınırı değil **takım sınırı**. Blast radius teknik olarak 12 servis olabilir ama organizasyonel olarak 3 takımdır — ve maliyeti belirleyen ikinci sayıdır. Hiçbir araç onu veremiyor, çünkü sahiplik her yerde elle giriliyor (CODEOWNERS, catalog-info.yaml) ve o elle girilen şey de kanıtlandığı üzere sessizce çürüyor.

**Benimseme, kalite kadar önemli ve yalnızca bir kez tesadüfen ölçülmüş.** CodeCompass'ın %58'i tam olarak bu: araç kalitesi değil, ajan davranışı. Aynı bulgu insanlar için de geçerli (kullanılmayan Backstage portalları). Değer = kalite × benimseme, ve tarama ikinci çarpanı neredeyse hiç ele almıyor. Merceğin kendi sonucu doğru — "üstün bir grafa $150k harcamadan önce bir hafta tool description'a harca" — ama bunu bir kere söyleyip bırakıyor, hâlbuki bu bütün satın alma kararının çarpanı.

**Teşvik uyumu, kategorinin gerçek seçim kriteri.** Manifest kenarı ve build grafı çalışıyor çünkü yanlış olduklarında bir şey kırılıyor. Katalog YAML'ı ve elle çizilmiş soy ağacı çürüyor çünkü yanlış olmanın bedeli yok (DataHub bunu dokümanına yazmış: elle ve programatik soy ağacı birbirini eziyor). Bu ilke tüm merceklere uygulanabilir tek satırlık bir test: **bu kenarı yanlış tutmanın bedeli var mı?**

**Grafın sahibi kim?** Platform ekibi mi, mimarlar mı? Kim "hayır" diyecek? Deponun kendi §5 dersi burada birebir geçerli: kapı eklemek işin yarısı; okunmayan kapı, olmayan kapıyla aynı sonucu verir ve üstüne "bu soru sorulmuş" yanılsaması bırakır. Bir correctness artifact'in sahibi yoksa kırmızısını kimse okumaz.

**Bus factor ve bilgi haritası.** Silme kararının asıl riski kodda değil: o kodu bilen kişinin ayrılmış olmasında. CodeScene sınıfı davranışsal analiz bunu git geçmişinden çıkarır **[teyit edilmedi]**; taramada bu sinyal hiç yok.

**Yeniden yapılanma sonrası sahipsiz servis sınıfı.** Katalogdaki `owner` alanı artık var olmayan bir takımı gösteriyor. Sahiplik tazeliği IdP ile bağ gerektiriyor (bu depoda Keycloak grupları hazır duruyor) — hiç konuşulmamış.

---

**Sentez için tek cümlelik özet:** Tarama altı merceğin altısında da "kenar nasıl üretilir" sorusunu iyi çalışmış; atladığı şey (a) kenarın **yaptırımlı** olup olmadığının tek gerçek seçim kriteri olduğu, (b) kenarların yarısının koddan değil **manifest / SBOM / IaC / DB / broker / olay** katmanlarından geldiği ve bunların çoğunun zaten ürünleşmiş olduğu, (c) "neden" katmanının (ADR, PR yorumu, postmortem) tamamen boş olduğu, (d) grafın **kimin göreceği, kimin bakacağı, kimin kırmızısını okuyacağı** sorularının hiç sorulmadığı, ve (e) taramanın kendisinin tek bir aracı bile çalıştırmadığı.

**Sources:**
- [Bazel cquery](https://bazel.build/query/cquery)
- [Nx affected](https://nx.dev/features/ci-features/affected)
- [GUAC](https://guac.sh/) · [GUAC use cases](https://docs.guac.sh/guac/guac-use-cases/)
- [OWASP Dependency-Track](https://docs.dependencytrack.org/)
- [OpenLineage docs](https://openlineage.io/docs/)
- [DataHub lineage](https://docs.datahub.com/docs/features/feature-guides/lineage)
- [Develocity Predictive Test Selection](https://docs.develocity.ai/predictive-test-selection/)
- [GitHub CODEOWNERS](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners)
- [Sourcegraph Own](https://sourcegraph.com/docs/own)
- [ADR GitHub org](https://adr.github.io/)
- [MCP Security Best Practices](https://modelcontextprotocol.io/specification/2025-06-18/basic/security_best_practices)
- [Palantir Foundry Ontology](https://www.palantir.com/docs/foundry/ontology/overview)
- [CrossCodeEval (arXiv 2310.11248)](https://arxiv.org/abs/2310.11248)