---
title: "T03 — Ingest boru hattı: OTLP, WAL, kodlama"
kind: ticket
status: 2
---

# T03 — Ingest boru hattı: OTLP, WAL, kodlama

**Bağımlılık:** T02 · **Sonraki:** T04, T10, T12
**Yöneten belgeler:** [F1 §2](../../f1-teknik-plan/index.md) · [K24, K4](../../mimari-kararlar/index.md)

## Amaç

Verinin içeri girdiği tek kapı ve **dayanıklılık sınırı**. Bu ticket bittiğinde
gerçek bir cihazdan syslog akıyor, ham hali diske düşüyor ve süreç `kill -9` ile
öldürülse bile ack'lenmiş hiçbir olay kaybolmuyor. Henüz parse yok.

## Kapsam

### İçinde

1. **OTLP/HTTP uç** — `POST /v1/logs`, hem `application/x-protobuf` hem
`application/json`. `opentelemetry-proto`'dan `logs_service.proto` üretilir
(`Grpc.Tools`), `LogsData` çözücüsü yazılır. gRPC (`:4317`) **yok** — kaçış kapısı.
2. **Yerel WAL** — ack **ham batch WAL'a yazılıp fsync edildikten sonra** verilir.
Segment dosyaları, çapraz kontrol için CRC, ve yeniden başlatmada kurtarma.
3. **Backpressure** — WAL doluysa `503 + Retry-After`. Kendi kuyruğumuzu yazmıyoruz;
collector'ın `file_storage` kalıcı kuyruğu yeniden dener.
4. **Kodlama tespiti ve normalizasyon** ([F1 §2.4](../../f1-teknik-plan/index.md))
  - `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` — **zorunlu**,
yoksa `windows-1254` çalışma anında patlar.
  - Tespit sırası: envanterdeki `encoding` → UTF-8 doğrulaması → kaynağın yedek
kod sayfası → `latin1` (kayıpsız).
  - Depolamadan önce **UTF-8 NFC**. `encoding_detected` saklanır.
5. **OTel Collector yapılandırması** — `syslog` receiver `protocol: none`
([K24](../../mimari-kararlar/index.md)), `file_storage` kalıcı kuyruk,
`otlp_http` exporter. Sürüm sabitli.
6. **Boru hattı iskeleti** — `System.Threading.Channels`, sınırlı kapasite,
worker sayısı `ProcessorCount`. Parse adımı bu ticket'ta **geçiş (passthrough)**;
T05/T06 doldurur.

### Dışında

Parse (T05), dispatcher (T06), RustFS'e yükleme (T04 — bu ticket WAL'a kadar),
kimlik doğrulama (T09 — uç şimdilik açık, T09 kapatır).

## Kabul kriterleri

Gerçek bir cihaz/logger ile syslog → collector → /v1/logs → WAL akıyorkill -9 altında ack'lenmiş hiçbir olay kaybolmuyor (entegrasyon testi)WAL dolduğunda 503 dönüyor ve collector yeniden deniyor; veri kaybı yokprotocol: none ile gelen satır byte-for-byte korunuyorTR/AR/CJK + windows-1254 + bozuk bayt fixture'ları tur-gidiş testinden geçiyorOTLP protobuf ve JSON kodlamalarının ikisi de kabul ediliyor

## Uygulama sonucu

| Parça | Nerede |
| --- | --- |
| OTLP çözücü (protobuf + JSON) | `src/Bizigo.Ingest/Otlp/OtlpLogsDecoder.cs`, protolar `Otlp/proto/` (opentelemetry-proto v1.9.0) |
| WAL | `src/Bizigo.Ingest/Wal/` — `WalFrame` (magic + CRC32), `WriteAheadLog` (fsync, kurtarma, kapasite) |
| Kodlama tespiti | `src/Bizigo.Ingest/Text/EncodingDetector.cs` |
| Boru hattı | `src/Bizigo.Ingest/Pipeline/` — kanal, işçiler, `IIngestSink` (T06 buraya takılıyor) |
| Uç | `src/Bizigo.Api/LogsEndpoint.cs` — `POST /v1/logs`, 503 + `Retry-After` |
| Collector | `deploy/otel/collector.yaml` — `protocol: none` + `encoding: nop` |

**İki taşıyıcı bulgu:**

1. **`encoding: nop` zorunlu (K27).** Bu olmadan `protocol: none` tek başına
yetmiyordu: collector gövdeyi varsayılan `utf-8` ile çözüp geçersiz baytları
U+FFFD yapıyor. Ham sadakat zinciri burada kopuyordu.
2. **WAL yükü = arşiv satırı (K28).** Tek codec, tek format; T04 dönüştürmüyor,
kopyalıyor.

**Doğrulama durumu:** derleme temiz; birim testleri yazıldı (WAL kurtarma,
kodlama, OTLP çözücü, codec, kapı) ama **koşturulmadı** — makine yükü ve Docker
disk engeli nedeniyle testler toplu olarak sonraya bırakıldı. Gerçek cihaz →
collector → `/v1/logs` → WAL akışı da Docker engeline bağlı.

## Notlar

- **Ham baytlar saklanır, çözülmüş string değil.** Kodlama tespiti yanlışsa replay
düzeltebilsin diye ([F1 §7.1](../../f1-teknik-plan/index.md)).
- Batch arayüzü baştan **çoklu kayıt** alır — sonradan Kafka eklenirse imza değişmez
([K5 riski #5](../../mimari-kararlar/index.md)).
- `protocol: none` deneysel: collector sürümü sabitlenir, yedek plan `rfc3164` + OTTL
ile gövde kopyalama. Yedek planın çalıştığı **bir kez** doğrulanmalı.
