-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : nginx: 5xx sağanağı
-- kimlik     : 21011482-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/nginx_5xx_burst.yml
-- kaynak sha : sha256:98c544d16cb0cbdfc1cfd04f876d49eef6d0e4dc92030ce936721d85bd9c07af
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE metadata_product_name='nginx' AND (class_uid=4001 AND status ILIKE '5%')
