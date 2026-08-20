-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : nginx: büyük yükleme
-- kimlik     : 81341084-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/nginx_large_upload.yml
-- kaynak sha : sha256:d0fed53fbf57cd316d07f941e2abf653632248bf252e2ea29749a62d32e361ac
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE metadata_product_name='nginx' AND (class_uid=4001 AND (activity_name='POST' AND unmapped['otel.url.path'] ILIKE '%/upload%'))
