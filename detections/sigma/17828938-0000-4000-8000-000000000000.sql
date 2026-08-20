-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : FortiGate: reddedilen bağlantı sağanağı
-- kimlik     : 17828938-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/fortigate_denied_burst.yml
-- kaynak sha : sha256:6f11b2771471d30de9d51874198d65ab49b8c2ffeb097686ea846210b1360b7e
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Fortinet' AND (class_uid=4001 AND (activity_name='blocked' AND dst_endpoint_port=443))
