// LogAnalyzer Lite arayüzü.
//
// Ayrı dosyada çünkü satır içi betik bir sözdizimi hatasıyla (kapanmamış şablon
// dizgesi) tamamen ölüydü ve bunu söyleyen hiçbir kapı yoktu. CI artık
// `node --check` ile bu dosyayı ayrıştırıyor.

const basePath = window.location.pathname.startsWith('/loganalyzer') ? '/loganalyzer' : '';

// Durum değiştiren her çağrı bu başlığı taşır; sunucu taşımayanı (ve çapraz
// siteden geleni) 403 ile reddeder. Başlık CSRF'e karşı: başka bir site bu
// başlığı CORS ön uçuşu olmadan ekleyemez.
const ADMIN_HEADERS = { 'Content-Type': 'application/json', 'X-Requested-With': 'loganalyzer-lite' };

const WEBHOOK_ERRORS = {
  not_configured: 'Adres tanımlı değil',
  timeout: 'Zaman aşımı — sağlayıcı yanıt vermedi',
  unreachable: 'Sağlayıcıya ulaşılamadı',
  rejected: 'Sağlayıcı bildirimi reddetti'
};

let allIncidents = [];

function esc(value) {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

async function adminPost(path, body) {
  return fetch(`${basePath}${path}`, {
    method: 'POST',
    headers: ADMIN_HEADERS,
    credentials: 'same-origin',
    body: JSON.stringify(body ?? {})
  });
}

async function errorMessage(res, fallback) {
  try {
    const data = await res.json();
    return data.message || fallback;
  } catch {
    return fallback;
  }
}

async function loadStats() {
  try {
    const res = await fetch(`${basePath}/api/v1/stats`);
    if (!res.ok) return;
    const data = await res.json();
    document.getElementById('stat-total').innerText = Number(data.totalIngested).toLocaleString('tr-TR');
    document.getElementById('stat-success').innerText = Number(data.successfulRequests).toLocaleString('tr-TR');
    document.getElementById('stat-incidents').innerText = Number(data.activeIncidents).toLocaleString('tr-TR');
    document.getElementById('stat-duration').innerText = `${data.avgDurationMs} ms`;
    document.getElementById('stat-purged').innerText = Number(data.purgedCount).toLocaleString('tr-TR');
  } catch (err) {
    console.error('Stats error:', err);
  }
}

async function loadIncidents() {
  try {
    const res = await fetch(`${basePath}/api/v1/incidents?limit=100`);
    if (!res.ok) return;
    allIncidents = await res.json();
    renderIncidents(allIncidents);
  } catch (err) {
    console.error('Incidents error:', err);
  }
}

// Olay alanları dışarıdan (ingest) geliyor: HTML'e girmeden önce kaçırılıyor.
function renderIncidents(list) {
  const tbody = document.getElementById('incidents-tbody');
  if (!list || list.length === 0) {
    tbody.innerHTML = '<tr><td colspan="8" class="text-center py-8 text-slate-500"><i class="fa-solid fa-shield-halved text-emerald-400 mr-2"></i> Aktif incident yok. Sistem pürüzsüz çalışıyor.</td></tr>';
    return;
  }

  tbody.innerHTML = list.map(item => {
    const status = Number(item.httpStatus) || 0;
    const isFatal = status >= 500 || item.level === 'Fatal';
    const badgeColor = isFatal ? 'bg-rose-500/20 text-rose-400 border-rose-500/30' : (status >= 400 ? 'bg-amber-500/20 text-amber-400 border-amber-500/30' : 'bg-emerald-500/20 text-emerald-400 border-emerald-500/30');
    const timeStr = new Date(item.timestamp).toLocaleTimeString('tr-TR');

    return `
      <tr class="hover:bg-slate-800/40 transition">
        <td class="py-2.5 px-4 font-mono text-[11px] text-slate-400">${esc(timeStr)}</td>
        <td class="py-2.5 px-4 font-mono font-medium text-indigo-400">${esc(item.trackingId || '-')}</td>
        <td class="py-2.5 px-4 text-slate-300 font-sans">${esc(item.source || '-')}</td>
        <td class="py-2.5 px-4 font-mono text-slate-200">
          <span class="text-slate-500 mr-1">${esc(item.httpMethod || 'POST')}</span>${esc(item.endpoint || '/')}
        </td>
        <td class="py-2.5 px-4">
          <span class="px-2 py-0.5 rounded text-[10px] font-bold border ${badgeColor}">${status}</span>
        </td>
        <td class="py-2.5 px-4 font-mono text-slate-400">${Math.round(Number(item.durationMs) || 0)} ms</td>
        <td class="py-2.5 px-4 max-w-xs truncate text-slate-300" title="${esc(item.errorMessage || '')}">${esc(item.errorMessage || '-')}</td>
        <td class="py-2.5 px-4 text-right">
          <button data-incident-id="${esc(item.id)}" class="incident-detail px-2.5 py-1 bg-indigo-600/20 hover:bg-indigo-600 text-indigo-300 hover:text-white rounded border border-indigo-500/30 transition text-[11px]">
            <i class="fa-solid fa-magnifying-glass-chart mr-1"></i> Uçuş Kaydı
          </button>
        </td>
      </tr>
    `;
  }).join('');

  tbody.querySelectorAll('button.incident-detail').forEach(button => {
    button.addEventListener('click', () => openDetail(button.dataset.incidentId));
  });
}

function filterIncidents() {
  const search = document.getElementById('search-input').value.toLocaleLowerCase('tr-TR');
  const level = document.getElementById('level-select').value;

  const filtered = allIncidents.filter(i => {
    const matchesLevel = !level || (i.level && i.level.toLowerCase() === level.toLowerCase());
    const matchesSearch = !search ||
      (i.trackingId && i.trackingId.toLocaleLowerCase('tr-TR').includes(search)) ||
      (i.endpoint && i.endpoint.toLocaleLowerCase('tr-TR').includes(search)) ||
      (i.errorMessage && i.errorMessage.toLocaleLowerCase('tr-TR').includes(search));
    return matchesLevel && matchesSearch;
  });

  renderIncidents(filtered);
}

function openDetail(id) {
  const item = allIncidents.find(x => x.id === id);
  if (!item) return;

  document.getElementById('modal-tracking-id').innerText = item.trackingId || item.id;
  document.getElementById('modal-level').innerText = item.level || 'Error';
  document.getElementById('modal-status-badge').innerText = item.httpStatus;
  document.getElementById('modal-endpoint-title').innerText = (item.httpMethod || 'POST') + ' ' + (item.endpoint || '/');

  document.getElementById('modal-time').innerText = new Date(item.timestamp).toLocaleString('tr-TR');
  document.getElementById('modal-duration').innerText = String(item.durationMs) + ' ms';
  document.getElementById('modal-source').innerText = item.source || '-';
  document.getElementById('modal-ip').innerText = item.clientIp || '-';

  document.getElementById('modal-error-message').innerText = item.errorMessage || 'Belirtilmedi';

  const stackContainer = document.getElementById('modal-stack-container');
  if (item.stackTrace) {
    stackContainer.classList.remove('hidden');
    document.getElementById('modal-stack-trace').innerText = item.stackTrace;
  } else {
    stackContainer.classList.add('hidden');
  }

  document.getElementById('modal-request-payload').innerText = formatJson(item.requestPayload);
  document.getElementById('modal-response-payload').innerText = formatJson(item.responsePayload);

  document.getElementById('detail-modal').classList.remove('hidden');
}

function closeModal() {
  document.getElementById('detail-modal').classList.add('hidden');
}

function formatJson(str) {
  if (!str) return '(Boş / Kaydedilmedi)';
  try {
    const parsed = JSON.parse(str);
    return JSON.stringify(parsed, null, 2);
  } catch {
    return str;
  }
}

function copyToClipboard(elementId) {
  const text = document.getElementById(elementId).innerText;
  navigator.clipboard.writeText(text);
  alert('Panoya kopyalandı!');
}

// Webhook adresleri sır: sunucu yalnızca maske ve "kayıtlı mı" bilgisi
// döndürüyor. Alan boş bırakılırsa kayıtlı adres korunur; silmek için
// "Kaldır" kutusu işaretlenir.
function showWebhookState(kind, configured, mask) {
  const input = document.getElementById(`cfg-${kind}-url`);
  const state = document.getElementById(`cfg-${kind}-state`);
  const clear = document.getElementById(`cfg-${kind}-clear`);
  input.value = '';
  input.placeholder = configured ? `${mask} (kayıtlı — değiştirmek için yeni adres girin)` : input.dataset.example;
  state.innerText = configured ? 'Kayıtlı' : 'Tanımlı değil';
  clear.checked = false;
  clear.disabled = !configured;
}

async function openAlertModal() {
  try {
    const res = await fetch(`${basePath}/api/v1/alerts/config`);
    if (res.ok) {
      const cfg = await res.json();
      document.getElementById('cfg-enabled').checked = cfg.enabled ?? true;
      showWebhookState('slack', cfg.slackConfigured, cfg.slackWebhookUrl);
      showWebhookState('discord', cfg.discordConfigured, cfg.discordWebhookUrl);
      document.getElementById('cfg-min-level').value = cfg.minLevel || 'Error';
      document.getElementById('cfg-cooldown').value = cfg.cooldownSeconds ?? 60;
    }
  } catch (e) {
    console.error('Config fetch error:', e);
  }
  document.getElementById('test-alert-result').classList.add('hidden');
  document.getElementById('alert-modal').classList.remove('hidden');
}

function closeAlertModal() {
  document.getElementById('alert-modal').classList.add('hidden');
}

async function saveAlertConfig() {
  const cooldown = parseInt(document.getElementById('cfg-cooldown').value, 10);
  const cfg = {
    enabled: document.getElementById('cfg-enabled').checked,
    slackWebhookUrl: document.getElementById('cfg-slack-url').value.trim() || null,
    discordWebhookUrl: document.getElementById('cfg-discord-url').value.trim() || null,
    clearSlackWebhook: document.getElementById('cfg-slack-clear').checked,
    clearDiscordWebhook: document.getElementById('cfg-discord-clear').checked,
    minLevel: document.getElementById('cfg-min-level').value,
    cooldownSeconds: Number.isNaN(cooldown) ? 60 : cooldown,
    notifyOnSlaBreach: true,
    slaThresholdMs: 3000
  };

  try {
    const res = await adminPost('/api/v1/alerts/config', cfg);
    if (res.ok) {
      alert('Alarm ve webhook yapılandırması başarıyla kaydedildi!');
      closeAlertModal();
    } else {
      alert('Ayarlar kaydedilemedi: ' + await errorMessage(res, 'bilinmeyen hata'));
    }
  } catch (err) {
    alert('Hata: ' + err.message);
  }
}

async function testWebhook(target) {
  const resBox = document.getElementById('test-alert-result');
  resBox.className = 'p-3 rounded-xl border text-[11px] bg-indigo-950/30 border-indigo-800 text-indigo-300';
  resBox.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1"></i> Webhook test bildirimi iletiliyor...';
  resBox.classList.remove('hidden');

  const label = target.toLocaleUpperCase('tr-TR');

  try {
    const res = await adminPost('/api/v1/alerts/test', { target: target });
    if (!res.ok) {
      resBox.className = 'p-3 rounded-xl border text-[11px] bg-rose-950/30 border-rose-800 text-rose-300';
      resBox.innerText = `${label} testi yapılamadı: ${await errorMessage(res, 'istek reddedildi')}`;
      return;
    }

    const data = await res.json();
    const success = target === 'slack' ? data.slackSuccess : data.discordSuccess;
    const err = target === 'slack' ? data.slackError : data.discordError;

    if (success) {
      resBox.className = 'p-3 rounded-xl border text-[11px] bg-emerald-950/30 border-emerald-800 text-emerald-300';
      resBox.innerHTML = `<i class="fa-solid fa-circle-check mr-1"></i> ${esc(label)} testi başarılı! Bildirim iletildi.`;
    } else {
      resBox.className = 'p-3 rounded-xl border text-[11px] bg-rose-950/30 border-rose-800 text-rose-300';
      resBox.innerHTML = `<i class="fa-solid fa-circle-xmark mr-1"></i> ${esc(label)} testi başarısız: ${esc(WEBHOOK_ERRORS[err] || 'Bilinmeyen hata')}`;
    }
  } catch (e) {
    resBox.className = 'p-3 rounded-xl border text-[11px] bg-rose-950/30 border-rose-800 text-rose-300';
    resBox.innerText = 'Hata: ' + e.message;
  }
}

async function triggerSimulate() {
  const res = await adminPost('/api/v1/demo/simulate-incident', {});
  if (res.status === 404) {
    alert('Demo simülasyonu bu ortamda kapalı (LITE_ENABLE_DEMO).');
    return;
  }
  if (!res.ok) {
    alert('Simülasyon başarısız: ' + await errorMessage(res, 'istek reddedildi'));
    return;
  }
  await loadAll();
}

async function purgeLogs() {
  if (!confirm('24 saatten eski veya başarılı tüm log sayaçları süpürülecek. Onaylıyor musunuz?')) return;
  const res = await adminPost('/api/v1/logs/purge', { keepLastHours: 1, resetStats: false });
  if (!res.ok) {
    alert('Süpürme başarısız: ' + await errorMessage(res, 'istek reddedildi'));
    return;
  }
  await loadAll();
}

async function loadAll() {
  await loadStats();
  await loadIncidents();
}

// Auto-refresh interval
setInterval(() => {
  if (document.getElementById('auto-refresh').checked) {
    loadAll();
  }
}, 3000);

// Initial load
loadAll();
