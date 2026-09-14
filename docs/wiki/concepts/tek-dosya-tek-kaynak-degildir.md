---
title: Tek dosya, tek kaynak değildir
category: concepts
tags: [kavram, surec, kapsam, bizigo]
aliases: [tek kaynak ilkesi, kavram tekilleştirme]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: related
sources: [docs/epic/fs-simulatorler/index.md, docs/epic/tickets-fs/filo-kapsam/index.md, CLAUDE.md]
source_digest: "sha256-12/v1 CLAUDE.md=d270c2c035f1 docs/epic/fs-simulatorler/index.md=22bb02d19d29 docs/epic/tickets-fs/filo-kapsam/index.md=a30ad9554779"
summary: Bir bilgiyi tek dosyada toplamak onu tek kaynak yapmıyor. Kavramın kendisi tek yerde durmalı — ve onu tanıyan predicate de. Üç ölçülmüş örnek.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.78
lifecycle: draft
lifecycle_changed: 2026-08-26
tier: core
created: 2026-08-26T00:00:00Z
updated: 2026-08-26T00:00:00Z
---

# Tek dosya, tek kaynak değildir

Bir bilgiyi **tek dosyada** toplamak, onu **tek kaynak** yapmıyor. İkisi ayrı
sorular ve ikincisi daha zor: dosya sayısı görünür, kavramın kaç yerde
yaşadığı görünmez.

FS-a'nın son iki ticket'ı bu ayrımın üç ayrı hâlini üretti. Üçü de aynı sonuca
çıkıyor: **ayrışan şey sessizce ayrışıyor.**

## 1 · Veri tek dosyada, kavram iki yerde

S05'in filo tanımı (`catalog/simulators/filo.yaml`) beş cihazı tek dosyada
topluyor — kabul kriteri buydu. Ama dosya **`owner_group` taşımıyor**, ve bu
ayrı bir karar.

Taşısaydı grup iki yerde dururdu: profilde ve filoda. Ayrıştıkları gün ayrışan
şey **kapsamın kendisi** olurdu — ve bu depoda o hatanın ölçülmüş bir örneği
var:

> CSV'de aynı kaynak iki kez geçince son satır sessizce kazanıyordu — kazanan
> şey `owner_group`, yani kapsamın kendisi.

Bir test dosyanın **şeklini** sabitliyor: `devices:` bloğunda `owner_group`
geçerse kırmızı yanıyor. Yani kural bir okuma disiplinine değil bir mekanizmaya
bağlı.

Aynı gerekçe, **aynı cihazın iki kez tanımlanmasının** neden reddedildiğini de
söylüyor. "Son satır kazansın" seçeneği vardı ve seçilmedi: ret ucuz — bir satır
silinir; sessiz kazanma pahalı — belirtisi yok ve **yanlış grup ekranda doğru
görünür**.

## 2 · Sözlük tek, predicate iki

S04 senaryo sözlüğünü tekilleştirdi: yedi adlandırılmış geçiş tek yerde. Ama
*"bu ad baseline mi"* sorusunun cevabı **iki yerde** kaldı — taşıyıcı boş dizeyi
baseline sayıyordu, motor hem boşu hem `"baseline"` sabitini tanıyordu, ve
taşıyıcının config yolu motora hiç uğramıyordu.

Sonuç: adlandırılmış baseline sözlükte arandı, bulunamadı, ve hata *"profilde
tanımlı değil"* dedi — yani **okuyan kişiyi profil dosyasına gönderdi**, oysa
profil doğruydu.

> Bir kavramı tekilleştirmek, onu **tanıyan** predicate'i tekilleştirmekle aynı
> şey değil.

Deponun "ikinci kopya yazma" kuralı *veriyi* anlatıyor; bu, **tanımanın**
karşılığı. Ve boşluğun neden birim testlerinde görünmediği de ölçüldü: mevcut
zincir testi baseline'ı parametresiz kurucuyla alıyordu, yani taşıyıcının config
yolunu **adlandırılmış** baseline'la geçen tek bir test yoktu. Sonuç
`CLAUDE.md` §6'nın kaydettiği şekle düştü: *"iki gösterim doğdu, birim paketi
sessiz kaldı, CI kırmızı yandı."*

