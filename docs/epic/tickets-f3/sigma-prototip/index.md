---
title: "T30 — Sigma pipeline prototipi"
kind: ticket
status: 0
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

- Ölçüm sonuçları bir artifact'a yazıldı: kural başına maliyet, çalışır oran,
karşılaşılan tuzaklar.
- En az bir kural **canlı ClickHouse'ta** koşup doğru sonucu verdi.
- Kapsam önerisi gerekçesiyle yazıldı: hangi kategoriler/vendor'lar F3'e girsin.

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
