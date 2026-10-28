// Browser side of the platform services (Platform/WebPlatformServices.cs).

// ---------- localStorage (preferences, remembered session) ----------
export const lsGet = (key) => { try { return localStorage.getItem(key); } catch { return null; } };
export const lsSet = (key, value) => { try { localStorage.setItem(key, value); } catch { /* private mode / quota */ } };
export const lsRemove = (key) => { try { localStorage.removeItem(key); } catch { /* ignore */ } };

// ---------- connectivity ----------
export function isOnline() { return navigator.onLine; }

export function watchConnectivity(dotnetRef) {
    const notify = () => dotnetRef.invokeMethodAsync('OnConnectivityChanged', navigator.onLine);
    window.addEventListener('online', notify);
    window.addEventListener('offline', notify);
}

// ---------- full screen ----------
export async function toggleFullScreen() {
    try {
        if (document.fullscreenElement) await document.exitFullscreen();
        else await document.documentElement.requestFullscreen();
    } catch { /* not allowed (iframe, old browser) */ }
}