## 3 · Yeni kavram eklerken onu görecek eski kodu aramamak

`filo.yaml` profil dizinine kondu ve profil yükleyici `*.yaml` glob'luyordu —
yani filo dosyasını **bozuk bir cihaz profili** saydı. İki bekçi birden kırmızı
yandı.

Belirti yine yanlış yeri gösteriyordu: hata *"profil bozuk"* diyecekti ve okuyan
kişi **var olmayan bir cihazın** peşine düşecekti.

Bu, üçüncü hâl: kavram tek yerde tanımlı olabilir ama onu **görmemesi gereken**
eski kod hâlâ görüyordur. Yeni bir kavram eklerken sorulacak soru *"bunu kim
okuyor"* değil, **"bunu kim yanlışlıkla okur"**.

## Silmenin yan ürünü: gizlediği şey görünür

S05'in kapanış ölçütü uçtan uca harness'taki elle tohumlamanın silinmesiydi.
Silinen iki adımdan biri envanteri **gelmiş veriden** türetiyordu:

```
SELECT source_id, ... FROM events WHERE owner_group = ... AND parser_id != ''
```

Yani *"hangi kaynaklar var"* sorusunu **veri göndermiş** kaynaklardan
cevaplıyordu. Sonucu:

> Veri göndermemiş bir kaynak envantere hiç yazılmıyordu — ve **envanterde
> olmayan bir cihazın sustuğu görülemez.**

Sessizlik alarmının sınanacak zemini yoktu. Bu, [[concepts/sessiz-yanlis-davranis]]
listesindeki kalemlerle aynı aileden: hata yok, sayaç yok, alarm hiç
tetiklenmiyor ve **tetiklenmemesi doğru görünüyor**.

Bulgunun çıkış biçimi ayrıca kayda değer: aranarak değil, **silinerek** çıktı.
Bir şeyi kaldırınca onun neyi gizlediği görünüyor.

## Uygulama

Bir bilgiyi tek dosyaya taşırken üç soru:

1. **Bu bilgi başka nerede duruyor?** Duruyorsa, hangisi kaynak — ve diğeri
   neden var?
2. **Bu kavramı kim tanıyor?** Tanıma iki yerdeyse veri tek yerde olsa bile
   ayrışma mümkün.
3. **Bunu kim yanlışlıkla okur?** Yeni dosya, eski bir tarayıcının kapsamına
   girmiş olabilir.

Üçünün de cevabı bir **mekanizmaya** bağlanmalı — bir okuma disiplinine değil.
Bu depoda okuma disiplinine bağlanan kurallar tekrar tekrar kaybetti.

### Ama bu sayfanın ölçtüğü şey ölçüm aracının kendisine de uygulanıyor

`CLAUDE.md` §6 aynı gün bir adım kazandı ve gerekçesi bu sayfanın 3. hâliyle
aynı aileden: kırmızı ölçümünde **kusurun dosyada gerçekten olduğunu iddia
etmek**. Üç ajan bağımsız olarak kusuru uyguladığını sandı, dosya değişmemişti
ya da ölçüm sabit bir girdiyle *"kusur yok"* hâlini iki kez ölçüyordu — ve
sonuç **yeşil** geldi.

Bağ şu: burada *"bu kavramı kim tanıyor"* sorusunun cevabı ikiye ayrılıyordu;
orada *"bu ölçüm neyi ölçtü"* sorusunun cevabı ayrılıyordu. İkisinde de
görünen şey (dosya sayısı · yeşil sonuç) altındaki şeyi (tanıma sayısı ·
ölçümün gerçekten koşup koşmadığı) **temsil etmiyor**.

Ve ikisinin çözümü de aynı cinsten: görünmeyeni **iddiaya çevirmek**. Bir test
`assert 'KIRMIZI' in dosya` yazdığında, ölçümün koştuğunu okuma disiplininden
mekanizmaya taşımış oluyor. Bkz. [[skills/olcum-bekciyi-kirmizi-yakmak]]
Adım 1.5.
