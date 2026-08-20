---
kind: spec
title: "Keşif — 'adı ile gövdesi ayrışan bekçi' mekanik olarak aranabilir mi"
---

# Keşif — "adı ile gövdesi ayrışan bekçi" mekanik olarak aranabilir mi

**Cevap: bu iki kuralla hayır, ve ölçüldü.** Yan ürün olarak kullanılabilir bir
şey çıktı ama o bir kapı değil, bir **inceleme listesi**.

Soru şuydu: bu turda beş kez elle bulunan sınıfın — adı iddiasından geniş olan
test — bekçisi yazılabilir mi. Aşağıda iki kural denendi, ikisi de ölçüldü,
ikisi de sevk edilmedi.

## Ölçüm tabanı

| | |
| --- | --- |
| C# test metodu | **885–901** (regex'e göre) |
| Kod tanımlayıcısı (tip/metot/özellik) | **1420** |

## Kural 1 — "adındaki tanımlayıcı gövdesinde geçmeli"

Önerilen biçim: test adının bir parçası bir kod tanımlayıcısıyla eşleşiyorsa
(`surum` → `Version` gibi), gövdede o tanımlayıcı geçmeli.

| Ölçüm | Sonuç |
| --- | --- |
| Kuralın **baktığı** test (ad↔sembol eşleşmesi) | 126 / 901 (**%14**) |
| İşaretlenen | 16 |
| Beş bilinen örnekten yakaladığı | **0** |

**Neden çalışmıyor — iki ayrı sebep:**

1. **Yanlış pozitif baskın.** İşaretlenenlerin neredeyse tamamı, Türkçe cümlede
geçen sıradan bir kelimenin bir tip adıyla çakışmasından doğuyor: `Vendor`,
`Type`, `Owner`, `Next`, `Score`, `Golden`, `Pipeline`, `Principal`, `Slug`.
En umut verici aday `Kolona_yazilmayan_OCSF_alani_attrs_uzerinden_gorunuyor`
idi; gövdesi okundu ve **iddiayı karşılıyor** —
`SELECT unmapped['ocsf.disposition_id'] FROM events_ocsf` sorgusu tam olarak
"kolona yazılmayan alan görünüyor" demek. Kelime `attrs` diye yazılmıyor,
o kadar.

2. **Yakalaması gerekeni hiç görmüyor.** Bu turda bulunan beş örneğin adında
kod tanımlayıcısı **yok**: `zincir`, `izin listesi`, `bütün`, `kendisi` — hepsi
Türkçe kavram. Kural bu adlara **hiç bakmıyor**.

İkisi birlikte öldürücü: az bakıyor, baktığında yanılıyor, ve aradığımız şeyi
görmüyor.

## Kural 2 — "nicel iddia eden ad + tek vaka gövdesi"

Beş örneğin ortak imzası tanımlayıcı değil **nicelik**: adda "bütün", "her",
"hiçbir", "zincir", "tamamı", "kendisi", "boşaldı mı" geçiyor ve gövde tek bir
vakayı koşuyor.

| Ölçüm | Sonuç |
| --- | --- |
| Nicel iddia taşıyan ad | **56 / 885 (%6)** |
| Nicel ad **+** tek vaka gövdesi (döngü yok, `Theory` yok, ≤1 `Assert`) | **14** |
| Bunların içinde gerçekten ilginç olan | **1** (`Izin_listesi_bosaldi_mi`) |
| Beş örnekten ad filtresinin yakaladığı | **3 / 4** (C# olanların) |

**Recall iyileşti, precision çöktü.** İşaretlenen 14'ün 13'ü meşru: *"boş kapsam
hiçbir satır döndürmüyor"* iddiasının doğru gövdesi **zaten** tek bir iddiadır.
Nicel bir ad, çok vakalı bir gövde gerektirmiyor — "hiçbir" çoğu zaman tek bir
sınır durumunu anlatıyor.

## Neden sevk edilmedi

%7 isabetle çalışan bir kapı, gürültü üretir. Gürültü muafiyet doğurur,
muafiyet listesi büyür, ve büyüyen muafiyet listesi bekçiyi kör eder — bu
depoda **beş kez** ödenen bedelin tam kendisi. Kapı olarak eklemek, kapatmaya
çalıştığı hatayı bir kat yukarı taşımak olurdu.

Ayrıca `it.each` ile çöken TypeScript paketi bu kuralların **hiçbirinin**
kapsamında değil: orada sorun ad değil, **sayının kapsam sanılması**.

## Kullanılabilir yan ürün — kapı değil, liste

Kural 2'nin **ad filtresi tek başına** anlamlı: 885 testten **56'sı** nicel bir
kapsam iddiası taşıyor. Bu okunabilir bir sayı ve faz başına bir kez gözden
geçirilebilir. Öneri: kapı değil, **inceleme kalemi** — bir testin adı "bütün",
"her", "hiçbir", "zincir" diyorsa, ona dokunan kişi gövdesinin o iddiayı hâlâ
hak ettiğini kontrol etsin.

Listeyi üretmek için (kalıcı araç yazılmadı; bir kapı sanılmasın diye):

```bash
grep -rhoE 'public\s+(async\s+)?(Task|void)\s+[A-Za-z_0-9]*(butun|Her_|_her_|hicbir|zincir|tamami|uctan|kendisi|bosald)[A-Za-z_0-9]*' \
  tests --include='*.cs' | sed 's/.* //' | sort -u
```

**Neden `tools/` altına bir betik konmadı:** koşulabilir bir şey, er ya da geç
CI'a girer; CI'a giren bir şey yeşil/kırmızı olmak zorunda kalır; ve bu ölçüm
%7 isabetle kırmızı yanamaz. Her zaman yeşil yanan bir "bekçi" ise bu deponun
adını koyduğu hata sınıfı.

## Ne öğrenildi

Sınıfın **mekanik imzası yok**, çünkü ayrışma adın Türkçesi ile gövdenin
davranışı arasında ve ikisi farklı diller. Beş örnekten hiçbiri sözdiziminden
görülebilir değildi; beşi de **okunarak** bulundu.

Buradan çıkan tek genellenebilir kural şu, ve zaten protokolde: bir testin özet
yorumu **koşturulduğunda ne kanıtlayacağını** yazıyorsa, ad ile gövde arasındaki
mesafe o cümlede görünür hâle geliyor. Mekanik bekçi yerine geçen şey, iddiayı
yazılı hâle getirme alışkanlığı.
