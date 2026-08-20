---
title: "T08 — Vendor parser kataloğu ve altın örnekler"
kind: ticket
status: 2
---

<user_quoted_section>Durum (t08-vendor-katalogu dalı, commit edilmedi). Katalog yazıldı ve tümkapılar yeşil; commit onayı ve iki karar bekliyor. Ayrıntılı geri besleme:T08 → T05 motor geri beslemesi.
A2A kanalı rapor gönderilirken cevap vermedi; bu bölüm o raporun kalıcı hâlidir.
Yapıldı — 4 vendor / 8 parser (cisco.asa, fortinet.fortigate,mikrotik.routeros, nginx.access), 87 altın örnek satırı, 62 gömülü test,6 yeni eşleme tablosu, bizigo parser coverage komutu, 11 birim testi.
KapıSonuçparser lint catalog/parsersexit 0parser test catalog/parsers62 geçti, 0 kaldıparser coverage catalog/parsers87 satır — ok 84, partial 3, failed 0 (%0.0)dotnet test tests/Bizigo.UnitTests242 geçti, 0 kaldı
Üç partial bilinçli ve etiketli (2 FortiGate satırında date/time alanıyok, 1 ASA satırında zaman damgası yok). Her örnek satırı kendi vendor'ınınparser'ına düştü — çapraz sızıntı yok.
Bekleyen kararlar:
.github/workflows/ci.yml (paylaşılan dosya) — kapsam raporu adımı henüz eklenmedi, çakışma riski yüzünden onay bekliyor: dotnet run --project src/Bizigo.Cli --no-build --configuration Release -- parser coverage catalog/parsersT05'e ait olup dokunulan dosyalar (hepsi additive): src/Bizigo.Cli/Program.cs (+1 alt komut), src/Bizigo.Cli/ParserCommandHandlers.cs (+1 metot), src/Bizigo.Parsing/Testing/SampleCoverage.cs (yeni), tests/Bizigo.UnitTests/VendorCatalogTests.cs (yeni).Commit onayı. Önerilen mesaj: Add vendor parser catalog for four network vendors (T08)
Not: catalog/mappings/ T07'nin alanı. 6 tablo eklendi veocsf_network_activity genişletildi; mevcut hiçbir satır silinmedi veyadeğiştirilmedi. T07'nin haberdar edilmesi gerekebilir.
Kapsamdaki sapma: ticket dört parser istiyor, sekiz yazıldı. Sebep: mapbloğu satır içeriğine göre dallanamıyor (F1 §3) ve aynı vendor'ın farklı mesajaileleri farklı OCSF sınıflarına ait. Alternatif, class_uid'i vendor'larınyarısı için yanlış yazmaktı. Katalog kuralı "OCSF sınıfı ailesi başına birparser" oldu.</user_quoted_section>

# T08 — Vendor parser kataloğu ve altın örnekler

**Bağımlılık:** T06, T07 · **Sonraki:** —
**Yöneten belgeler:** [F1 §3, §13, §14](../../f1-teknik-plan/index.md) · [K2](../../mimari-kararlar/index.md)

## Amaç

**Motorun asıl sınavı.** F1 planında bu adım kasten ortada duruyor: motor
tamamlanmadan gerçek vendor logu görülmezse formatın eksikleri en pahalı anda
ortaya çıkar.

## Kapsam

### İçinde

1. **Dört vendor parser'ı**, ağ alanının (K2) en yaygınları:
  - **FortiGate** — key=value, tırnaklı değerler, `devname=` / `type=`
  - **Cisco ASA** — `%ASA-6-302013:` mesaj kodu + serbest metin; en zoru
  - **MikroTik** — kısa, topic tabanlı, RouterOS
  - **nginx** — combined + JSON access log (yaygın ve kolay; motorun `json` adımını doğrular)
2. **Altın örnek dosyaları** — `catalog/parsers/<id>/samples/`. **Gerçek cihaz**
** çıktısı olmalı, elde uydurulmuş değil.** Uydurulmuş örnek, motorun eksiğini değil
kendi hayal gücümüzü test eder.
3. **Her parser için gömülü `tests` bloğu** — en az bir "beklenen alanlar" testi,
en az bir negatif test (bu parser'a **düşmemeli** olan satır).
4. **`map` blokları** — dördü de `core` + `ocsf` + `otel` bölümlerini dolduruyor.
5. **Kapsam raporu** — her parser için: örnek dosyadaki satırların yüzde kaçı `ok`,
kaçı `partial`, kaçı `failed`. Bu rapor CI çıktısında görünür.

### Dışında

Kataloğun geri kalanı (PAN-OS, Juniper, F5, HAProxy) — F2'deki editörle ve F4'teki
keşif senaryosuyla çok daha ucuza gelecek. F1'de dört tanesi motoru doğrulamaya yeter.

## Kabul kriterleri

Dört parser'ın altın örnek dosyalarının %100'ü testte geçiyorCisco ASA'nın en az 5 farklı mesaj kodu doğru ayrıştırılıyorNegatif testler geçiyor: bir vendor'ın satırı başka vendor'ın parser'ına düşmüyorDördü de core alanlarını dolduruyor; OCSF/OTel görünümünden okunabiliyorKapsam raporu CI çıktısında; failed oranı her parser için raporlanıyor

## Notlar

- Son kabul kriteri en önemlisi. Bu ticket'ın gerçek çıktısı dört YAML dosyası değil,
**motorun gerçek yükle karşılaşmış olması.** Hiçbir eksik bulunmadıysa muhtemelen
örnekler yeterince gerçek değil.
- Cisco ASA bilinçli seçildi: mesaj kodu + serbest metin karışımı, formatın en zor
hali. Bunu taşıyabilen motor çoğu şeyi taşır.
- Örnek dosyalarda gerçek IP/kullanıcı adı varsa maskelenmeli — ama **yapı**
**bozulmadan** (aynı uzunluk, aynı biçim).
