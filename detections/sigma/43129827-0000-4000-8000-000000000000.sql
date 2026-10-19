-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : FortiGate: yasaklı kategori
-- kimlik     : 43129827-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/fortigate_blocked_category.yml
-- kaynak sha : sha256:0084052ff19e61a2b39cae89addaf2b30412b10ab9a099c8b3e3230c42893481
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : loganalyzer-events-ocsf/62691bd7a021 (sha256:62691bd7a0219f80b311b848748f23c6525598217838ec3cd5d2b807c0342c6f)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Fortinet' AND (class_uid=4001 AND (activity_name='blocked' AND unmapped['otel.url.path'] ILIKE '%/config/%'))
