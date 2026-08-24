---
title: Vault kurulumu
---

# `docs/wiki` — Obsidian vault'u nasıl açılır

Bu dizin bir **Obsidian vault'u**. `obsidian-wiki` skill'leri (`wiki-ingest`,
`wiki-query`, `wiki-lint`, `tag-taxonomy`…) buraya yazıyor, Obsidian ise aynı
dizini okuyor. Araç depoya vendor **edilmiyor** — `tools/obsidian-wiki/`
`.gitignore`'da.

Depo tarafındaki gerekçeler README kökünde: "Bilgi tabanı — Obsidian vault".

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
/Users/hakkisagdic/Projects/bizigo-loganalyzer/.claude/worktrees/proje-ozeti-tanitim-353f6e/docs/wiki
```

4. Obsidian "Trust author and enable plugins?" diye sorarsa — bu vault'ta
   **hiç topluluk eklentisi yok**, sadece `.obsidian/app.json` ve
   `appearance.json` var. Güvenli.

Kısayol (aynı işi yapar, vault'u kaydeder):

```bash
open "obsidian://open?path=%2FUsers%2Fhakkisagdic%2FProjects%2Fbizigo-loganalyzer%2F.claude%2Fworktrees%2Fproje-ozeti-tanitim-353f6e%2Fdocs%2Fwiki"
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

## ⚠️ Bilinen sorun: vault yolu bir **worktree**'yi gösteriyor

`~/.obsidian-wiki/config.bizigo` ve `tools/obsidian-wiki/.env` şu an
`.claude/worktrees/proje-ozeti-tanitim-353f6e/docs/wiki` yolunu taşıyor.
`CLAUDE.md` §4'e göre **iş `main`'e girip doğrulandıktan sonra worktree
siliniyor** — o an bu yol boşa düşer: Obsidian vault'u bulamaz, skill'ler
yazacak yer bulamaz.

Bu dal `main`'e girdikten **sonra** iki dosyayı da ana depo yoluna çevir:

```bash
NEW=/Users/hakkisagdic/Projects/bizigo-loganalyzer

# 1) profil
sed -i '' "s#/Users/hakkisagdic/Projects/bizigo-loganalyzer/.claude/worktrees/[^/]*#$NEW#g" \
  ~/.obsidian-wiki/config.bizigo

# 2) araç .env (aynı değeri ikinci kez tutuyor)
sed -i '' "s#/Users/hakkisagdic/Projects/bizigo-loganalyzer/.claude/worktrees/[^/]*#$NEW#g" \
  "$NEW/tools/obsidian-wiki/.env"

# 3) doğrula
grep -n OBSIDIAN_VAULT_PATH ~/.obsidian-wiki/config.bizigo "$NEW/tools/obsidian-wiki/.env"
```

Obsidian tarafında da vault yeniden açılmalı (eski yol kayıtlı kalır): önce
**"Open folder as vault"** ile yeni yolu aç, sonra eskisini vault listesinden
kaldır.

---

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
