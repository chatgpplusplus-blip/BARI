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
