-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : Cisco ASA: VPN oturum açma
-- kimlik     : 14477452-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/asa_vpn_login.yml
-- kaynak sha : sha256:3bd9645df58dc8b1effa5fe4e6200445ec1c6be39bb555b927433d224973dee9
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND (class_uid=4001 AND (raw_data ILIKE '%Group User%' AND actor_user_name ILIKE '%@%'))
