-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : Cisco ASA: RST ile kapanan oturum
-- kimlik     : 07883503-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/asa_teardown_rst.yml
-- kaynak sha : sha256:adc6ec5597843539ba01b7a1eddfd7d71f9701aa4c325805f5c0beffc5ccbb4e
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND (class_uid=4001 AND raw_data ILIKE '%RST%')
