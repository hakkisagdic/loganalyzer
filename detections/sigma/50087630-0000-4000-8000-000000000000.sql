-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : MikroTik: DHCP teklifi
-- kimlik     : 50087630-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/routeros_dhcp_offer.yml
-- kaynak sha : sha256:960ecd6660ac2d764e7023576c9f721dd6bb35d15473ed27958e82fbd0b81684
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='MikroTik' AND (class_uid=4001 AND (connection_info_protocol_name='udp' AND dst_endpoint_port=68))
