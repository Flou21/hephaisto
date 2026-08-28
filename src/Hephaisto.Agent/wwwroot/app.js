// Two conveniences and nothing else. Everything that matters on these pages is rendered
// server-side over the circuit; if this file fails to load, the console still works and
// only the scroll-to-step animation and the remembered name are lost.
//
// No module system, no bundler, no CDN: the pod may run in a cluster with no egress, and a
// script that silently fails to load would take the incident console with it.
window.hephaisto = {
    // localStorage throws outright in some privacy modes rather than returning null, so
    // every access is guarded. A convenience must never be able to break the page.
    storageGet(key) {
        try {
            return window.localStorage.getItem(key);
        } catch {
            return null;
        }
    },

    storageSet(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch {
            // Full, disabled, or a private window. Nothing to do and nothing to report.
        }
    },

    // The other half of the grounding link: clicking a citation must land the reader on the
    // step it quotes, with that step opened. A citation that scrolls to a collapsed
    // <details> looks broken, and the whole point of the link is that it can be followed.
    revealStep(anchorId) {
        const el = document.getElementById(anchorId);

        if (!el) {
            return;
        }

        if (el.tagName === 'DETAILS') {
            el.open = true;
        }

        el.scrollIntoView({ behavior: 'smooth', block: 'center' });

        // Re-triggered by removing and re-adding, so clicking the same citation twice
        // flashes twice instead of doing nothing the second time.
        el.classList.remove('hp-flash');
        void el.offsetWidth;
        el.classList.add('hp-flash');

        window.setTimeout(() => el.classList.remove('hp-flash'), 2000);
    },
};
