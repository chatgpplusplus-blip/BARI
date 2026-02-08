window.scheduleInterop = window.scheduleInterop || {};

window.scheduleInterop.getRect = (el) => {
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { top: r.top, height: r.height };
};
