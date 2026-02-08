(() => {
    const maxHiddenMinutes = 5;
    let hiddenAt = null;

    const reloadIfStale = () => {
        if (!hiddenAt) {
            return;
        }

        const hiddenMs = Date.now() - hiddenAt;
        hiddenAt = null;

        if (hiddenMs >= maxHiddenMinutes * 60 * 1000) {
            window.location.reload();
        }
    };

    document.addEventListener("visibilitychange", () => {
        if (document.hidden) {
            hiddenAt = Date.now();
            return;
        }

        reloadIfStale();
    });

    window.addEventListener("online", () => {
        if (document.visibilityState === "visible") {
            reloadIfStale();
        }
    });
})();
