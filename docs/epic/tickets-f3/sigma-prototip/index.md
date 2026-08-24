---
title: "T30 — Sigma pipeline prototipi"
kind: ticket
status: 2
---

# T30 — Sigma pipeline prototipi

**Bağımlılık:** — · **Sonraki:** T31 · **Yöneten karar:** K36

## Amaç

**Çıktısı kod değil, bir sayı:** Sigma kuralı başına eşleme maliyeti. F3'ün
detection kolunun kapsamı bu sayıya göre seçilecek.

<user_quoted_section>Bu ticket atılabilir kod üretiyor. Prototipin kendisi korunmayacak;korunacak olan ölçüm ve ondan çıkan kapsam kararı.</user_quoted_section>

## Neden gerekiyor

Ölçüldü: `SigmaHQ/pySigma-pipeline-ocsf` SigmaHQ kataloğunun %80'ine dokunuyor
ama bizim `events_ocsf` görünümümüze karşı **0 kural** olduğu gibi çalışıyor.
Ayrıntı: [Sigma araştırması](../../sigma-clickhouse-arastirmasi/index.md).

Yani kendi `ProcessingPipeline`'ımızı yazmak zorundayız. Açık olan tek şey
**ne kadar** yazacağımız.

## Kapsam

### İçinde

- 20-30 kurallık bir örneklem seç. Örneklem **bizim evrenimizden**: F1
kataloğunun tanıdığı dört vendor (FortiGate, Cisco ASA, MikroTik, nginx) ve
`firewall` / `network_connection` / `dns` / `dns_query` kategorileri.
- Bu örneklem için elle `ProcessingPipeline` yaz: alan adı eşlemesi, değer
dönüşümü, gerekiyorsa `class_uid` ekleme.
- Üretilen SQL'i **canlı ClickHouse'ta koştur.** Önceki ölçüm kolon listesine
karşıydı ve sorgu hiç çalıştırılmadı — bu kez çalıştırılacak.
- Altın örneklerimizle sına: kural gerçekten eşleşiyor mu, yanlış pozitif var mı.

### Ölçülecekler

| Soru | Neden |
| --- | --- |
| Kural başına kaç satır eşleme? | Kapsam kararının birimi |
| Kural başına ne kadar süre? | 269 kuralın gerçek maliyeti |
| Kaçı **çalışır** hâle geldi? | "Derlendi" ile "doğru sonuç veriyor" farklı |
| Üretilen SQL canlı ClickHouse'ta koşuyor mu? | Nokta/alt tire, `FROM logs` sorunu |
| `unmapped` Map erişimi nasıl çözülüyor? | Pipeline `unmapped.X` üretiyor, bizde `Map` |

### Dışında

- Kalıcı pipeline — T31.
- Derleme hattı ve versiyonlama — T32.

## Kabul kriterleri

- ✅ Ölçüm sonuçları bir artifact'a yazıldı: **çalışır oran** ve **tuzaklar**
tamam ([T30 ölçümü](../../t30-sigma-olcumu/index.md), on bir tuzak).

  ⚠️ **Kural başına maliyet ÖLÇÜLMEDİ.** Payda `matches` olacaktı ve o sayı
  hâlâ oynuyor: bu turda dört kez değişti (24 → 21 → 15 → ≥14). Bugünkü
  eşleme satırı **208 (42 alan)** ama bölüneceği sayı kesinleşmeden yazılan
  bir oran, bu belgenin dört kez düştüğü tuzağın beşincisi olurdu.

  269 kuralın toplam maliyeti de buna bağlı ve ölçekleme uyarısı geçerli:
  çarpım **ayrık alan** üzerinden yapılmalı, kural sayısı üzerinden değil.
  Artifact'ın "Hâlâ ölçülmedi" bölümünde duruyor.

- ✅ En az bir kural canlı ClickHouse'ta koştu ve **doğru sonucu verdi**:
`routeros_forward_new`, Kapı 3'ün `at_least_one` beyanıyla, 1 satır.

  *"Doğru sonuç"*un ayrıca sınanması gerekiyordu ve gerekçesi ölçüldü:
  `asa_teardown_rst` **eşleşiyordu** ama `RST` yalnızca `first`/`burst`
  sözcüklerinin içine denk geliyordu. Eşleşme sayısı doğruluk kanıtı değil.

- ✅ Kapsam önerisi gerekçesiyle yazıldı: **`firewall` + `network_connection`**.

  Karar orandan değil **verinin varlığından** çıkıyor — DNS'in verisi yok,
  hiçbir parser sorgu adı üretmiyor, o kurallar derlenmiyor bile. Oran
  (`≤%43`, payda `≥14`) bir **alt sınır** ve öyle yazıldı.

## Kapanırken karşılanmayan

**Kural başına maliyet.** Ticket'ın ilk kriterinin üçte biri. Bilerek açık
bırakılıyor: payda kesinleşmeden yazılacak bir sayı, ölçümün kendisinin
uyardığı hatayı yapmak olur. Kesin payda 2'nin `(product, column)` ölçümünden
gelecek ve T38'de yeniden okunacak.

## Notlar

Bilinen tuzaklar (araştırmadan):

- Pipeline noktalı OCSF yolu üretiyor (`dst_endpoint.ip`), K30'un görünümü
düzleştirilmiş ad kullanıyor (`dst_endpoint_ip`).
- Backend `FROM logs` yazıyor; bizim tablo `events` / `events_ocsf`.
- Tırnaklama tutarsız: aynı SQL içinde hem backtick'li hem tırnaksız noktalı ad
görülmüş — tırnaksız hâli ClickHouse'ta derlenmiyor.
- `type_uid` hesabında üç kusur ölçüldü (`3002001`, `driver_load` çakışması,
`image_load` yanlış sınıf).

Referans akış: [`clicksiem/sigma_rules`](https://github.com/clicksiem/sigma_rules).
