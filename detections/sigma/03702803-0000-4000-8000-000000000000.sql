-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : FortiGate: kimlik doğrulama hatası
-- kimlik     : 03702803-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/fortigate_user_auth_fail.yml
-- kaynak sha : sha256:3a44f8c4cd3454e327eed1f1afd62262c75a1d195bfb0b90f3666b809d35f709
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : loganalyzer-events-ocsf/62691bd7a021 (sha256:62691bd7a0219f80b311b848748f23c6525598217838ec3cd5d2b807c0342c6f)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Fortinet' AND (class_uid=4001 AND (status='failure' AND actor_user_name ILIKE '%admin%'))
