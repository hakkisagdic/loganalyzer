-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : nginx: şüpheli Host başlığı (DNS rebinding)
-- kimlik     : 14644215-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/nginx_dns_rebind.yml
-- kaynak sha : sha256:706a5f29ef35b03941f6085541d187f56120ff6658ac69d62980ef0a7acdbdaa
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE metadata_product_name='nginx' AND (class_uid=4002 AND (device_hostname ILIKE '%localhost%' OR device_hostname ILIKE '%127.0.0.1%' OR device_hostname ILIKE '%169.254.169.254%'))
