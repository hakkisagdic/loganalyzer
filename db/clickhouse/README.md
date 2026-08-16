# ClickHouse göçleri

Dosya adı: `NNNN_kisa_ad.sql` — sıralı, sıfır dolgulu.
`schema_migrations` tablosu uygulanmışları izler; **uygulanmış bir dosya
değiştirilirse göç hata verir** (sürüklenme tespiti). Değişiklik yeni dosyayla yapılır.

Şema içeriği T02'de geliyor (F1 teknik plan §6).

Çalıştırma:

    dotnet run --project src/Bizigo.Cli -- schema migrate db/clickhouse
