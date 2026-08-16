# Analiz sidecar'ı — Drain3 + pySigma

Bilinmeyen format keşfinin altyapısı (T12 / K14, [F1 §9](../README.md)).

> **Taşıyıcı kısıt: bu servis sıcak yolda DEĞİL.**
> Ölürse ingest çalışmaya devam eder; yalnızca format keşfi devre dışı kalır.
> Bakımsız bir kütüphaneye (Drain3) bağlanmayı kabul edilebilir kılan tek şey
> bu. .NET tarafı buraya sert bağımlılık kurmaz — sınırlı kuyruk, devre kesici,
> 2 sn zaman aşımı (`src/Bizigo.Ingest/Discovery/`).

## Uçlar

| Uç | Ne yapar |
| --- | --- |
| `POST /v1/mine/batch` | Öğrenerek eşleştirir (`add_log_message`). Keşif turu. |
| `POST /v1/mine/match` | Öğrenmeden eşleştirir. Yeni küme yaratmaz. |
| `GET /v1/clusters/{source_key}` | Şablonlar + sayaçlar + şablonda geçen mask adları. |
| `POST /v1/sigma/compile` | Sigma kuralı → ClickHouse SQL (F3 önizleme akışı). |
| `GET /healthz` | Ayakta mı + sözleşme/maske sürümü. |
| `GET /readyz` | Redis durumu, yüklü miner sayısı, sınırlar, sigma backend. |

`/v1/mine/*` yanıtındaki **`masked`** alanı sözleşmede yok, bilinçli bir ek:
.NET tarafı aynı maske dosyasıyla imzayı yerel olarak da hesaplıyor ve ikisini
karşılaştırıyor. Ayrışma `signature_drift` sayacına yazılıyor — ayrışmışsa
`template_id`'lere güvenilemez.

## Sürüm sabitleme — okumadan `pip install` etmeyin

`pip install drain3` **kullanılmaz.** PyPI'daki son sürüm 0.9.11 ve Temmuz
2022 tarihli. Depo `IBM/Drain3` → `logpai/Drain3` taşındı ve orada Şubat
2025'e kadar typing + modern Python düzeltmeleri aldı; hiçbiri yayımlanmadı.

```
drain3 @ git+https://github.com/logpai/Drain3@0fb6d6e1828838fa05c8f6fa5cda878e02ea79c7
```

`0fb6d6e` = 2025-02-04, `master` HEAD. Upstream durgun; düzeltme beklenmiyor.
Kod küçük (~28 KB) ve MIT — gerekirse fork edilir.

## Maskeleme sözlüğü tek kaynaktır

`catalog/masks/bizigo-masks.yaml` **iki tarafça birden** okunuyor:

```
catalog/masks/bizigo-masks.yaml
        │
        ├─► sidecar/app/masks.py         → Drain3 MaskingInstruction
        └─► src/Bizigo.Parsing/Grok/     → MaskCatalog (imza + grok köprüsü)
              MaskCatalog.cs
```

Maske adı bir **grok pattern adı** olmak zorunda (`IPV4`, `MAC`, `UUID`, …).
Böylece mined şablondaki `<IPV4>` doğrudan `%{IPV4:...}` grok taslağına
dönüşüyor — F4'teki format keşfi senaryosunun çıktı kalitesi buradan geliyor.

Dosyanın `golden` bölümü çapraz dil güvencesi: aynı örnekler hem
`sidecar/tests/test_masks.py` hem `MaskCatalogTests` içinde koşuyor. Bir regex
iki motorda farklı davranırsa CI'da görülür.

> İmaj **repo kökünden** build edilir, çünkü `sidecar/` ve `catalog/masks/`
> dizinlerinin ikisi de gerekiyor:
> ```yaml
> build:
>   context: ..
>   dockerfile: sidecar/Dockerfile
> ```

## Ayarlar (ortam değişkeni)

| Değişken | Varsayılan | Not |
| --- | --- | --- |
| `REDIS_URL` | `redis://localhost:6379/0` | Yoksa bellek içi devam eder, `/readyz` `degraded` der. |
| `BIZIGO_MASKS_PATH` | `/app/masks/bizigo-masks.yaml` | Yoksa **açılış patlar** — sessiz kalması, keşfin neden çalışmadığını gizlerdi. |
| `DRAIN_MAX_CLUSTERS` | `5000` | **Zorunlu, pozitif.** Sınırsız küme ağ loglarında bellek sızıntısı gibi davranıyor (K14). |
| `DRAIN_SIM_TH` | `0.4` | |
| `DRAIN_DEPTH` | `4` | |
| `DRAIN_MAX_CHILDREN` | `100` | |
| `DRAIN_SNAPSHOT_INTERVAL_MINUTES` | `5` | |
| `SIDECAR_MAX_MINERS` | `64` | Bellekte tutulan kaynak sınıfı sayısı; LRU. Tahliye kayıp değil, durum Redis'te. |
| `SIDECAR_MAX_BATCH` | `500` | Aşan istek 413. |
| `SIGMA_TABLE` | `events` | Backend varsayılanı `logs`; şemamız `events`. |
| `SIGMA_FULL_LOG_COLUMN` | `body` | Backend varsayılanı `full_log`. |

## Geliştirme

```bash
uv venv --python 3.13 .venv
uv pip install --python .venv/bin/python -r requirements-dev.txt
.venv/bin/python -m pytest

# Elle koşturmak (Redis olmadan da açılır):
BIZIGO_MASKS_PATH=../catalog/masks/bizigo-masks.yaml \
  .venv/bin/python -m uvicorn app.main:app --port 8099
```

Testler Redis'e **bağlanamayan** bir URL ile koşuyor; böylece "kalıcılık
olmadan da çalış" yolu her koşuda sınanmış oluyor.
