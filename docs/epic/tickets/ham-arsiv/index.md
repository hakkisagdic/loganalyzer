---
title: "T04 — Ham arşiv: RustFS, manifest, scrub"
kind: ticket
status: 2
---

# T04 — Ham arşiv: RustFS, manifest, scrub

**Bağımlılık:** T03 · **Sonraki:** T11
**Yöneten belgeler:** [F1 §7.0, §7.1](../../f1-teknik-plan/index.md) ·
[K25, risk #13](../../mimari-kararlar/index.md)

## Amaç

Replay'in tek kaynağını kurmak — ve **RustFS'in veri kaybetmesi ihtimalini**
**varsayarak** kurmak. RustFS 1.0-beta; geliştiricilerin kendi üretim tavsiyesi yok.
Bu ticket'ın işi bu riski taşınabilir kılmak.

## Kapsam

### İçinde

1. **RustFS yazıcı** — `AWSSDK.S3` + özel endpoint. **RustFS'e özel hiçbir çağrı**
** yok**; yalnızca S3 API. Nesne biçimi NDJSON + ZSTD, ~64 MB.
Anahtar: `raw/{owner_group}/{yyyy}/{MM}/{dd}/{hh}/{source_class}/{ulid}.ndjson.zst`
— `owner_group` yolun içinde, çünkü ham okuma da kapsam filtresinden geçecek.
2. **Manifest** (Postgres `raw_manifest`) — `object_key, sha256, byte_size,  event_count, ts_from, ts_to, uploaded_at, verified_at`. **Bu ticket'ın en değerli**
** parçası:** manifest olmadan "replay 7 gün yerine 5 gün döndü" fark edilmez.
3. **Yükleyici (uploader)** — WAL segmentini yükler, geri okuyup sha256 doğrular,
`verified_at` yazar. Doğrulanmadan segment silinmez.
4. **WAL kuyruk politikası** — segment, yüklendiği doğrulandıktan sonra **48 saat**
** daha** tutulur. RustFS bu pencerede veri kaybederse yerelden yeniden yüklenir.
5. **Periyodik scrub** — örneklenmiş nesneler indirilip sha256 manifest'e karşı
doğrulanır. Uyuşmazlık ve kayıp nesne sağlık uçlarında ve log'da görünür.
6. **Ham okuma yolu** — `event_id → raw_ref → nesne + offset`. Kapsam doğrulanmadan
indirme yok.

### Dışında

Replay'in kendisi (T11), sorgu ucu `/v1/events/{id}/raw` (T10 — bu ticket servisi
sağlar), lifecycle/tiering (v2).

## Kabul kriterleri

Ham nesneler RustFS'e yazılıyor, geri okunuyor, sha256 tutuyorScrub sha256 uyuşmazlığını yakalıyorDoğrulanmamış segment silinmiyor (test)

## Uygulama sonucu

| Parça | Nerede |
| --- | --- |
| S3 yazıcı | `src/Bizigo.Storage.Raw/IRawObjectStore.cs` — yalnızca S3 API |
| Nesne kurucu | `RawObjectBuilder.cs` — NDJSON + konumlar + ZSTD + sha256 |
| Yükleyici | `RawArchiveUploader.cs` — yükle → geri oku → doğrula → manifest |
| Scrub | `RawArchiveScrubber.cs` — en eski denetlenenden başlayarak örnekler |
| Okuma yolu | `RawReader.cs` — kapsam kontrolü **indirmeden önce** |
| WAL bağı | `src/Bizigo.Ingest/Wal/WalSegmentSource.cs` (yön: ingest → arşiv) |
| Envanter çözümü | `src/Bizigo.ControlPlane/SourceDirectory.cs` |

**Kapsam ayarlaması:** `owner_group` çözümlemesi biçimsel olarak T06'nın kalemi,
ama nesne anahtarının parçası olduğu ve anahtar bir kez yazılıp değişmediği için
yüklemeden **önce** olmak zorundaydı. `SourceDirectory` bu yüzden T04'te yazıldı;
T06 aynı bileşeni sıcak yolda kullanacak.

**Açık kalem — `raw_ref`.** Yükleyici her kaydın `object_key#offset:length`
değerini üretiyor ve `IRawRefSink` üzerinden dışarı veriyor, ama F1'de varsayılan
uygulama hiçbir şey yapmıyor. Sebep yapısal: ingest boru hattı ile yükleyici
bilinçli olarak bağımsız çalışıyor (F1 §2.3), yani olay satırı ClickHouse'a
yazılırken `raw_ref` henüz bilinmiyor. **T07'de karara bağlanmalı:** ayrı bir
ClickHouse indeks tablosu mu, yoksa manifest üzerinden (owner_group + ts aralığı)
arama mı.

**Doğrulama:** çözüm 13 projeyle 0 uyarı derleniyor; birim testleri (nesne
kurucu, okuma yolu, kapsam) ve entegrasyon testleri (gerçek RustFS + Postgres:
yazma/geri okuma, `_unassigned`, mükerrer yükleme, saklama süresi, scrub'ın
bozulma ve kayıp yakalaması) yazıldı. Testler yerelde koşturulmadı — makine yükü
nedeniyle doğrulama CI'a bırakıldı.

## Notlar

- Kaçış planı sırası: SeaweedFS (Apache 2.0) → kurumun mevcut S3 uyumlu depolaması →
Garage (AGPL — kullanılacaksa hukuki teyit). Yalnızca S3 API kısıtı bunun bedelini
bir config satırına indiriyor.
- MVP **tek düğüm** — RustFS dağıtık modu "under testing". Düğüm dayanıklılığı
RAID/ZFS. RustFS sürümü **tam sabitlenir**, beta serisinde sürüm takibi yapılmaz.
- İzlenecek kalem: RustFS 1.0 GA çıkınca yükseltme ve dağıtık modun yeniden
değerlendirilmesi.
