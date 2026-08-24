# Graphify — ne zaman kullanılır, ne zaman grep yeter

`docs/graphify.md` aracın **ne olduğunu ve nasıl kurulduğunu** anlatıyor. Bu
belge onu tekrarlamıyor; cevapladığı soru şu: **elimdeki soruya graf mı bakar,
grep mi?** ve **grafın göremediği ne var?**

Buradaki bütün sayılar ve çıktılar bu depoda `HEAD = c7066e6` iken **gerçekten
koşturuldu**. Koşturulmayan bir şey varsa açıkça "koşturulmadı" yazıyor.

> **Sürüm uyarısı doğru, yok sayma.** Her komut stderr'e
> `warning: skill is from graphify 0.8.19, package is 0.9.48.` basıyor. Sebebi:
> graphify `.graphify_version` damgasını yalnız Claude'unkinde değil **bütün
> platform kurulum dizinlerinde** kontrol ediyor (`graphify/__main__.py:498`);
> makinedeki dört damgadan ikisi (`~/.gemini/skills/graphify`,
> `~/.agents/skills/graphify`) hâlâ `0.8.19` diyor. `graphify install`
> günceller. `2>/dev/null` ile susturma — `path`'in hata mesajı da stderr'e
> gidiyor ve onunla birlikte kayboluyor (§4.6).

---

## 1 · Önce dürüst soru: graf mı, grep mi?

İkisinin **ölçtüğü şey farklı**: `git grep` "bu **metin** hangi dosyalarda
geçiyor" diye soruyor ve çalışma ağacına, yani **şu ana** bakıyor; `graphify`
"bu **düğüme** hangi düğümler bağlı, kaç adım ötesinden" diye soruyor ve
`graphify-out/graph.json`'a, yani **grafın derlendiği commit'e** bakıyor.

Aynı sembol üzerinde ikisini de koşturdum:

```
$ graphify affected src/Bizigo.Contracts/AccessScope.cs | grep -c '^- '
448
$ git grep -l "AccessScope" | wc -l
82
```

