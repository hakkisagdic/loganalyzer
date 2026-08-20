-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : FortiGate: WAN'dan yönetim portuna erişim
-- kimlik     : 52776632-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/fortigate_admin_from_wan.yml
-- kaynak sha : sha256:f052b62beb91c442837c1184f0f965dd90ff0ae750c5ad808a2b099a12ff86dd
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Fortinet' AND (class_uid=4001 AND ((dst_endpoint_port IN (22, 443)) AND replaceRegexpOne(toString(src_endpoint_ip), '^::ffff:', '') ILIKE '203.0.113.%'))
