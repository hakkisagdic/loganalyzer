-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : MikroTik: yeni forward bağlantısı
-- kimlik     : 33839694-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/routeros_forward_new.yml
-- kaynak sha : sha256:d5d43add482493c7a3943b55d509679967b11555ab9076a7ba4583491550eeea
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='MikroTik' AND (class_uid=4001 AND (connection_info_protocol_name='tcp' AND activity_name='forward'))
