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

// ------------------------------------------------------------------------------------
// Recover the page by itself when the agent restarts.
//
// Blazor puts `components-reconnect-rejected` on the modal when the server is reachable
// again but refuses this circuit - which is what a pod restart always looks like, because
// the circuit's state died with the old process. Blazor stops there and waits for a human
// to press reload. On a wall-mounted incident console nobody is there to press it, so the
// page sits frozen showing whatever it last knew, indefinitely.
//
// A reload is safe here: every page reads its state from the server on init and holds
// nothing a refresh would lose.
//
// This is belt and braces over the overlay, not a replacement for it. If this observer
// never runs - script blocked, MutationObserver unavailable - the overlay still tells the
// reader the page is stale, which is the part that must not fail.
// ------------------------------------------------------------------------------------
(() => {
    const modal = document.getElementById('components-reconnect-modal');

    if (!modal || typeof MutationObserver === 'undefined') {
        return;
    }

    let reloading = false;

    new MutationObserver(() => {
        if (reloading || !modal.classList.contains('components-reconnect-rejected')) {
            return;
        }

        reloading = true;

        // A short delay, not an immediate reload: the class can appear while the new
        // process is still binding its port, and a reload that races it lands on a
        // connection error, which is a worse thing to be looking at than the overlay.
        window.setTimeout(() => window.location.reload(), 1500);
    }).observe(modal, { attributes: true, attributeFilter: ['class'] });
})();
