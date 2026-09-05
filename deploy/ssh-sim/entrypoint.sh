#!/bin/bash
# N2/N3 simülatörünün açılışı (S03 · S06).
#
# `sh` DEĞİL `bash`: `vendor-cli` buradan da kaynak alınıyor (vendor listesi
# tek yerde duruyor) ve o dosya `read -n`/`read -t` kullanıyor. Ash altında
# kaynak almak, sözdizimi geçse bile davranışı sessizce ayrıştırırdı.
set -eu

: "${SIM_PROFILE:?SIM_PROFILE gerekli — hangi cihaz taklit edilecek}"
: "${SIM_USERNAME:=bizigo-ro}"
: "${SIM_PASSWORD:?SIM_PASSWORD gerekli}"
: "${SIM_SCENARIO:=}"
: "${SIM_PAGING:=etkilesimli}"
: "${SIM_PAGE_LINES:=24}"
: "${SIM_MORE_TIMEOUT:=2}"

# Profilin VAR OLDUĞU açılışta doğrulanıyor.
#
# Yanlış yazılmış bir profil adıyla sessizce ayağa kalksaydı, test bağlanır,
# komut çalıştırır ve "config bulunamadı" alırdı — yani arıza SSH katmanında
# aranırdı. Açılışta patlamak, hatayı doğru yere koyuyor.
if [ ! -f "/sim/${SIM_PROFILE}.yaml" ]; then
    echo "profil bulunamadı: /sim/${SIM_PROFILE}.yaml" >&2
    echo "katalog monte edilmemiş olabilir (compose: ./catalog/simulators -> /sim)" >&2
    exit 1
fi

# Vendor ve hostname PROFİLDEN okunuyor — ortam değişkeninden değil.
#
# İkinci bir yerde durabilirlerdi (compose'da bir `SIM_VENDOR`), ve ayrıştıkları
# gün öykünme `cisco` promptu basarken envanter `fortinet` derdi. S01'in tek
# kaynak kararı burada da geçerli.
SIM_VENDOR="$(sed -n 's/^vendor:[[:space:]]*//p' "/sim/${SIM_PROFILE}.yaml" | head -n 1 | tr -d '\r')"
SIM_HOSTNAME="$(sed -n 's/^hostname:[[:space:]]*//p' "/sim/${SIM_PROFILE}.yaml" | head -n 1 | tr -d '\r')"

# Öykünmenin TANIMADIĞI bir vendor açılışta patlıyor.
#
# Sessizce genel bir davranışa düşseydi, yeni bir vendor profili eklendiği gün
# simülatör onu "tanıyor gibi" davranırdı: prompt basmaz, hata metni vermez, ve
# testler bunu "cihaz cevap verdi" diye okurdu (§7).
. /usr/local/bin/vendor-cli

if ! cli_vendor_bilinen "${SIM_VENDOR}"; then
    echo "N3 öykünmesi '${SIM_VENDOR}' vendor'ını tanımıyor (profil: ${SIM_PROFILE})." >&2
    echo "tanınanlar: ${CLI_VENDORS}" >&2
    exit 2
fi

# Host anahtarları her açılışta yeniden üretiliyor.
#
# Kalıcı olsaydı imaja gömülü bir özel anahtar taşımak zorunda kalırdık ve o
# anahtar depoya girerdi. Testin bilinen bir host anahtarına ihtiyacı yok:
# istemci tarafı (`SshDeviceTransport`) host anahtarını doğrulamıyor — bu
# ürünün kendi sınırı ve S03'ün kapsamı değil.
ssh-keygen -A

if ! id "${SIM_USERNAME}" >/dev/null 2>&1; then
    adduser -D -s /bin/bash "${SIM_USERNAME}"
fi

echo "${SIM_USERNAME}:${SIM_PASSWORD}" | chpasswd

# AYARLAR DOSYAYA YAZILIYOR, ORTAMA BIRAKILMIYOR.
#
# OpenSSH oturum çocuğuna temiz bir ortam kuruyor; `SIM_*` gibi keyfi
# değişkenler sshd'nin ortamında dursa bile ForceCommand'a ULAŞMIYOR. S03 bunu
# profil için biliyordu (`/sim-profil`) ama `SIM_SCENARIO` ortamdan okunmaya
# devam ediyordu ve compose onu ayarlıyordu — yani senaryo seçimi sessizce yok
# sayılıyor, dağıtıcı her zaman baseline veriyordu.
#
# `SshSimulatorCliTests` artık dağıtıcının okuduğu her `SIM_*` değişkeninin
# burada yazıldığını sınıyor; unutulan bir satır Docker'sız kırmızı yanıyor.
#
# Parola BURAYA GİRMİYOR: dosyayı okuyan dağıtıcının parolaya ihtiyacı yok ve
# ihtiyacı olmayan bir sır, sızabilecek bir sırdır.
cat > /sim-ayarlar <<AYAR
SIM_PROFILE='${SIM_PROFILE}'
SIM_SCENARIO='${SIM_SCENARIO}'
SIM_VENDOR='${SIM_VENDOR}'
SIM_HOSTNAME='${SIM_HOSTNAME}'
SIM_USERNAME='${SIM_USERNAME}'
SIM_PAGING='${SIM_PAGING}'
SIM_PAGE_LINES='${SIM_PAGE_LINES}'
SIM_MORE_TIMEOUT='${SIM_MORE_TIMEOUT}'
AYAR

chmod 0444 /sim-ayarlar

echo "N2 hazır: profil=${SIM_PROFILE} vendor=${SIM_VENDOR} kullanıcı=${SIM_USERNAME} sayfalama=${SIM_PAGING}" >&2

# `-D` ön planda, `-e` günlükler stderr'e: ikisi de container'ın canlılığının
# ve çıktısının görülebilmesi için.
exec /usr/sbin/sshd -D -e
