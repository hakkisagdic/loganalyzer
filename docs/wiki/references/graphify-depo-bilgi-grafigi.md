---
title: Graphify — depo bilgi grafiği
category: references
tags: [arac, mimari, kaynak-ozeti, bizigo]
aliases: [graphify]
relationships:
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: uses
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: related_to
sources: [docs/graphify.md, README.md]
source_digest: "sha256-12/v1 README.md=dc51f8de9a8c docs/graphify.md=5cc01d2e969c"
summary: docs/graphify.md özeti — kod grafını tree-sitter AST'siyle LLM'siz üreten yerel araç; 8.280 düğüm, 18.892 kenar, sıfır token maliyeti.
provenance:
  extracted: 0.9
  inferred: 0.1
  ambiguous: 0.0
base_confidence: 0.71
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: supporting
created: 2026-08-24T16:15:22Z
updated: 2026-08-24T16:15:22Z
---

# Graphify — depo bilgi grafiği

`docs/graphify.md` belgesinin özeti. Kod tabanını **sorgulanabilir bir grafa**
çeviren yerel araç; çıktısı `graphify-out/` altında ve depoda duruyor.

## Neden bu depoda işe yarıyor

Bu depoda beş ajan paralel çalışıyor ve her biri kodun bir bölgesini biliyor
(bkz. [[skills/paralel-ajan-koordinasyonu]]). Kesişimleri gören tek şey
koordinatör — ve o da grep'le görüyor. Graf, "bu sınıfa kim dokunuyor"
sorusunun cevabını **dosya taramadan** veriyor.

## Ölçülen ilk koşum (`b904be4b`)

| | |
| --- | --- |
| Dosya | 724 (~546 bin kelime) |
| Düğüm / kenar | 8.280 / 18.892 |
| Topluluk | 429 |
| Çıkarım | %95 `EXTRACTED` · %5 `INFERRED` (862 kenar, ort. güven 0,81) |
| **Token maliyeti** | **0 girdi · 0 çıktı** |

Son satır aracın asıl iddiası: **kod grafı LLM'siz üretiliyor.** Ayrıştırma
tree-sitter AST'siyle deterministik; LLM yalnızca kod olmayan girdilerin
semantik çıkarımında ve topluluk adlandırmada devreye giriyor. Vektör store yok
— gerçek graf gezinmesi var, gömme benzerliği değil.

`EXTRACTED` / `INFERRED` ayrımı bu depo için özellikle anlamlı: **tahmin ile
olgu ayrı etiketlerde duruyor.** Aynı ayrımı bu vault da `^[inferred]`
işaretçisiyle yapıyor — ikisi de aynı ihtiyaca cevap veriyor, bir kenarın
"çözümlenmiş" olduğunu bilmek onu olguymuş gibi okumayı engelliyor. ^[inferred]

## Kullanım

```bash
graphify query "kapsam filtresi nerede uygulanıyor"
graphify path "AccessScopeResolver" "EventsController"
graphify explain "SecretProtector"
graphify update .                # kod değişince (AST-only, API maliyeti yok)
```

## Merge sürücüsü — kopya başına

`graphify hook install`, `graph.json` için bir **git merge sürücüsü**
(birleşim merge'i) kaydediyor. Paralel ajanlar aynı grafı yeniden ürettiği için
gerekli; `CLAUDE.md` §5'in "üretilen dosyalar elle birleştirilmez" maddesinin
araç karşılığı.

> Sürücü **kopya başına kaydediliyor**, commitlenmiyor. `.gitattributes`
> eşlemesi commitli ama `merge.graphify.driver` yerel git yapılandırmasında.
> Yeni klonda çalıştırılmazsa git eşlemeyi bulur, sürücüyü bulamaz ve normal
> metin merge'ine düşer.

Bu tam olarak [[concepts/sessiz-yanlis-davranis]] şekli: kurulum eksikse hata
yok, uyarı yok, sadece yanlış merge. ^[inferred]

## Açık karar: her commit'te yeniden derleme

`post-commit` kancası grafı arka planda yeniden derliyor. Tek başına ucuz ama
makine 16 GB ve üstünde paralel ajanlar commit atıyor. Belge üç seçenek sayıyor
(kancayı `machine-resources.sh check`'e bağlamak, `GRAPHIFY_REBUILD_TIMEOUT`'u
düşürmek, kancayı hiç kullanmamak) ve açıkça yazıyor:

> Bu karar henüz verilmedi.

## Kaynaklar

- `docs/graphify.md` — kurulum, ölçüm, merge sürücüsü, bilinen maliyet
- `README.md` — "Bilgi grafiği (Graphify)" bölümü
- `docs/graphify-kullanim.md` — genişletilmiş kullanım (bu özete girmedi)
