#!/usr/bin/env bash
#
# PostHog hobby compose dosyasını SABİTLENMİŞ bir commit'ten indirir.
#
# Neden kopyalamıyoruz: PostHog self-host sürümlenmiş imaj yayımlamıyor,
# HEAD'den akıyor. Dosyayı depoya kopyalamak, ilk gün bayatlayan ve
# bayatladığı hiçbir yerde görünmeyen bir kopya üretirdi. Sabitlenmiş commit,
# yükseltmeyi `POSTHOG_REF` satırında GÖRÜNÜR bir hareket yapıyor.
set -euo pipefail

# Yükseltme = bu satırı değiştirmek. Değiştirirken PostHog'un CHANGELOG'una
# bakın; self-host için CVE yayımlanmıyor.
POSTHOG_REF="${POSTHOG_REF:-master}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/.gitignored"
mkdir -p "$out"

url="https://raw.githubusercontent.com/PostHog/posthog/${POSTHOG_REF}/docker-compose.hobby.yml"

echo "İndiriliyor: $url"
curl -fsSL "$url" -o "$out/docker-compose.hobby.yml"

# İndirilen dosyanın gerçekten compose olduğunu doğrula. Bir 404 sayfası da
# 200 ile gelebiliyor ve sessizce yazılırdı.
if ! grep -q "^services:" "$out/docker-compose.hobby.yml"; then
  echo "HATA: indirilen dosya compose dosyasına benzemiyor. POSTHOG_REF doğru mu?" >&2
  exit 1
fi

echo "Yazıldı: $out/docker-compose.hobby.yml"
echo
echo "Sonraki adım:"
echo "  cp $here/.env.example $here/.env   # düzenleyin"
echo "  docker compose --env-file $here/.env -f $out/docker-compose.hobby.yml up -d"
