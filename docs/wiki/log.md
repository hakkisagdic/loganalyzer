---
title: Wiki Log
---

# Wiki Log

Append-only. Her satır bir yazma turu; en yenisi altta.

- [2026-08-21T15:54:59Z] INIT vault_path="docs/wiki" categories=concepts,entities,skills,references,synthesis,journal
- [2026-08-24T16:15:22Z] SETUP dirs_added=_meta files_added=README.md,_meta/taxonomy.md note="wiki-setup iskeleti zaten tamdı; _meta/ eksikti"
- [2026-08-24T16:15:22Z] INGEST source="CLAUDE.md,README.md,docs/graphify.md,docs/epic/f2-kapanis/index.md" pages_created=6 pages_updated=0
- [2026-08-24T17:05:00Z→18:40:00Z] DISTILL agents=5 batches="17:05(6), 17:30(7), 17:31(7), 17:45(7), 18:40(7)" pages_created=34 pages_total=40 sources="docs/epic/ (40+ dizin), CLAUDE.md, README.md, docs/ekran-goruntuleri/BENIOKU.md" note="beş ajan paralel damıttı; §9 gereği sayfalar belge başına değil, belgelerin arasından geçen karar/kavram başına yazıldı"
- [2026-08-24T17:50:00Z] GUARD files_added=tests/Bizigo.UnitTests/WikiSourceDigest{,Tests,Stamper}.cs field_added=source_digest pages_stamped=40 exempt=5 note="bayatlama bekçisi; damgalayıcı ile bekçi aynı koşumda buluşamıyor"
- [2026-08-24T18:20:00Z] TIDY links_checked=313 links_broken_before=0 links_broken_after=0 orphans_before=2 orphans_after=0 links_added=4 files_rewritten=index.md,hot.md,log.md files_edited=README.md,skills/f3-eslesmeyen-kural-teshisi.md,concepts/olcum-sayi-kapsam-degil.md,skills/f1-deklaratif-parser-motoru.md,references/f2-kapanis.md source_digest=dokunulmadı
