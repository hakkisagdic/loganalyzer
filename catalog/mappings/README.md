# Eşleme tabloları

`map` bloğundaki `{ from: <alan>, table: <tablo> }` ifadelerinin çözüldüğü yer.
Dosya adı tablo adıdır: `ocsf_network_activity.yaml` → `table: ocsf_network_activity`.

Tablolar **veri**dir (F1 §5): "türetme kuralları YAML `map` bloğunda ve merkezî
eşleme tablolarında durur — kodda değil". Motor bir tabloyu tanımıyorsa bu bir
**şema hatasıdır**, çalışma anı sürprizi değil: `ParserCompiler` bilinmeyen tabloyu
`parser lint` sırasında bildirir.

Arama **ordinal**dir; büyük/küçük harf normalizasyonu yapılmaz. Sebebi F1 §2.4:
`tr-TR` kültüründe `ToLower()` `I → ı` yapar ve eşleme sessizce ıskalar. Cihazın
bastığı değer neyse tabloda o yazar.

Bu dizindeki içeriğin **sahibi T07**'dir (normalizasyon). T05 yalnızca mekanizmayı
kurar; buradaki dosyalar tam katalog değil, mekanizmanın çalıştığını gösteren
başlangıç setidir.
