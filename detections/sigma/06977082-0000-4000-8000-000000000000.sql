-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : nginx: tarayıcı user-agent
-- kimlik     : 06977082-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/nginx_scanner_agent.yml
-- kaynak sha : sha256:e30aa8a3baa78b8f2a033b92e5227cf11c91f4fa6f68cbd0c327e72df59e88e7
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE metadata_product_name='nginx' AND (class_uid=4001 AND (unmapped['otel.user_agent.original'] ILIKE '%sqlmap%' OR unmapped['otel.user_agent.original'] ILIKE '%nikto%'))
