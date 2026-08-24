---
title: Vault kurulumu
---

# `docs/wiki` — Obsidian vault'u nasıl açılır

Bu dizin bir **Obsidian vault'u**. `obsidian-wiki` skill'leri (`wiki-ingest`,
`wiki-query`, `wiki-lint`, `tag-taxonomy`…) buraya yazıyor, Obsidian ise aynı
dizini okuyor. Araç depoya vendor **edilmiyor** — `tools/obsidian-wiki/`
`.gitignore`'da.

Depo tarafındaki gerekçeler README kökünde: "Bilgi tabanı — Obsidian vault".

Katalog [[index]], bugünkü durum [[hot]], işlem kaydı [[log]], etiket sözlüğü
[[_meta/taxonomy]].

---

## 0 · Kapsam kararı — bu vault neye ait, neye ait değil

**Bu vault yalnızca `bizigo-loganalyzer` deposuna ait ve geçicidir.**

- **Kaynağı tek bir depo.** Her sayfa bu deponun kendi belgelerinden
  (`docs/epic/`, `CLAUDE.md`, `README.md`, `docs/graphify.md`,
  `docs/ekran-goruntuleri/`) damıtıldı ve frontmatter'ında o dosyaları
  **yolla** gösteriyor. Başka bir deponun, başka bir vault profilinin ya da
  oturum geçmişinin bilgisi buraya girmiyor.
- **Projeler arası hafıza değil.** Bu vault "geçmişte ne konuşmuştuk" deposu
  olarak kullanılmıyor. Aynı makinede başka profiller var
  (`~/.obsidian-wiki/config.*`); onlar ayrı kalıyor ve bu vault onlara
  yazmıyor.
- **Geçici.** Ömrü, damıttığı belgelerin işe yaradığı süre kadar. Kaynak
  belgeler arşive kalktığında ya da faz kapanışları anlamını yitirdiğinde
  vault da kalkar; kalıcı olması hedeflenmiyor.

Gerekçe §9'un kuralı: **ikinci kopya yazma.** Bu sayfalar `docs/epic/`'in
kopyası değil, belgelerin *arasından* geçen kararların damıtılmış hâli. Kapsam
genişlerse bu ayrım kaybolur ve vault yavaşça ikinci bir belge yığınına döner.

---

## 1 · Aracı getir (klon başına bir kez)

```bash
git clone --depth 1 https://github.com/ar9av/obsidian-wiki.git tools/obsidian-wiki
```

## 2 · Profili aktif et

Bu makinede **birden çok proje profili** var ve `~/.obsidian-wiki/config` bir
sembolik bağ. Bizigo'ya geçmek:

```bash
ln -sfn ~/.obsidian-wiki/config.bizigo ~/.obsidian-wiki/config
```

Geri dönmek (`dukkan-defteri` profili korunuyor, ezilmiyor):

```bash
ln -sfn ~/.obsidian-wiki/config.dukkan-defteri ~/.obsidian-wiki/config
```

Hangisinin aktif olduğunu görmek:

```bash
ls -l ~/.obsidian-wiki/config
```

> ⚠️ **İki yapılandırma kaynağı var.** Skill'lerin çözüm sırası: önce CWD'den
> yukarı doğru `.env`, sonra `~/.obsidian-wiki/config`. Yani
> `tools/obsidian-wiki/.env` varsa ve CWD o ağacın içindeyse **o kazanıyor**.
> Şu an ikisi aynı yolu gösteriyor; birini değiştirirken diğerini de değiştir,
> yoksa iki farklı vault'a yazan iki skill koşumu elde edilir.

## 3 · Obsidian'da vault'u aç

Obsidian kurulu (`/Applications/Obsidian.app`) ama **henüz hiçbir vault kayıtlı
değil** (`~/Library/Application Support/obsidian/obsidian.json` yok). İlk açış:

1. Obsidian'ı başlat.
2. Açılış ekranında **"Open folder as vault"** ("Klasörü kasa olarak aç").
3. Şu dizini seç:

```
/Users/hakkisagdic/Projects/bizigo-loganalyzer/docs/wiki
```

4. Obsidian "Trust author and enable plugins?" diye sorarsa — bu vault'ta
   **hiç topluluk eklentisi yok**, sadece `.obsidian/app.json` ve
   `appearance.json` var. Güvenli.

