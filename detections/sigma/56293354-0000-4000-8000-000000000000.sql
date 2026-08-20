-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : nginx: yönetim yoluna istek
-- kimlik     : 56293354-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/nginx_admin_path.yml
-- kaynak sha : sha256:7dd3fb47f38dcd39f645e7c09417cac92d19760d6f692b3973875bf7d0bc4b4f
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE metadata_product_name='nginx' AND (class_uid=4001 AND unmapped['otel.url.path'] ILIKE '/admin%')
