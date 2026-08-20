-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : Cisco ASA: içeri reddedilen paket
-- kimlik     : 21934394-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/asa_deny_inbound.yml
-- kaynak sha : sha256:02d0ff433f966396e901ef13204783c31f23c74c5c0d4b597715fd6fe8673cb2
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND (class_uid=4001 AND (activity_name='Deny' AND dst_endpoint_port=445))
