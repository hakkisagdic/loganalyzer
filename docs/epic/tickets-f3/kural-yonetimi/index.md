---
title: "T33 — Kural yönetimi ve alarm motoruna bağlama"
kind: ticket
status: 0
---

# T33 — Kural yönetimi ve alarm motoruna bağlama

**Bağımlılık:** T32 · **Sonraki:** T38

## Amaç

Derlenmiş Sigma SQL'inin **çalışması** ve yönetilebilmesi.

## Kapsam

### İçinde

- Kural kaydı: kimlik, kaynak sürümü, etkin/pasif, kapsam, gürültü ayarı.
- **F2'nin alarm motoruna bağlama.** Ayrı bir çalıştırıcı yazılmıyor: eşik/oran/
sessizlik değerlendiricisi zaten bir sorgu koşturup sonucu eşikle
karşılaştırıyor; Sigma kuralı da bir sorgu.
- Kural yönetim ekranı — F2'nin alarm ekranına eklenen bir sekme, ayrı ürün
yüzeyi değil.
- Toplu etkinleştirme: 269 kuralı tek tek açmak kimse yapmaz.

### Dışında

- Kural yazma/düzenleme. Sigma kuralları yukarı akıştan geliyor; bizde yazılmıyor.
- Sigma korelasyon kuralları — backend destekliyor ama önce tekiller otursun.

## Kabul kriterleri

- Bir Sigma kuralı etkinleştirildiğinde tetikleniyor ve bildirim gidiyor.
- Kural **sahibinin kapsamıyla** koşuyor; başka grubun verisini görmüyor.
- Pasif kural hiç sorgu üretmiyor — kapalı kuralın maliyeti sıfır olmalı.
- Eşzamanlılık limiti Sigma kurallarını da kapsıyor: 269 kural açıksa
ClickHouse'a atılan eşzamanlı sorgu sayısı sınırlı.
- Kaynak kural sürümü değiştiğinde kullanıcı bunu görüyor.

## Notlar

Gürültü F3'ün en büyük ürün riski: Sigma kuralları ağ evreninde yazılmadı ve
yanlış pozitif üretecekler. F2'nin alarm önizlemesi (T23) burada da geçerli —
kural açılmadan önce "son 24 saatte kaç kez tetiklenirdi" gösterilmeli.
