-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : MikroTik: yeni forward zincirinde TCP bağlantısı
-- kimlik     : 33839694-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/routeros_forward_new.yml
-- kaynak sha : sha256:91a9ba46ba5a00ddda6b0cfbea03d81193c78f64f0711aba4aa047d497015dc7
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/fa56f2121e9b (sha256:fa56f2121e9bf35752eb2c65d4a68055f8e818f940100a42f1c1796c71ea46a5)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='MikroTik' AND (class_uid=4001 AND (connection_info_protocol_name='tcp' AND unmapped['fw_chain']='forward'))
