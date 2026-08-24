#!/bin/sh
# N2 simülatörünün açılışı (S03).
set -eu

: "${SIM_PROFILE:?SIM_PROFILE gerekli — hangi cihaz taklit edilecek}"
: "${SIM_USERNAME:=bizigo-ro}"
: "${SIM_PASSWORD:?SIM_PASSWORD gerekli}"

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

# Dağıtıcı profili buradan okuyor.
echo "${SIM_PROFILE}" > /sim-profil

echo "N2 hazır: profil=${SIM_PROFILE} kullanıcı=${SIM_USERNAME}" >&2

# `-D` ön planda, `-e` günlükler stderr'e: ikisi de container'ın canlılığının
# ve çıktısının görülebilmesi için.
exec /usr/sbin/sshd -D -e
