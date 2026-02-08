(function () {
    const state = new WeakMap();

    function measureContent(inner) {
        const card = inner.querySelector(".label-card") || inner;

        const iw = Math.max(
            inner.offsetWidth || 0,
            card.offsetWidth || 0,
            card.scrollWidth || 0
        );

        const ih = Math.max(
            inner.offsetHeight || 0,
            card.offsetHeight || 0,
            card.scrollHeight || 0
        );

        return { iw, ih };
    }

    function applyFit(frame, inner) {
        if (!frame || !inner) return;

        // guard anti-recursión
        const s = state.get(frame);
        if (s && s.fitting) return;

        if (s) s.fitting = true;
        try {
            inner.style.transformOrigin = "top left";
            inner.style.transform = "translate(0px, 0px) scale(1)";

            const fr = frame.getBoundingClientRect();
            const fw = fr.width;
            const fh = fr.height;
            if (!fw || !fh) return;

            const { iw, ih } = measureContent(inner);
            if (!iw || !ih) return;

            let scale = Math.min(fw / iw, fh / ih);

            if (Math.abs(1 - scale) < 0.005) scale = 1;
            scale = Math.max(scale, 0.20);

            const tx = Math.max(0, (fw - iw * scale) / 2);
            const ty = Math.max(0, (fh - ih * scale) / 2);

            inner.style.transform = `translate(${tx}px, ${ty}px) scale(${scale})`;
            inner.style.setProperty("--fit", String(scale));
        } finally {
            if (s) s.fitting = false;
        }
    }

    function fitWithRetries(frame, inner) {
        applyFit(frame, inner);
        requestAnimationFrame(() => applyFit(frame, inner));
        setTimeout(() => applyFit(frame, inner), 60);
        setTimeout(() => applyFit(frame, inner), 160);
        setTimeout(() => applyFit(frame, inner), 320);
    }

    function observe(frame, inner) {
        if (!frame || !inner) return;

        unobserve(frame);

        const s = { ro: null, mo: null, fitting: false };
        state.set(frame, s);

        s.ro = new ResizeObserver(() => fitWithRetries(frame, inner));
        s.ro.observe(frame);

        // 👇 IMPORTANTÍSIMO: NO observar attributes (evita loop por style.transform)
        s.mo = new MutationObserver(() => fitWithRetries(frame, inner));
        s.mo.observe(inner, {
            subtree: true,
            childList: true,
            characterData: true
            // attributes: false  (implícito)
        });

        if (document.fonts && document.fonts.ready) {
            document.fonts.ready.then(() => fitWithRetries(frame, inner)).catch(() => { });
        }

        fitWithRetries(frame, inner);
    }

    function unobserve(frame) {
        const s = state.get(frame);
        if (!s) return;
        try { s.ro && s.ro.disconnect(); } catch { }
        try { s.mo && s.mo.disconnect(); } catch { }
        state.delete(frame);
    }

    window.labelFit = window.labelFit || {};
    window.labelFit.fit = (frame, inner) => fitWithRetries(frame, inner);
    window.labelFit.refit = (frame, inner) => fitWithRetries(frame, inner);
    window.labelFit.observe = (frame, inner) => observe(frame, inner);
    window.labelFit.unobserve = (frame) => unobserve(frame);
})();
