-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : MikroTik: başarısız giriş
-- kimlik     : 73633522-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/routeros_login_failure.yml
-- kaynak sha : sha256:91f92732f0c777c94ffba436865c9240db06806cd5ca4048f49f4a88aaa009ba
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='MikroTik' AND (class_uid=4001 AND raw_data ILIKE '%login failure%')
