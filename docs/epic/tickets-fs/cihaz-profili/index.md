---
title: "S01 — Cihaz profili şeması ve N1 sahte taşıyıcı"
kind: ticket
status: 2
---

# S01 — Cihaz profili şeması + N1 sahte taşıyıcı

**Bağımlılık:** — · **Sonraki:** S02, S03, S07

## Amaç

Bir simüle cihazın **tek kaynağı**: profil YAML'ı. Vendor, model, komut → çıktı
eşlemesi, syslog örneklerinin nereden okunacağı, ve senaryo geçişleri hep orada.

Üç sadakat seviyesinin (N1 sahte taşıyıcı, N2 SSH sunucusu, N3 CLI öykünmesi)
**hepsi aynı profili okuyor.** Ayrı profiller tutmak, üç seviyenin sessizce
ayrışması demekti — ve o ayrışmayı hiçbir şey söylemezdi.

## Kapsam

### İçinde

- Profil YAML şeması + doğrulayıcı.
- `IDeviceTransport`'un N1 uygulaması: profilden okuyup cevap veren sahte
  taşıyıcı.
- Profiller örnek satırları **kopyalamıyor, işaret ediyor**
  (`catalog/parsers/<id>/samples/`). Kopyalasaydı aynı satırın iki kaynağı
  olurdu ve biri değiştiğinde diğeri sessizce eskirdi.

### Dışında

- Gerçek ağ dinleyen hiçbir şey — o S02 ve S03.

## Kabul kriterleri

- Bir profil bozuksa **derleme/lint kırmızı yanıyor**.
- `DeviceConfigTests` profil okuyarak koşuyor.
- İşaret edilen örnek dosyanın varlığını bir bekçi sınıyor.

## Durum

**Kapandı.** `catalog/simulators/profiller/` altında dört profil
(`asa-dc-01`, `fw-ankara-01`, `fw-izmir-01`, `rb-sube-07`),
`sim/Bizigo.Simulators/` altında `SimulatorProfile`, `SimulatorProfileStore`,
`SimulatedDeviceTransport`.
