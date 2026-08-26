---
title: "S04 — Senaryo motoru"
kind: ticket
status: 2
---

# S04 — Senaryo motoru

**Bağımlılık:** S02, S03 · **Sonraki:** S05

## Amaç

Adlandırılmış geçişler: bir cihaz "normal"den "config değişti"ye, "susmaya",
"saldırı altında"ya geçebilsin — **hem SSH hem syslog tarafında aynı anda**.

İki tarafın ayrı ayrı senaryo taşıması, ikisinin sessizce ayrışması demekti:
config'i değişmiş görünen bir cihaz syslog'da hiçbir şey söylemezse ürünün
korelasyonu test edilemez.

## Kapsam

### İçinde

- FS §7'deki yedi senaryo, adlandırılmış geçişlerle.
- Geçiş **her iki taşıyıcıda birden** etkili.
- Varsayılan **statik**; değişim açıkça seçiliyor. Rastgele senaryo yok —
  tekrarlanamayan bir simülatör, ölçemeyeceğimiz bir simülatördür.

### Dışında

- Filo ve kapsam yayılımı — S05.

## Kabul kriterleri

- Yedi senaryonun **her biri için bir test**.
- Her testin **kırmızı yanabildiği ölçülmüş** ve raporda yazılı (§6).
- Bir senaryo seçilmediğinde davranış **statik** — bunu ayrı bir test tutuyor,
  çünkü "varsayılan değişmiyor" iddiası en kolay sessizce kaybolan şey.

## Bir uyarı — bu ticket'ın en kolay düşeceği tuzak

Senaryo geçişleri **duvar saatine bağlanmamalı.** Bu depoda süreye bağlı bir
testin geçme sebebi iki kez makinenin yükü oldu (`DiscoveryWorkerTests`,
`GrokPropertyTests`). Bir senaryo "30 saniye sonra susar" diye yazılırsa yüklü
bir makinede yanlış anı ölçer.

Geçişler **olay sayısına** ya da **açık bir tetiklemeye** bağlansın; süre
ölçülmek istenen şeyse mutlak bütçe yerine aynı koşumda alınan bir tabana oran.
