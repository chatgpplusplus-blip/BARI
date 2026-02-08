window.labelExport = window.labelExport || {};

function waitImages(el) {
    const imgs = Array.from(el.querySelectorAll("img"));
    return Promise.all(imgs.map(img => {
        if (img.complete && img.naturalWidth > 0) return Promise.resolve();
        return new Promise(res => {
            img.addEventListener("load", res, { once: true });
            img.addEventListener("error", res, { once: true }); // no bloquea
        });
    }));
}

window.labelExport.downloadPdf = async function (element, widthMm, heightMm, filename) {
    if (!element) return;

    // Espera fuentes e imágenes para que el render sea fiel
    if (document.fonts && document.fonts.ready) {
        try { await document.fonts.ready; } catch { }
    }
    await waitImages(element);

    // Clona a un contenedor offscreen SIN transforms (evita el scale del "fit")
    const w = element.offsetWidth;
    const h = element.offsetHeight;

    const host = document.createElement("div");
    host.style.position = "fixed";
    host.style.left = "-10000px";
    host.style.top = "0";
    host.style.width = w + "px";
    host.style.height = h + "px";
    host.style.background = "#fff";
    host.style.overflow = "hidden";
    host.style.zIndex = "-1";

    const clone = element.cloneNode(true);
    clone.style.transform = "none";
    clone.style.width = w + "px";
    clone.style.height = h + "px";

    host.appendChild(clone);
    document.body.appendChild(host);

    try {
        // DPI objetivo (ajusta si quieres). Mantiene tamaño en mm exacto en PDF.
        const dpi = 300;
        const targetPxW = (widthMm * dpi) / 25.4;
        const scale = Math.max(1, targetPxW / w); // sube resolución si la etiqueta es grande

        const canvas = await html2canvas(host, {
            backgroundColor: "#fff",
            scale,
            useCORS: true,
            allowTaint: false
        });

        const imgData = canvas.toDataURL("image/png");

        const { jsPDF } = window.jspdf;
        const pdf = new jsPDF({
            unit: "mm",
            format: [Number(widthMm), Number(heightMm)],
            orientation: widthMm >= heightMm ? "landscape" : "portrait",
            compress: true
        });

        pdf.addImage(imgData, "PNG", 0, 0, Number(widthMm), Number(heightMm));
        pdf.save(filename || "etiqueta.pdf");
    } finally {
        host.remove();
    }
};
