---
title: Atomik yayın — çalışan motoru bozmadan değiştirmek
category: skills
tags: [log-analiz, mimari, yordam, bizigo]
aliases: [K33, parser yayın akışı, taslak-inceleme-yayın]
relationships:
  - target: "[[concepts/f2-kapsam-tek-kapi]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: uses
  - target: "[[skills/f2-ekran-tutarliligi]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: related_to
sources:
  - docs/epic/f2-teknik-plan/index.md
  - docs/epic/tickets-f2/parser-yayin-akisi/index.md
  - docs/epic/tickets-f2/parser-editoru/index.md
  - docs/epic/tickets-f2/katalog-ekrani/index.md
  - docs/epic/f2-kapanis/index.md
source_digest: "sha256-12/v1 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/f2-teknik-plan/index.md=70c97eb131ab docs/epic/tickets-f2/katalog-ekrani/index.md=e9f77ef3d17c docs/epic/tickets-f2/parser-editoru/index.md=b67acb46e9be docs/epic/tickets-f2/parser-yayin-akisi/index.md=1addd87ba7bf"
summary: Parser'ı üründe yazıp yayınlama akışının yordamı — zorunlu kapılar, atomik referans değişimi, tek adımda geri alma ve kataloğun ikiye çıkan kaynağı.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.72
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:45:00Z
updated: 2026-08-24T17:45:00Z
---

# Atomik yayın — çalışan motoru bozmadan değiştirmek

K33'ün kararı: **taslak → inceleme → yayın, üründe.** Gerekçesi tek cümle —
*"editör yazmadan işe yaramaz"* — ve tersi de doğru: T19'un editörü T18'in
akışı olmadan yalnızca bir deneme kutusu.

Bu sayfa akışın **yordamı**: hangi kapılar zorunlu, neden, ve hangi sorunun
cevabı testle sabitlenmek zorunda.

## Durum makinesi

```
taslak ──try──> taslak
taslak ──yayın istendi──> incelemede
incelemede ──değişiklik istendi──> taslak
incelemede ──onaylandı──> yayında
yayında ──önceki sürüme──> geri alındı
yayında ──yeni sürüm──> incelemede
```

Kaynak: `docs/epic/f2-teknik-plan/index.md` (K33 durum diyagramı) ve T18'in
kapsam listesi.

## Zorunlu kapılar — hiçbiri isteğe bağlı değil

1. **`parser lint` temiz** (şema + ReDoS). Gerekçe bir değişmezin korunması:
   F1'in `GROK003 = 0` sonucu **21'den 0'a**, dört ayrı daraltmayla elde edildi
   ve son iki daraltma bağlama özeldi. Yayın kapısı o değişmezin bekçisi;
   olmadan katalog **ilk katkıda** geri izlemeye düşer ve kimse fark etmez.
2. **Gömülü `tests` bloğu boş olamaz** ve hepsi geçmeli (F1'de şema düzeyinde
   zaten zorunlu).
3. **Rol ayrımı:** taslağı herkes yazar, yayını `author`/`admin` yapar. `author`
   olmayan yayın ucunu çağırdığında **403**. K16'nın 50 kişilik kurumunda bu
   ayrım anlam kazanıyor.

## Atomik yayın nasıl "atomik"

Yeni katalog **tamamen kurulup derlenene kadar** eskisi yerinde kalıyor, sonra
tek referans değişimi. `ParserCatalog` bunu zaten yapıyordu; F2'de eksik olan
**taslaktan besleme**ydi.

Kabul kriteri bunu davranış olarak yazıyor: yayın sırasında derleme hatası
çıkarsa **çalışan katalog değişmiyor ve ingest kesintisiz sürüyor.** Geri alma
da aynı atomik yolu kullanıyor — tek adım, çünkü yayın sonrası fark ancak
üretim trafiğinde görülüyor.

## En sinsi kısım: kataloğun kaynağı ikiye çıkıyor

T18 kendi notunda söylüyor: katalog artık hem repodaki dosyalardan hem
yayınlanmış taslaklardan besleniyor, yani *"repoda ne var"* sorusu **tek başına
cevap değil**.

Bunun tek kabul edilebilir kapanışı bir test: aynı `parser_id` için repo dosyası
ile yayınlanmış taslak birlikte varsa **hangisinin kazandığı testle
sabitlenmiş** olmalı. Sabitlenmezse kazananı ortam belirler ve iki ortam
sessizce farklı ayrıştırır — [[concepts/sessiz-yanlis-davranis]] sınıfının
tanımı. ^[inferred]

## Editörün akışa kattığı iki ayrım (T19)

- **`timed_out` gösteriliyor.** Sıfırdan farklıysa sonuç *"uymadı"* değil
  **"ölçülemedi"** demek; ikisini karıştırmak sağlıklı bir parser'ı karantinaya
  sokar.
- **Dispatcher kademesi gösteriliyor.** Envanter bağı yerine literal filtreye
  düşen satır, parser doğru olsa bile **envanterin eksik olduğunu** söylüyor.
  Yani ekran parser'ı değil, boru hattının hangi kademesinin karar verdiğini de
  görünür kılıyor.
- **Örnek satır ham arşivden çekiliyor** — uydurma örnekle yazılan parser
  üretimde çuvallıyor.

`POST /v1/parsers/try` hiçbir şey yazmıyor ve F1'de **tam bu ekran için**
tasarlanmıştı; `author` rolü istiyor, yani okuyucu bu ekranı göremiyor.

## Kataloğun sağlığı görünür kalmalı (T20)

- İnceleme kuyruğunda **satır satır YAML farkı** (önceki sürüme karşı).
- Sürüm geçmişi ve tek adımda geri alma; geri alma sonrası ingest kesintisiz.
- Her parser'ın altın örneklerinde `ok/partial/failed` oranı — F1'de **86/1/0**
  ve kataloğun sağlığının **tek sayısal göstergesi**.
- Katalog genelinde `GROK003` sayacı: sıfırdan farklıysa ekran **uyarı
  üretiyor**, sessizce sayı olarak durmuyor.

Son madde bu deponun tekrar eden refleksi: kazanılmış bir değişmez, görünür
olmadığı gün sessizce kaybediliyor.

## Yordamın kontrol listesi

- [ ] Lint kapısı taslakla birlikte koşuyor mu, yoksa yayın anında mı?
- [ ] Yayın hatası çalışan kataloğu **değiştirmiyor** — test var mı?
- [ ] Geri alma aynı atomik yoldan mı gidiyor?
- [ ] `author` olmayan 403 alıyor mu?
- [ ] Repo dosyası vs. yayınlanmış taslak çakışması testle sabit mi?
- [ ] `GROK003 > 0` ekranda uyarıya dönüyor mu?

## Bugünkü durum

Kapanış belgesi (§6) F2'nin dört uçtan uca akışından **ikisinin akış, ikisinin
parça** olduğunu yazıyor; parser akışı var olan ikiden biri. Yani bu yordam
uçtan uca koşturulmuş hâliyle kayıtlı.

## Kaynaklar

- `docs/epic/f2-teknik-plan/index.md` — K33 ve "Parser yayın akışı" bölümü
- `docs/epic/tickets-f2/parser-yayin-akisi/index.md` — T18 kapılar, atomiklik, ikili kaynak
- `docs/epic/tickets-f2/parser-editoru/index.md` — T19 canlı test, `timed_out`, dispatch kademesi
- `docs/epic/tickets-f2/katalog-ekrani/index.md` — T20 inceleme kuyruğu, geri alma, `GROK003`
- `docs/epic/f2-kapanis/index.md` — §6 uçtan uca akışların durumu
