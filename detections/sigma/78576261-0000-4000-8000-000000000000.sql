-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.
-- Sigma kuralından derleme zamanında üretildi (T32).
--
-- kural      : MikroTik: input zincirinde düşürme
-- kimlik     : 78576261-0000-4000-8000-000000000000
-- kaynak     : catalog/sigma/rules/routeros_drop_input.yml
-- kaynak sha : sha256:436a6f0a59dba9b1de31ec2e6cb0951c2bc3051d5749072e9fc1ba88f10cdebd
-- kural seti : t30-ornekleminden-terfi
-- pipeline   : bizigo-events-ocsf/ae264764362f (sha256:ae264764362fecd31fec7b2043a6f3a595063b95f497e0c73e02c50b98e3f718)
--
-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.
-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`

SELECT * FROM events_ocsf WHERE device_vendor_name='MikroTik' AND (class_uid=4001 AND (activity_name='drop' AND dst_endpoint_port=8291))
