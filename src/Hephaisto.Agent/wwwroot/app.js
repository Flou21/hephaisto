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

    // --- theme -----------------------------------------------------------------------
    //
    // Three states, and "system" is one of them rather than the absence of a choice: an
    // operator who deliberately follows the OS should see that, not an empty control.
    //
    // Pure JS with no circuit involved. A theme preference is this browser's business, it is
    // stored in this browser, and routing it through SignalR would make it stop working
    // exactly when the console is least well - while the circuit is down.
    THEMES: ['system', 'light', 'dark'],

    readTheme() {
        var t = this.storageGet('hephaisto.theme');

        return this.THEMES.indexOf(t) >= 0 ? t : 'system';
    },

    applyTheme(theme) {
        if (theme === 'system') {
            document.documentElement.removeAttribute('data-theme');
        } else {
            document.documentElement.setAttribute('data-theme', theme);
        }

        // Repaint the browser chrome to match. Read back from the stylesheet rather than
        // carrying a copy of the hex here: tokens.css is the one place a colour is written,
        // and a second copy in JavaScript is a second place for it to drift.
        var bg = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim();

        if (bg) {
            document.querySelectorAll('meta[name="theme-color"]').forEach(function (m) {
                m.setAttribute('content', bg);
            });
        }

        // The visible label is a CSS ::after keyed off the attribute above - see app.css for
        // why. Only the accessible name is set here, and it is re-set on every cycle because
        // aria-label is the one part a stylesheet cannot express.
        document.querySelectorAll('[data-theme-control]').forEach(function (el) {
            el.setAttribute('aria-label', 'Theme: ' + theme + '. Activate to change it.');
        });
    },

    cycleTheme() {
        var next = this.THEMES[(this.THEMES.indexOf(this.readTheme()) + 1) % this.THEMES.length];

        this.storageSet('hephaisto.theme', next);
        this.applyTheme(next);
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

// Label the control with the theme that is actually in force. The attribute was already
// stamped in <head>; this only catches the button up, and it is why the button ships with no
// text of its own - a label rendered server-side would be wrong for every reader who chose.
(function () {
    function sync() { window.hephaisto.applyTheme(window.hephaisto.readTheme()); }

    // Not simply an addEventListener: this file is a plain script at the end of <body>, so on
    // a warm cache it can run AFTER DOMContentLoaded has already fired, and the listener would
    // then never be called at all.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', sync);
    } else {
        sync();
    }
})();
