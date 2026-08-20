-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : FortiGate: kimlik doğrulama hatası
-- kimlik     : 03702803-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/fortigate_user_auth_fail.yml
-- kaynak sha : sha256:d22bf875d663afd7eb443c12b2d892b641cb5ff3257a63242d172adb58bf68ed
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Fortinet' AND (class_uid=4001 AND (status='failure' AND actor_user_name ILIKE '%admin%'))
