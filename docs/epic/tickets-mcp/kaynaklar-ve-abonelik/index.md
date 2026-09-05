---
title: "M07 — Kaynaklar ve abonelik"
kind: ticket
status: 0
---

# M07 — Araç değil veri: kaynaklar ve durum bildirimi

[MCP teknik plan §5](../../mcp-teknik-plan/index.md): *"Kaynaklar (resources)
olarak sunulacaklar **araç değil veri**: kanıt paketi belgesi, RCA raporu,
parser tanımı. Abonelik `rca.runs` için anlamlı — koşum durum değiştirdiğinde
bildirim."*

Ayrım önemli ve ticket'ın tamamı ona dayanıyor: bir **araç** çağrılır ve bir
iş yapar; bir **kaynak** adreslenir ve okunur. İkisini karıştırmak, her
okumayı bir araç çağrısına çevirip [bağlam bütçesini](#6--bilinen-sınırlar-ve-açık-sorular)
gereksiz yere şişirir.

## 1 · Bugünkü hâl — ölçüldü

Kaynak olarak sunulacak üç şeyin **verisi** bugün var:

| Kaynak | Bugünkü karşılığı |
| --- | --- |
| Kanıt paketi belgesi | `GET /v1/rca/{id}`; `Bizigo.Evidence` tarafında paket deposu |
| RCA raporu | `Bizigo.Rca/Reasoning/RcaReportStore` + `RcaReportDocument` (bu hafta T44/T51 ile girdi) |
| Parser tanımı | `GET /v1/parsers`, `catalog/parsers/` |

`rca.runs` durum kaynağı: `GET /v1/rca/runs` var (T46'nın üç yönlü ayrımı —
[M05 §6.2](../rca-araclari/index.md)'de doğrulanmamış olarak işaretli).

**Ölçmedim:** MCP kaynak/abonelik yeteneğinin M01'in dalında ne kadarının
kurulduğuna bakmadım. M01 `initialize` yetenek anlaşmasını kuruyor; kaynak
yeteneğinin orada ilan edilip edilmediği **bilinmiyor**.

## 2 · Kapsam

**İçinde**

- Üç belge türünün **kaynak** olarak sunulması: kanıt paketi, RCA raporu,
  parser tanımı.
- `rca.runs` için **abonelik**: koşum durum değiştirdiğinde bildirim.
- Kaynakların **kapsam** kapısından geçmesi — bir kaynak URI'si adreslenebilir
  olduğu için kapsamı atlamanın en kolay yolu ([M04](../okuma-araclari/index.md)
  kapısı burada da geçerli).

**Dışında**

- İstemler (prompts). Plan §2 onları uyum tablosunda sayıyor ama araç
  tablosunda karşılığı yok; bu ticket **eklemiyor**.
- Araçların kendisi — M04 ve M05.

## 3 · Kabul kriterleri

1. Üç kaynak türü bir URI şemasıyla adreslenebiliyor (§6.1).
2. `rca.runs` aboneliği koşum durum değiştirdiğinde bildirim gönderiyor, ve
   bildirim **istemcinin desteklediği** bir yetenekse gönderiliyor — bitti
   tanımı §1'in *"desteklenmeyen yetenek kullanılmıyor"* şartı burada da geçer.
3. **Kaynak okuma kapsamı atlamıyor.** Bir kaynak URI'si tahmin edilebilirse
   (ör. sıralı kimlik), kapsam kontrolü URI'nin kendisinde değil **okuma
   yolunda** olmalı.
4. Abonelik **sızdırmıyor**: istemci gittiğinde abonelik kapanıyor.
5. Kırmızı yanabildiği ölçüldü: kapsam dışı bir kaynağın URI'si doğrudan
   istendiğinde reddedildiği görüldü.

## 4 · Bitti tanımından karşıladıkları

Doğrudan bir madde **sahiplenmiyor**; bu yazılı olsun ki okuyan kişi M07'yi
bir maddeye bağlı sanmasın. Beslediği:

- **§1** — desteklenmeyen yeteneğin kullanılmaması, abonelik tarafında.
- **§7** — kapsam tek kapıdan; kaynaklar bu kapının **en sessiz** kaçış yolu.

## 5 · Bağımlılık ve sıra

**M05.** Abonelik `rca.runs` için anlamlı ve `rca.runs` M05 ile geliyor; kanıt
paketi ile RCA raporu da M05'in ürettiği/okuduğu şeyler. Kaynakları önce
yazmak, tüketicisi olmayan bir adres alanı yazmak olurdu.

## 6 · Bilinen sınırlar ve açık sorular

### 6.1 · URI şeması — **plan sessiz, uydurmuyorum**

Plan §2'nin uyum tablosu *"Kaynaklar | URI şeması, abonelik, değişiklik
bildirimi"* diyor ve şemayı **tarif etmiyor**. Karar M07'nin.

Kararın taşıması gereken bir kısıt var ve o ölçüldü: URI **kapsamı temsil
etmemeli**. Adres içinde `owner_group` taşımak, kapsamı adresin doğruluğuna
bağlamak olur — ve bu deponun CSV dersinde kazanan şey tam olarak
`owner_group`'du, yani kapsamın kendisi.

### 6.2 · Diğer açık sorular

1. **Kaynak yeteneği M01'de ilan ediliyor mu?** Bakmadım. Edilmiyorsa M07 önce
   `initialize` tarafına dokunmak zorunda ve bu M01'e geri besleme demek.
2. **Bildirim sıklığı bir bütçe kalemi mi?** Koşum durumu sık değişirse
   abonelik modelin bağlamına gürültü basar. Plan sessiz.
3. **Değişiklik bildirimi kaynaklar için de var mı** (parser tanımı
   değiştiğinde), yoksa yalnızca `rca.runs` mı? Plan yalnızca `rca.runs`'ı
   *"anlamlı"* diye işaretliyor — diğerlerini **yasaklamıyor**, ve bu iki
   farklı şey.

### 6.3 · Bağlam maliyeti

Kaynaklar araç değil, yani `tools/list` bütçesine **girmiyorlar** — ve bu
ticket'ın sessiz kazancı bu. M01'in ölçtüğü araç başına **194 belirteç**
kaynaklar için ödenmiyor; üç belge türünü üç araç olarak sunmak ≈ 580 belirteç
eder ve **her bağlamda** taşınırdı.
