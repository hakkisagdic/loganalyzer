-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : Cisco ASA: ACL eşleşmesi
-- kimlik     : 19506570-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/asa_acl_hit.yml
-- kaynak sha : sha256:3e01ad0e6b8bf50389c7c36190e27762980674d34d252cd87f51e0b8afc512a0
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND (class_uid=4001 AND (raw_data ILIKE '%access-list%' AND activity_name='denied'))
