// Browser-side helpers shared by the web app and the desktop app's web view.

// ---------- printing: print one element in a hidden frame, sized for the receipt paper ----------
export function printElement(id, widthMm) {
    const element = document.getElementById(id);
    if (!element) return false;
    const frame = document.createElement('iframe');
    Object.assign(frame.style, { position: 'fixed', right: '0', bottom: '0', width: '0', height: '0', border: '0' });
    document.body.appendChild(frame);
    const styles = [...document.querySelectorAll('link[rel="stylesheet"]')].map(l => `<link rel="stylesheet" href="${l.href}">`).join('');
    const page = widthMm ? `${widthMm}mm auto` : 'A4';
    const doc = frame.contentDocument;
    doc.open();
    doc.write(`<!doctype html><html><head><meta charset="utf-8">${styles}<style>@page{size:${page};margin:2mm}html,body{margin:0;background:#fff}` +
        `.rp-receipt{box-shadow:none!important;max-width:none!important;width:100%!important;border-radius:0!important}</style></head>` +
        `<body>${element.innerHTML}</body></html>`);
    doc.close();
    frame.onload = () => {
        frame.contentWindow.focus();
        frame.contentWindow.print();
        setTimeout(() => frame.remove(), 60000);
    };
    return true;
}

// ---------- kitchen: a short two-tone beep for new tickets ----------
let audio;
export function beep() {
    try {
        audio ??= new (window.AudioContext || window.webkitAudioContext)();
        [880, 1320].forEach((frequency, i) => {
            const oscillator = audio.createOscillator();
            const gain = audio.createGain();
            oscillator.frequency.value = frequency;
            oscillator.connect(gain);
            gain.connect(audio.destination);
            const start = audio.currentTime + i * 0.18;
            gain.gain.setValueAtTime(0.25, start);
            gain.gain.exponentialRampToValueAtTime(0.001, start + 0.16);
            oscillator.start(start);
            oscillator.stop(start + 0.17);
        });
    } catch { /* no audio device */ }
}

// ---------- hotkeys: F2/F3/F8/F9 on the POS, without the browser's own meaning ----------
const hotkeys = new Map();
let nextId = 1;
export function registerHotkeys(dotnetRef, keys) {
    const id = nextId++;
    const handler = e => {
        if (keys.includes(e.key) && !document.querySelector('.rp-overlay')) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnHotkey', e.key);
        }
    };
    window.addEventListener('keydown', handler);
    hotkeys.set(id, handler);
    return id;
}
export function unregisterHotkeys(id) {
    const handler = hotkeys.get(id);
    if (handler) window.removeEventListener('keydown', handler);
    hotkeys.delete(id);
}

export function focus(id) { document.getElementById(id)?.focus(); }
