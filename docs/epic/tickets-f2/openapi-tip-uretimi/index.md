---
title: "T14 — OpenAPI tip üretimi ve API istemcisi"
kind: ticket
status: 2
---

# T14 — OpenAPI tip üretimi ve API istemcisi

**Bağımlılık:** T13 · **Sonraki:** T15, T17, T19, T23, T24

## Amaç

UI ile API arasındaki sözleşmenin **elle yazılmaması**. Tipler şemadan üretiliyor,
sürüklendiği gün CI kırmızı yanıyor.

## Kapsam

### İçinde

- `Bizigo.Api`'nin ürettiği OpenAPI belgesinden TypeScript tipleri.
- Tiplenmiş, ince bir istemci sarmalayıcısı: yol ve yöntem adları üretilen
tiplerden geliyor.
- **CI kapısı:** üretilen tipler depodakiyle aynı değilse adım düşüyor. Aksi
halde şema değişir, UI derlenmeye devam eder ve hata çalışma zamanında çıkar.
- Hata gövdelerinin tek tipli ele alınması: `{ error, hint }` biçimi F1'de
yerleşik (`/v1/events/{id}/raw` örneği).

### Dışında

- İstemci tarafı önbellek/durum yönetimi kütüphanesi seçimi — T15'te ekranla
birlikte kararlaştırılacak.

## Kabul kriterleri

- `dotnet run` ile üretilen şema, `ui/`'daki tiplerle birebir eşleşiyor; CI bunu
doğruluyor.
- API'ye yeni bir uç eklenip tipler yeniden üretilmezse CI düşüyor.
- İstemci 401'i BFF'in yenileme akışına, 403'ü "yetkiniz yok" ekranına, 404'ü
"bulunamadı"ya çeviriyor — F1'de kapsam dışı olay **404** dönüyor (403 değil),
çünkü 403 "böyle bir olay var" bilgisini sızdırırdı.

## Notlar

Şema F1'de doğrulandı: OpenAPI **3.1.1**, 18 yol, zorunlu alanlar tam. Yani bu
ticket sıfırdan bir şema düzeltme işi değil, üretim hattı kurma işi.

`/v1/logs` şemada görünüyor ama UI'ın işi değil — collector'ın ucu. İstemci
üretiminde dışarıda bırakılabilir.
