window.bariSvg = {
    getRect: (el) => {
        if (!el) return { left: 0, top: 0, width: 0, height: 0 };
        const r = el.getBoundingClientRect();
        return { left: r.left, top: r.top, width: r.width, height: r.height };
    },
    capturePointer: (el, pointerId) => {
        try { el.setPointerCapture(pointerId); } catch (e) { }
    },
    releasePointer: (el, pointerId) => {
        try { el.releasePointerCapture(pointerId); } catch (e) { }
    }
};

// wwwroot/js/bariHud.js (por ejemplo)
window.bariHud = window.bariHud || {};

window.bariHud.initDraggableHud = function () {
    const hud = document.getElementById("ma-hud");
    if (!hud) return;

    // Evitar enganchar eventos múltiples en cada render
    if (hud.dataset.dragInit === "1") return;
    hud.dataset.dragInit = "1";

    const handle = hud.querySelector(".ma-hud-handle") || hud;

    let pointerId = null;
    let startX = 0, startY = 0;
    let startLeft = 0, startTop = 0;

    function parsePx(v) {
        const n = parseFloat(v);
        return Number.isFinite(n) ? n : 0;
    }

    handle.addEventListener("pointerdown", (e) => {
        pointerId = e.pointerId;

        const css = getComputedStyle(hud);
        // Posición actual del HUD relativa a su contenedor, NO al viewport
        startLeft = parsePx(css.left);
        startTop = parsePx(css.top);

        startX = e.clientX;
        startY = e.clientY;

        handle.setPointerCapture(pointerId);
        e.preventDefault();
    });

    handle.addEventListener("pointermove", (e) => {
        if (pointerId === null || e.pointerId !== pointerId) return;

        const dx = e.clientX - startX;
        const dy = e.clientY - startY;

        hud.style.left = `${startLeft + dx}px`;
        hud.style.top = `${startTop + dy}px`;
    });

    function endDrag(e) {
        if (pointerId === null) return;
        if (e && e.pointerId !== pointerId) return;

        try {
            handle.releasePointerCapture(pointerId);
        } catch (_) { }

        pointerId = null;
    }

    handle.addEventListener("pointerup", endDrag);
    handle.addEventListener("pointercancel", endDrag);
    handle.addEventListener("lostpointercapture", endDrag);
};

