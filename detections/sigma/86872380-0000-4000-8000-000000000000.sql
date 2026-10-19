-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : nginx: SQL enjeksiyon denemesi
-- kimlik     : 86872380-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/nginx_sqli_probe.yml
-- kaynak sha : sha256:c672b2ca247dd6b7d8ef7185d2d9df0f79da85a7c17355127c2497fe02b19a27
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : loganalyzer-events-ocsf/62691bd7a021 (sha256:62691bd7a0219f80b311b848748f23c6525598217838ec3cd5d2b807c0342c6f)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE metadata_product_name='nginx' AND (class_uid=4001 AND (unmapped['otel.url.path'] ILIKE '%UNION SELECT%' OR unmapped['otel.url.path'] ILIKE '%'' OR ''1''=''1%'))
