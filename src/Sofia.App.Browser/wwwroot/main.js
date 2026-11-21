// Sofia browser head boot (spec 044).
//
// Registers the `sofia` interop module the WASM code imports ([JSImport("...", "sofia")]) and then runs the
// managed entry point (Sofia.App.Browser.Program.Main -> StartBrowserAppAsync). Everything here is browser API
// only: no framework, no build step.

import { dotnet } from './_framework/dotnet.js';

const { setModuleImports, getConfig, getAssemblyExports, runMain } = await dotnet
    .withApplicationArgumentsFromQuery()
    .create();

const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);

const mediaQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
const notifyReducedMotion = (value) => {
    try {
        exports.Sofia.App.Browser.BrowserInterop.OnReducedMotionChanged(value);
    } catch (error) {
        console.warn('sofia: reduced-motion callback failed', error);
    }
};

let audioContext = null;
const audio = () => {
    if (audioContext === null) {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        audioContext = new Ctor();
    }
    return audioContext;
};

// The six pre-rendered WAVs arrive as base64 (they are asset resources inside Sofia.App) at the gain the shared
// SoundService computed with the web's volume curve. Voices are fire-and-forget, like the desktop player.
const playWav = (base64, gain) => {
    try {
        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        const ctx = audio();
        if (ctx.state === 'suspended') ctx.resume();
        ctx.decodeAudioData(bytes.buffer, (buffer) => {
            const source = ctx.createBufferSource();
            source.buffer = buffer;
            const volume = ctx.createGain();
            volume.gain.value = gain;
            source.connect(volume).connect(ctx.destination);
            source.start();
        }, (error) => console.warn('sofia: sound decode failed', error));
    } catch (error) {
        console.warn('sofia: sound playback failed', error);
    }
};

setModuleImports('sofia', {
    origin: () => window.location.origin,
    storageGet: (key) => {
        try { return window.localStorage.getItem(key); } catch { return null; }
    },
    storageSet: (key, value) => {
        try { window.localStorage.setItem(key, value); } catch (error) { console.warn('sofia: localStorage write failed', error); }
    },
    playWav,
    prefersReducedMotion: () => mediaQuery.matches,
    watchReducedMotion: () => mediaQuery.addEventListener('change', (event) => notifyReducedMotion(event.matches)),
});

// Browsers start an AudioContext suspended until a gesture; resume it on the first interaction.
window.addEventListener('pointerdown', () => { try { if (audioContext && audioContext.state === 'suspended') audioContext.resume(); } catch { } }, { once: true });

// The boot label goes away once Avalonia painted its canvas (or after a generous timeout on a slow first load).
const boot = document.getElementById('boot');
const out = document.getElementById('out');
if (boot && out) {
    const observer = new MutationObserver(() => {
        if (out.querySelector('canvas')) {
            boot.remove();
            observer.disconnect();
        }
    });
    observer.observe(out, { childList: true, subtree: true });
    setTimeout(() => boot.remove(), 15000);
}

await runMain();
