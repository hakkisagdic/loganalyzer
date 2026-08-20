-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : Cisco ASA: ICMP sağanağı
-- kimlik     : 25520785-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/asa_icmp_flood.yml
-- kaynak sha : sha256:69074f0cfbef67f7c6e28e3cd5e421f61d6877147a674fa10b528e9e14d6c904
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND (class_uid=4001 AND connection_info_protocol_name='icmp')
