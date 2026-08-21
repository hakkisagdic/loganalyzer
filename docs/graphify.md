# Graphify — depo bilgi grafiği

Kod tabanını **sorgulanabilir bir grafa** çeviren yerel araç. Çıktı
`graphify-out/` altında ve depoda duruyor.

## Neden bu depoda işe yarıyor

Bu depoda beş ajan paralel çalışıyor ve her biri kodun bir bölgesini biliyor.
Kesişimleri gören tek şey koordinatör — ve o da grep'le görüyor. Graf, "bu
sınıfa kim dokunuyor" sorusunun cevabını **dosya taramadan** veriyor.

Ölçülen ilk koşum (`b904be4b`):

| | |
| --- | --- |
| Dosya | 724 (~546 bin kelime) |
| Düğüm / kenar | 8.280 / 18.892 |
| Topluluk | 429 |
| Çıkarım | %95 `EXTRACTED` · %5 `INFERRED` (862 kenar, ort. güven 0,81) |
| **Token maliyeti** | **0 girdi · 0 çıktı** |

Son satır aracın asıl iddiası: **kod grafı LLM'siz üretiliyor.** Ayrıştırma
tree-sitter AST'siyle deterministik; LLM yalnızca PDF/doküman gibi kod olmayan
girdilerin semantik çıkarımında ve topluluk adlandırmada devreye giriyor.
Vektör store yok — gerçek graf gezinmesi var, gömme benzerliği değil.

`EXTRACTED` / `INFERRED` ayrımı bu depo için özellikle anlamlı: **tahmin ile
olgu ayrı etiketlerde duruyor.** Bir kenarın "çözümlenmiş" olduğunu bilmek,
onu olguymuş gibi okumayı engelliyor.

## Kurulum

```bash
uv tool install graphifyy
graphify install --project     # skill + CLAUDE.md bölümü + PreToolUse kancası
graphify hook install          # post-commit/post-checkout + graph.json merge sürücüsü
```

`graphify hook install` iki şey yapıyor:

- `.githooks/post-commit` ve `post-checkout` — commit sonrası grafı **arka
  planda** yeniden derliyor (ayrık proses, `GRAPHIFY_REBUILD_TIMEOUT`
  varsayılan 600 sn).
- `graph.json` için bir **git merge sürücüsü** kaydediyor (birleşim merge'i).
  Bu depoda paralel ajanlar aynı grafı yeniden ürettiği için gerekli:
  `CLAUDE.md` §5 üretilen dosyaların elle birleştirilmemesini söylüyor ve
  sürücü tam olarak bunu sağlıyor.

> **Merge sürücüsü kopya başına kaydediliyor**, commitlenmiyor
> (`.gitattributes` eşlemesi commitli ama `merge.graphify.driver` yerel git
> yapılandırmasında). Yeni klonda `graphify hook install` çalıştırılmalı;
> çalıştırılmazsa git eşlemeyi bulur ama sürücüyü bulamaz ve normal metin
> merge'ine düşer.

## Kullanım

```bash
graphify query "kapsam filtresi nerede uygulanıyor"
graphify path "AccessScopeResolver" "EventsController"
graphify explain "SecretProtector"
graphify update .                # kod değişince (AST-only, API maliyeti yok)
```

Claude Code içinden `/graphify` de aynı boruyu çalıştırıyor.

## Depoda ne duruyor

| Dosya | Boyut | Ne |
| --- | --- | --- |
| `graphify-out/graph.json` | ~12 MB | Sorgulanabilir graf — `query`/`path`/`explain` bunu okuyor |
| `graphify-out/graph.html` | ~464 KB | Kuvvet yönelimli interaktif görselleştirme |
| `graphify-out/GRAPH_REPORT.md` | ~100 KB | İnsan okuru: hub'lar, topluluklar, çıkarım denetimi |
| `graphify-out/manifest.json` | ~140 KB | Dosya → düğüm izi |
| `graphify-out/cache/` | ~16 MB | **Commitlenmiyor** — yalnızca yeniden derleme hızlandırıcı |

`cache/` kök `.gitignore`'da tek satırla dışlandı; geniş bir `graphify-out/*`
deseni kullanılmadı çünkü o desen yarın eklenecek bir çıktıyı sessizce yutardı
(aynı `.gitignore` dosyasındaki `**/wal/` olayının dersi).

## Bilinen maliyet: her commit'te yeniden derleme

`post-commit` kancası grafı arka planda yeniden derliyor. Tek başına ucuz
(AST-only, 697 dosya, 8 işçi) ama bu makine 16 GB ve üstünde paralel ajanlar
commit atıyor — `CLAUDE.md` §3'ün saydığı toplam görünmezliği tam olarak bu.

Açık seçenekler:

1. Kancayı `machine-resources.sh check` çağırmaya zorlamak; çıkış kodu 1 ise
   **derlemeyi atlayıp atladığını yazması**. Sessiz atlama değil, "yer yoktu,
   derlemedim" demesi. Global kuralların "ağır işten önce check" maddesinin
   kanca karşılığı bu.
2. `GRAPHIFY_REBUILD_TIMEOUT`'u düşürmek.
3. `graphify hook uninstall` ile kancayı hiç kullanmayıp grafı faz sonunda elle
   tazelemek.

Bu karar henüz verilmedi.
