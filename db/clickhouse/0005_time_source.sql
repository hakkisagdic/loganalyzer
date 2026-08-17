-- Olayın `ts` değerinin nereden geldiğini kaydeder.
--
-- Boru hattı zaman damgasını sırayla deniyor: parser'ın çözdüğü değer, yoksa
-- collector'ın gözlem zamanı, yoksa bizim aldığımız an. Üçü de `ts` kolonuna
-- yazılıyordu ve hangisi olduğu **hiçbir yerde durmuyordu**.
--
-- Bunun bedeli RCA'da çıkıyor: gözlem zamanına düşmüş bir olayın gerçek zamanı
-- dakikalarca önce olabilir, dolayısıyla korelasyon penceresi kayar ve rapor
-- yanlış kanıtla kurulur. Ağ cihazlarında zaman damgasız satır nadir değil —
-- Cisco ASA'nın PRI taşıyıp tarih taşımayan satırları katalogda örnekli.
--
-- Kolon olarak eklendi, `attrs` içine değil: "yalnızca güvenilir zamanlı
-- olaylar" filtresi RCA'nın sık sorusu ve Map araması kolon kadar ucuz değil.
--
-- Geçmiş satırlar için varsayılan boş kalıyor. Kasıtlı: 'parsed' demek onları
-- olduklarından güvenilir göstermek, 'received' demek olmadıkları kadar
-- şüpheli göstermek olurdu. Boş = "bilinmiyor, bu kolondan önce yazıldı".
ALTER TABLE events
    ADD COLUMN IF NOT EXISTS time_source LowCardinality(String) DEFAULT ''
    AFTER ingested_at;
