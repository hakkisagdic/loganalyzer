-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : MikroTik: Winbox erişimi
-- kimlik     : 07131427-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/routeros_winbox_access.yml
-- kaynak sha : sha256:efada6f9199ca8e0242e9a779f9ae02a219e825f4f8019a2b831f407241d69c9
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='MikroTik' AND (class_uid=4001 AND dst_endpoint_port=8291)
