-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : FortiGate: yüksek porta tarama
-- kimlik     : 34775915-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/fortigate_high_port_scan.yml
-- kaynak sha : sha256:f4b0a995a26509f220d09c7d438da37da82f40b11e8d7a8b2a37fa235b9c5878
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Fortinet' AND (class_uid=4001 AND (connection_info_protocol_name='tcp' AND dst_endpoint_port >= 30000))
