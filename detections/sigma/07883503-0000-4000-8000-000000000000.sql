-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : Cisco ASA: RST ile kapanan oturum
-- kimlik     : 07883503-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/asa_teardown_rst.yml
-- kaynak sha : sha256:a494c1150e9299234c656d424910472c731f7297be3c00a87c9fc56482e06053
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : loganalyzer-events-ocsf/62691bd7a021 (sha256:62691bd7a0219f80b311b848748f23c6525598217838ec3cd5d2b807c0342c6f)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND (class_uid=4001 AND (raw_data ILIKE '%Teardown%' AND raw_data ILIKE '%Reset%'))