Kısayol (aynı işi yapar, vault'u kaydeder):

```bash
open "obsidian://open?path=%2FUsers%2Fhakkisagdic%2FProjects%2Fbizigo-loganalyzer%2Fdocs%2Fwiki"
```

Açıldıktan sonra sol altta **Graph view** (`Ctrl/Cmd+G`) düğümlerin birbirine
bağlandığını gösteriyor — `index.md`'den başlayıp iç bağlantıları izle.

### Önerilen topluluk eklentileri (elle kurulur)

| Eklenti | Ne için |
| --- | --- |
| Dataview | Frontmatter'ı sorgulamak, dinamik tablo üretmek |
| Graph Analysis | Genişletilmiş graf görünümü |
| Obsidian Git | Vault'u otomatik yedeklemek |

Hiçbiri zorunlu değil; vault onlarsız da tam çalışıyor.

---

## Vault yolu — kapandı, ama neden yazılı kalıyor

`~/.obsidian-wiki/config.bizigo` ve `tools/obsidian-wiki/.env` bir dönem
`.claude/worktrees/proje-ozeti-tanitim-353f6e/docs/wiki` yolunu taşıyordu.
`CLAUDE.md` §4'e göre iş `main`'e girdikten sonra worktree siliniyor — o gün
Obsidian olmayan bir vault açacaktı: hata yok, uyarı yok, sadece kaybolmuş bir
kasa. Bu deponun §7'de tarif ettiği sınıf.

İkisi de artık kalıcı yolu gösteriyor:

```
/Users/hakkisagdic/Projects/bizigo-loganalyzer/docs/wiki
```

**Bölüm silinmedi çünkü tuzak tekrar kurulabilir.** Vault'u bir worktree'den
açan biri Obsidian'a o yolu kaydettirir ve aynı yere geri döner. Yeni bir
kurulumda vault yolu **daima ana checkout'u** göstermeli.

**İkinci kopya kapandı — ve nasıl kapandığı önemli.** Aynı değer iki yerdeydi:
`tools/obsidian-wiki/.env` ve `~/.obsidian-wiki/config`. Çözüm sırası önce
`.env`'i okuyor, yani **yol `.env`'de durduğu sürece profil sembolik bağını
değiştirmek vault'u değiştirmiyordu** — iki proje aynı kasaya yazıyor ve hiçbir
şey hata vermiyordu. Profil mekanizması dekoratif kalmıştı.

Çözüm: `OBSIDIAN_VAULT_PATH` artık **yalnızca profil dosyasında**. `.env` diğer
29 ayarı taşımaya devam ediyor; yolun bilerek orada olmadığı, sebebiyle birlikte
dosyanın içine yazıldı.

Aynı turda iki bayat yol daha bulundu ve düzeltildi: `OBSIDIAN_WIKI_REPO` ve
`OBSIDIAN_SOURCES_DIR` de worktree'yi gösteriyordu, ve aracın klonu **yalnızca
worktree'de** duruyordu — o dizin silindiğinde vault yolu düzelmiş olsa bile
araç kaybolacaktı. Klon ana checkout'a taşındı.

## Vault yapısı

| Dizin | Ne durur |
| --- | --- |
| `concepts/` | Fikirler, zihinsel modeller |
| `entities/` | Kişi, kurum, araç, servis |
| `skills/` | Nasıl yapılır bilgisi |
| `references/` | Tek bir kaynağın özeti |
| `synthesis/` | Birden çok kaynağı kesen analiz |
| `journal/` | Zaman damgalı gözlemler |
| `projects/<ad>/` | Projeye özgü bilgi + `<ad>.md` genel bakışı |
| `_meta/` | `taxonomy.md` — kanonik etiket sözlüğü |
| `_raw/` | Ham taslaklar; `wiki-ingest` bunları sayfaya çevirip siler |
| `_staging/` | `WIKI_STAGED_WRITES=true` iken inceleme kuyruğu |
| `_archives/` | `wiki-rebuild` anlık görüntüleri |

Kök dosyalar: `index.md` (katalog), `log.md` (append-only işlem kaydı),
`hot.md` (son etkinliğin anlık görüntüsü).

`.obsidian/workspace.json` ve `cache` **commitlenmiyor** (`docs/wiki/.gitignore`)
— makineye özgü çalışma zamanı durumu.

## Bayatlama bekçisi — `source_digest`

Vault sayfaları depo belgelerinden **damıtıldı**. O belgeler değiştiğinde
damıtılmış cümle sessizce yanlış olur: derleme geçer, testler yeşil kalır, sayfa
yerinde durur — yalnızca artık doğru değildir. Hata yok, sayaç yok, belirti yok;
`CLAUDE.md` §7'nin adını koyduğu sınıfın ta kendisi
([[concepts/sessiz-yanlis-davranis]]).

**Mekanizma.** Her sayfa frontmatter'ında `sources:` taşıyor. Bekçi o
kaynakların **içeriğinden** deterministik bir dizge üretip sayfadaki
`source_digest:` alanıyla karşılaştırıyor:

```
source_digest: "sha256-12/v1 CLAUDE.md=3984257f89e8 docs/epic/f2-kapanis/index.md=c701d88f78fd"
```

Biçim yola göre sıralı, kaynak **başına** bir kısa hash. Tek bir toplam hash
bilerek kullanılmadı: toplam hash "bir şey değişti" der, *hangi kaynağın*
değiştiğini söyleyemez — bekçinin işe yaraması tam olarak o cümleye bağlı.

Kod: `tests/Bizigo.UnitTests/WikiSourceDigest.cs` (hesap),
`WikiSourceDigestTests.cs` (bekçi), `WikiSourceDigestStamper.cs` (damgalayıcı).
Denetlenen küme **taranıyor**, elle listeden gelmiyor
([[concepts/elle-tutulan-liste-bekciyi-korlestirir]]); elle kalan tek şey beş
satırlık muafiyet listesi (`index.md`, `hot.md`, `log.md`, `README.md`,
`_meta/taxonomy.md` — kaynağı olmayan gezinme/yapılandırma sayfaları) ve onu
büyütmek `ExpectedExemptCount` sabitini de değiştirmeyi gerektiriyor.

### Kırmızı yandığında ne yapılır

Sıra **önce oku, sonra damgala**:

1. **Gözden geçir.** Bekçi hangi sayfanın hangi kaynağı yüzünden bayatladığını
   söylüyor. O kaynağı aç ve sor: değişiklik sayfadaki cümleyi *yanlışlıyor mu*?
   Yanlışlıyorsa **önce sayfayı düzelt**.
2. **Yeniden damgala.**

   ```bash
   BIZIGO_WIKI_STAMP=1 dotnet test tests/Bizigo.UnitTests      --filter FullyQualifiedName~WikiSourceDigestStamper
   ```

3. **Değişkeni kaldırıp bekçiyi yeniden koştur.** Damgalayıcı ile bekçi aynı
   koşumda buluşamaz — buluşursa bekçi kendi çıktısını doğrular ve hiçbir şey
   kanıtlamaz. Bu iki taraflı kapatıldı: `BIZIGO_WIKI_STAMP=1` ile koşulduğunda
   bekçi kırmızı yanıyor.

Ters sıra — önce damgalayıp sonra bakmak — damgayı bir kayıt olmaktan çıkarıp
**gürültü bastırıcıya** çevirir. Bekçinin bütün değeri o sıradan geliyor.

**Yeni sayfa eklendiğinde** damgalayıcıyı koşturmak birleştirmenin parçası:
`sources` bildiren ama damgası olmayan sayfa bekçiyi kırmızı yakıyor, çünkü
üçüncü hâl (*kaynağı yok, sessizce atlanıyor*) bekçiyi tam da yeni sayfalarda
kör bırakırdı.

**`source_digest` elle düzenlenmez.** Elle yazılan damga, bekçiyi kaynağın
değil yazanın hafızasının bekçisi yapar.

## Sayfa yazarken

- Şablon ve frontmatter alanları: `llm-wiki` skill'i, "Page Template".
- Etiket seçmeden önce **[[_meta/taxonomy]]** okunur; listede olmayan etiket
  uydurulmaz, en fazla 5 etiket.
- Kaynağı olmayan iddia yazılmaz. Çıkarım `^[inferred]`, belirsizlik
  `^[ambiguous]` ile işaretlenir — işaretsiz olan kaynaktan çıkarılmış sayılır.

## Sırada ne var

```bash
# neyin ingest edilebilir olduğunu gör
/wiki-status

# depodaki belgeleri sayfaya çevir
/wiki-ingest

# bağlantı sağlığı
/wiki-lint
```

`OBSIDIAN_SOURCES_DIR` bu deponun `docs/` dizinini gösteriyor; yani
`docs/epic/`, `docs/arastirma/`, `docs/graphify*.md` hazır kaynak.