448 ile 82 çelişmiyor: `affected` iki adım derinlikte **geçişli** kapanışı
veriyor (`AccessScope`'u import eden dosyayı import eden dosya da listede),
grep ise yalnızca metnin birebir geçtiği yeri.

| Soru | Araç | Çünkü |
| --- | --- | --- |
| "Bu dizge nerede geçiyor?" | `git grep` | Graf metni değil sembolü tutuyor; dizge grafta hiç yok |
| "Bu dosyayı bozarsam ne kırılır?" | `graphify affected` | Geçişli kapanış; grep tek adım görür |
| "Bu depoda ne var, nereden başlarım?" | `graphify god-nodes` + `GRAPH_REPORT.md` | Merkezîlik grep'in ölçemeyeceği bir şey |
| "Şu iki parça birbirine nasıl bağlanıyor?" | `graphify path --undirected` | Ara düğümleri grep'le kovalamak elle BFS yapmak demek |
| "Dün yazdığım sınıfı kim çağırıyor?" | `git grep` | **Graf bayat** — §3 ve §4.1 |
| "Compose'da kaç tane `redis` servisi var?" | `git grep` / `docker compose config` | Graf YAML'ı hiç düğümleştirmiyor — §4.4 |
| "Bu ucun UI tarafındaki tüketicisi kim?" | `git grep` / `api:check` | Graf'ta `ui` ile `src` arasında **sıfır** kenar var — §4.3 |

**Kısa kural:** graf *ilişki* sorularında kazandırıyor; *varlık* ve *tazelik*
sorularında grep kazanıyor. Emin değilsen ikisini birden koştur — `affected`
boş makinede 0,17 s, load average 45'te 2,8 s sürdü (ölçüldü).

---

## 2 · İş akışları

### 2.1 · Depoya yeni gelen ajan: oryantasyon

```bash
graphify god-nodes --top 12
sed -n '1,20p'     graphify-out/GRAPH_REPORT.md   # Corpus + Summary + Freshness
sed -n '436,462p'  graphify-out/GRAPH_REPORT.md   # God Nodes + Surprising + Import Cycles
```

Gerçek çıktı (ilk beş):

```
God nodes (most connected):
  1. AccessScope - 150 edges
  2. Bizigo.Contracts - 129 edges
  3. Bizigo.ControlPlane - 109 edges
  4. Bizigo.UnitTests - 92 edges
  5. ControlPlaneDbContext - 71 edges
```

Bu bir mimari iddia değil, bir **dikkat sırası**: `AccessScope`'a dokunan bir
ticket 150 kenarlık bir yüzeye dokunuyor demektir — `CLAUDE.md` §9'un "kesişen
bir uç varsa sözleşmeyi önceden çivile" maddesi tam burada devreye giriyor.

### 2.2 · Bir refactor'un etkisini ölçmek

```bash
graphify affected src/Bizigo.Contracts/AccessScope.cs --depth 2
graphify affected src/Bizigo.Contracts/AccessScope.cs --relation imports --depth 1
```

Varsayılan derinlik 2, ve varsayılan olarak 14 ilişki tipini birden ters yönde
geziyor (`calls`, `references`, `imports`, `inherits`, `implements`, `uses`…;
tam listeyi çıktının `Relations:` satırı yazıyor). 448 satır çok geliyorsa
**derinliği düşür, ilişkiyi daralt** — `--depth 1 --relation imports`
"doğrudan bağımlı dosyalar" listesini verir. Geniş listeyi ticket bölmek için,
dar listeyi kod okumak için kullan.

### 2.3 · Bir sözleşme değişikliğinin tüketicilerini bulmak

Burada bir tuzak var ve ölçüldü: **dosya düğümü ile sembol düğümü aynı şey
değil.** Dosya yolu verirsen dosya düğümünü alırsın, derecesi 2 çıkar
(`--> AccessScope [contains]`, `--> Bizigo.Contracts [contains]`) ve "bunun
bağlantısı yokmuş" sanırsın. Sembol adını verince ise **belirsizlik** çıkıyor:

```
$ graphify explain "AccessScope"
Ambiguous: 'AccessScope' matches 2 nodes in different files.
  src/Bizigo.Contracts/AccessScope.cs
    id: src_bizigo_contracts_accessscope_bizigo_contracts_accessscope
  src_bizigo_api_authenticationsetup_cs_accessscope
    id: src_bizigo_api_authenticationsetup_cs_accessscope
Retry with the repo-relative path or the full node id.
```

Doğru hamle **tam düğüm id'siyle** tekrar sormak. Gerçek çıktı 51 satır;
aşağıda `...` ile elendi:

```
$ graphify explain "src_bizigo_contracts_accessscope_bizigo_contracts_accessscope"
Node: AccessScope
  Source:    src/Bizigo.Contracts/AccessScope.cs L13
  Degree:    150
  ...
Connections (150):
  --> .ForGroups() [method] [EXTRACTED] src/Bizigo.Contracts/AccessScope.cs:L53
  <-- .From() [references] [EXTRACTED] src/Bizigo.Storage.ClickHouse/ScopePredicate.cs:L39
  ...
  ... and 130 more
  Grouped by file:
    <-- tests/Bizigo.UnitTests/EvidenceTestDoubles.cs: 18 connections
    <-- src/Bizigo.Query/ScopedQuery.cs: 16 connections
    ...
```

`-->` çıkan kenar, `<--` **gelen** kenar. Sözleşme değiştiriyorsan seni
ilgilendiren `<--` satırları: onlar tüketiciler. 150 kenarı tek tek okumak
yerine **`Grouped by file` bölümüne** bak — "hangi dosya ne kadar bağlı"
sorusunu doğrudan cevaplıyor.

`[EXTRACTED]` kenarın AST'den geldiğini söylüyor; `[INFERRED]` görürsen o bir
**tahmin** ve grafın kendi raporuna göre ortalama güveni 0,81 — olgu gibi
okuma.

### 2.4 · İki parça arasındaki bağı bulmak

`path` varsayılan olarak **yönlü** arama yapıyor, oysa `graph.json`
`"directed": false`. Sonuç: çoğu gerçek soru ilk denemede boş dönüyor.

```
$ graphify path "AccessScopeResolver" "ScopePredicate"
No directed path found between 'AccessScopeResolver' and 'ScopePredicate'. Re-run with --undirected to search ignoring edge direction.

$ graphify path "AccessScopeResolver" "ScopePredicate" --undirected
Shortest path (4 hops):
  AccessScopeResolver --method [EXTRACTED]--> .Resolve() --references [EXTRACTED]--> AccessScope <--references [EXTRACTED]-- .Describe() --references [EXTRACTED]--> ScopePredicate
```

`--undirected` **belgeli** bir bayrak: `graphify path --help` birebir
`[--directed|--undirected]` yazıyor. Ama `graphify --help`'in `path` bölümünde
görünmüyor — orada yalnızca `--graph` listeli — o yüzden ana yardımı okuyup
"böyle bir bayrak yok" sanmak kolay. `path` boş döndüyse önce bunu dene, sonra
"bağ yok" de.

### 2.5 · Serbest soru sormak (`query`) ve bütçe tuzağı

```
$ graphify query "kapsam filtresi nerede uygulaniyor" --budget 400
... | 155 nodes found

[!] TRUNCATED: showing 9 of 155 nodes (~400-token budget). The answer may be
among the 146 cut nodes — raise the token budget (CLI: --budget) or narrow
the query
```

Araç kestiğini açıkça söylüyor, ama okunması gerekiyor: 9 düğüme bakıp "cevap
yok" demek, 146 düğümü görmeden karar vermek olur. Varsayılan bütçe 2000 token.

`query` bir LLM sorgusu değil, **soru metnindeki dizgelerden başlayan BFS**.
Başlangıç düğümleri ilk satırda yazıyor (`Start: [...]`); oradaki düğümler
sorunla ilgisizse soru yanlış anlaşılmış demektir — kelimeleri kodda geçen
isimlere yaklaştır.

---

## 3 · Tazelik: graf ne zaman bayatlar

**Şu an bayat, ve bayatlığın iki ayrı boyutu var.** "Kaç commit geride" diye
sormak eksik cevap veriyor, çünkü graf commit'i HEAD'in **atası değil**:

```
$ git merge-base --is-ancestor cca19da5 HEAD; echo $?
1
$ git rev-list --count HEAD..cca19da5     # grafta var, HEAD'de yok
3
$ git rev-list --count cca19da5..HEAD     # HEAD'de var, grafta yok
36
$ git branch -a --contains cca19da5
  yedek-rebase-oncesi
```

Graf bir rebase'le terk edilmiş hatta duruyor. İki kör nokta birden: HEAD'in
ortak atadan beri ilerlediği **36 commit grafta yok**, ve grafın taşıdığı
**3 commit HEAD'in hattında artık yok**. İkincisi daha sinsi — graf yalnızca
eksik değil, **var olmayan bir durumu** anlatıyor. Tek başına `rev-list
--count` bunu göremez, o yüzden kontrol ıraksamayı sormalı:

```bash
C=$(python3 -c "import json;print(json.load(open('graphify-out/graph.json'))['built_at_commit'])")
git merge-base --is-ancestor "$C" HEAD || echo "IRAKSAK: grafta HEAD'de olmayan $(git rev-list --count "HEAD..$C") commit var"
echo "ortak atadan beri geride: $(git rev-list --count "$(git merge-base "$C" HEAD)..HEAD") commit"
```

Koşturuldu; bu depoda yukarıdaki iki sayıyı (3 ve 36) basıyor. Graf HEAD'in
atası olduğunda ilk satır hiç yazılmaz, ikincisi düz "kaç commit geride"
cevabını verir.

### Neden kancalar tazelemiyor

Sebep kancaların kurulu olmaması değil — **worktree'de çalışmamaları**.
`.githooks/post-commit` satır 29-33:

```sh
_GFY_GITDIR=$(cd "$(git rev-parse --git-dir 2>/dev/null)" 2>/dev/null && pwd)
_GFY_COMMONDIR=$(cd "$(git rev-parse --git-common-dir 2>/dev/null)" 2>/dev/null && pwd)
if [ -n "$_GFY_COMMONDIR" ] && [ "$_GFY_GITDIR" != "$_GFY_COMMONDIR" ]; then
    exit 0
fi
```

Bağlı bir worktree'de `--git-dir` ile `--git-common-dir` farklıdır, dolayısıyla
kanca **sessizce çıkar**. Bu depoda ajanların hepsi worktree'de commit atıyor
(`CLAUDE.md` §4), yani **hiçbir ajan commit'i grafı tazelemiyor.** Aynı kısa
devre `post-checkout`'ta da var (satır 50).

`graphify hook status` yine de üç satırın üçüne de `installed` / `registered`
diyor. Bu cevap **kancanın dosyada olduğunu** söylüyor, **koştuğunu** değil.
İkisini karıştırmak `CLAUDE.md` §7'nin "sessizce atlayan bekçi" sınıfı.

### Tazelemek

Yeniden derleme AST-only ve API maliyeti yok, ama manifest'teki 771 dosyanın
tamamını yeniden ayrıştırıyor (`graphify update` → `_rebuild_code(...,
changed_paths=None)`). 16 GB'lık makinede beş ajan çalışırken bu **ağır iş**:

```bash
~/.claude/scripts/machine-resources.sh check     # çıkış kodu 1 ise başlama
graphify update .                                # AST yeniden çıkarma + kümeleme
graphify cluster-only . --no-viz --no-label      # sadece kümeleme, HTML/LLM yok
```

- `--force`: yeniden derleme **daha az düğüm** üretse bile yazmayı zorlar.
  Varsayılan davranış bir bekçi — kod silen bir refactor'dan sonra bilinçli
  olarak `--force` verirsin, kazara küçülmede graf korunur.
- `--no-viz`: 8.313 düğümlük grafta `graph.html` üretimi pahalı.
- `--no-label`: topluluk adlandırma **tek LLM adımı**; atlarsan "Community 12"
  kalır, token harcanmaz. Bu depoda `manifest.json`'daki 771 girdinin
  771'inde `semantic_hash` boş — semantik çıkarım hiç koşmadı, graf tamamen
  AST.

**Kim koşturur:** `CLAUDE.md` §2 gereği koordinatör, arka planda, tek sahiple —
beş ajanın paralel `graphify update` koşturması aynı maddenin gerekçesindeki
"hiçbiri diğerinin maliyetini göremiyor" hâlinin ta kendisi. Bu belge tazeleme
komutlarını **koşturmadı** (ağır iş yasağı); bayraklar `graphify --help`
çıktısından birebir alındı.

### `graph.json` çakışması

`.gitattributes` eşlemeyi taşıyor (`graphify-out/graph.json merge=graphify`),
ama sürücünün kendisi **yerel git config'te** (`merge.graphify.driver`),
commitli değil. Sonuç: eşleme taşınabilir, **sürücü değil**. Yeni bir klonda
git eşlemeyi bulur, sürücüyü bulamaz ve 12,3 MB'lık JSON'ı **normal metin
merge'ine** düşürür — `CLAUDE.md` §5'in "üretilen dosyalar elle
birleştirilmez" kuralının ihlal edildiği an. Çakışma gördüğünde sıra:

```bash
graphify hook install                            # sürücüyü kaydet (idempotent)
git checkout --theirs graphify-out/graph.json    # ya da --ours; önemsiz
graphify update .                                # kaynaktan yeniden üret
```

Üretilen bir dosya için anlamı olan tek çözüm **yeniden üretmek**; ardından
yeniden üretilecekse hangi tarafın alındığı önemsizdir.

---

## 4 · Sınırlar — grafın göremediği şeyler

Bu bölüm zorunlu, çünkü grafın **yanlış cevabı sessiz**: "bulunamadı" der,
hata vermez, sayaç artırmaz. `CLAUDE.md` §7'nin tarif ettiği en pahalı hata
sınıfı bu.

### 4.1 · Graf zamanda donmuş — ölçülmüş örnek

`sim/Bizigo.Simulators/` projesi grafın derlendiği commit'ten sonra eklendi
(`85343ff`):

```
$ graphify explain "SyslogEmitter"
No node matching 'SyslogEmitter' found.

$ git grep -l "SyslogEmitter"
docs/epic/fs-simulatorler/index.md
sim/Bizigo.Simulators/Program.cs
sim/Bizigo.Simulators/SyslogEmitter.cs
tests/Bizigo.UnitTests/EventRetentionTests.cs
```

Graf "yok" dedi; sınıf altı dosyalık bir projenin göbeğinde duruyor.

> **Kural: grafın "bulunamadı" cevabı bir bulgu değildir.** Rapora yazmadan
> önce `git grep` ile teyit et. `CLAUDE.md` §10'un "aradım, yok" / "aramadım"
> ayrımı burada üçe çıkıyor: *graf bulamadı* üçüncü bir hâl ve tek başına
> hiçbir şey ispatlamıyor.

### 4.2 · `type` alanı bir taksonomi değil

8.313 düğümün `type` alanı sayımı: **8.278'inde alan yok**, 35'inde
`namespace`. Düğüm tipine göre filtrelemeye kalkarsan neredeyse her şeyi
kaçırırsın. Gerçek ayrım `file_type` / `node_kind` / `_callable` alanlarında
(`node_kind` 1.252 düğümde dolu).

### 4.3 · Diller arası sınır: `ui` ile `src` arasında sıfır kenar

19.000 kenarı üst dizinlerine göre saydım: `tests ↔ src` 1771, **`ui ↔ src` 0**,
**`ui ↔ sidecar` 0**. C# backend ile TypeScript UI arasında hiçbir kenar yok —
olması da beklenmez, çünkü aralarındaki bağ AST'de değil **HTTP
sözleşmesinde**. Pratik sonucu: bir uç sözleşmesini değiştiriyorsan `affected`
sana backend tüketicilerini verir, **UI tüketicisini asla vermez**. Onun için
`npm run api:check` ve `ProducesContractTests` var (`CLAUDE.md` §8).

Aynı sebeple çalışma zamanında kurulan bağlar da grafta yok: DI kaydı,
yansıma, dizgeden çözülen tip adı. Kenar sözlüğünde `dynamic_import` 20,
`indirect_call` 7 — 19.000 içinde. Bir DI bağını grafta aramanın boşa
çıkacağını beklemek doğru.

### 4.4 · Yapılandırma dosyaları grafta hiç yok

```
manifestteki yml/yaml: 49  -> grafta dugumu olan: 0
manifestteki json:     24  -> grafta dugumu olan: 4
manifestteki md:      113  -> grafta dugumu olan: 113
```

`deploy/docker-compose.yml`, `.github/workflows/ci.yml`: manifestte var,
grafta **düğümü yok**. Bu doğrudan `CLAUDE.md` §5'in anlattığı olaya bağlanıyor
— iki ajanın compose'a ayrı ayrı `redis` eklemesiyle YAML'ın ayrıştırılamaz
hâle geldiği olay. **O kırığı graf göremezdi.** Yapılandırma yüzeyinin bekçisi
`docker compose config --quiet`, graf değil.

### 4.5 · Raporun metadata'sı ve özet sayıları güvenilir değil

`GRAPH_REPORT.md`'nin ilk satırı
`# Graph Report - graphify-posthog-maestro-setup-851963` diyor —
bu deponun **başka bir worktree'sinin adı** (`git worktree list` doğruladı).
Graphify projeyi **koştuğu dizinin adıyla** etiketliyor, deponun adıyla değil;
`CLAUDE.md` §4'ün "dizin adına güvenme" maddesinin bir başka yüzü.

Özet sayıları da kendi içinde tutarsız: Summary satırı
`420 communities (394 shown, 26 thin omitted)` diyor, ama
`grep -c '^### Community'` **391** veriyor ve fark açıklanmıyor. Sonuç: **rapor
gezinme için iyi, alıntı için değil**; bir sayıyı rapora dayandıracaksan
`graph.json`'dan kendin say.

### 4.6 · Exit kodları: "bulunamadı" iki farklı kod dönüyor

Aşağıdaki beş satırın her biri **tek tek koşturularak ölçüldü**:

| Vaka | Mesaj nereye | exit |
| --- | --- | --- |
| `explain <var-olmayan-düğüm>` | stdout | **0** |
| `affected <eşleşmeyen-dosya>` | stdout | **0** |
| `path A B` — ikisi de var, **yönlü yol yok** | stdout | **0** |
| `path A B` — A ya da B **grafta yok** | **stderr** | 1 |
| `explain <belirsiz-ad>` → `Ambiguous` | stdout | 1 |

Ayrım şu: bir **düğüm adı hiç çözülemiyorsa** `path` kırmızı yanıyor, `explain`
yanmıyor. Ve "soru geçerli, cevap boş" olan üç vakanın hepsi **exit=0**. İki
pratik sonucu var, ters yönlere bakıyorlar:

1. **Üst üç satır sessiz.** Bir CI adımı ya da betik `affected`/`explain`/`path`
   çağırıp yalnızca exit koduna bakarsa "cevap yok"u başarı sayar — hata yok,
   sayaç yok, belirti yok (`CLAUDE.md` §7). Exit koduna değil **çıktı metnine**
   bak.
2. **Dördüncü satır `2>/dev/null` ile kayboluyor.** `path`'in tek teşhis mesajı
   stderr'e gidiyor; sürüm uyarısını susturmak için `2>/dev/null` yazdıysan
   `No node matching '...' found.` satırını da yutarsın ve elinde boş stdout
   ile exit=1 kalır. Uyarıyı `graphify install` ile kaynağında sustur.

### 4.7 · Küçük ama ısıran davranışlar

| Davranış | Sonucu | Ne yap |
| --- | --- | --- |
| `explain` dosya yolu verilince dosya düğümünü döndürüyor (derece 2) | Yanlışlıkla "bağlantısı yok" sonucu | Tam düğüm id'si kullan — §2.3 |
| `path` varsayılan yönlü, graf yönsüz | "Yol yok" yanlış negatifi, üstelik exit=0 | `--undirected` — §2.4 |
| `query --budget` kolayca gözden kaçacak biçimde kesiyor | 155'in 9'una bakıp karar vermek | `TRUNCATED` satırını oku — §2.5 |
| `docs/graphify.md` ve `README.md`'nin `path` örneğindeki `EventsController` bu depoda hiç var olmadı | Örnek kopyalayan boş cevap alıyor | §2.4'teki `--undirected` örneğini kullan |

---

## 5 · `.claude/settings.json` kancası — ne yapıyor

Depoda bir `PreToolUse` kancası var: `Bash|Grep` için `graphify hook-guard
search`, `Read|Glob` için `graphify hook-guard read`. `hook-guard` alt komutu
`graphify --help` çıktısında **listelenmiyor**; denendi
(`echo '{}' | graphify hook-guard read` → çıktı yok, exit=0). Yani komut var ve
sessizce geçiyor. Amacı, arama/okuma araçlarını kullanmadan önce grafa bakmayı
hatırlatmak; şu hâliyle **hiçbir şeyi engellemiyor**. Bunu "graf zorunlu" diye
okuma.

---

## 6 · Kopyala-yapıştır kartı

```bash
# Oryantasyon
graphify god-nodes --top 12

# Etki analizi (geniş → dar)
graphify affected <yol/dosya.cs> --depth 2
graphify affected <yol/dosya.cs> --relation imports --depth 1

# Tüketici listesi ("<--" satırları + "Grouped by file" bölümü)
graphify explain "<tam_dugum_id>"

# Bağ arama (yönsüz; yönlü arama yanlış negatif verir — üstelik exit=0)
graphify path "A" "B" --undirected

# Serbest soru
graphify query "..." --budget 2000

# Cevap "yok" ise — TEYİT ET (§4.1)
git grep -n "<sembol>"

# Tazelik + ıraksama (her oturumun başında, ucuz)
C=$(python3 -c "import json;print(json.load(open('graphify-out/graph.json'))['built_at_commit'])")
git merge-base --is-ancestor "$C" HEAD || echo "IRAKSAK: grafta HEAD'de olmayan $(git rev-list --count "HEAD..$C") commit var"
echo "ortak atadan beri geride: $(git rev-list --count "$(git merge-base "$C" HEAD)..HEAD") commit"
```

> Kartta bilerek `2>/dev/null` yok: sürüm uyarısını yutmak `path`'in hata
> mesajını da yutuyor (§4.6). Uyarıyı `graphify install` ile kaynağında sustur.

Son blok bu belgenin en kısa özeti: **grafı kullanmadan önce ondan ne kadar
ıraksadığına bak.** 36 commit geride ve 3 commit yan hatta duran bir graf, hem
kör noktası hem de artık doğru olmayan bilgisi olan bir cevap üretir — ve
bunların hiçbirini sana söylemez.
