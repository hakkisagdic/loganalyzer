# Grok pattern kütüphanesi

Bu dizin **veri**dir, kod değil (T05 / F1 §4.1). İçeriği
[logstash-plugins/logstash-patterns-core](https://github.com/logstash-plugins/logstash-patterns-core)
**v4.3.4** sürümünden olduğu gibi alınmıştır. Lisans: Apache-2.0 (`LICENSE`).

| Dizin | Ne zaman |
| --- | --- |
| `legacy/` | **Varsayılan.** Yakalanan alan adları nötr (`clientip`, `logsource`). Bizim `map` bloğumuz alan adlarını zaten kendisi belirlediği için istediğimiz bu. |
| `ecs-v1/` | Aynı pattern'lerin ECS alan adlı (`[source][ip]`) sürümü. Yüklenir ama varsayılan değildir; ECS adlarıyla doğrudan çalışmak isteyen parser'lar için. |

## Yükseltme

Dosyalar elle **düzenlenmez**. Yeni sürüm gerektiğinde upstream'den yeniden
kopyalanır ve bu README'deki sürüm numarası güncellenir. Motor tarafındaki
söz dizimi farkları (`\h`, `[[:alnum:]]` gibi Oniguruma/POSIX yapıları)
`OnigurumaTranslator` içinde çevrilir — pattern dosyasına dokunulmaz. Böylece
upstream'i takip etmek `cp -R`'den ibaret kalır.

`ParserPatternLibraryTests` her iki setin de **tamamının** derlendiğini
doğrular; yükseltme bir pattern'i bozarsa CI'da görülür.
