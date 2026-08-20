-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : Cisco ASA: dışa açılan bağlantı
-- kimlik     : 58613739-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/asa_outbound_built.yml
-- kaynak sha : sha256:f9c1e86ce58620045a4ffe8d34b69f746cfaa18b0875518a12612790f9cf6287
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND (class_uid=4001 AND (activity_name='Built' AND connection_info_protocol_name='tcp'))
