(() => {
  let lastScrollY = 0;

  document.addEventListener(
    "click",
    (event) => {
      const summary = event.target.closest("summary");
      if (!summary) {
        return;
      }

      lastScrollY = window.scrollY;
      requestAnimationFrame(() => {
        if (Math.abs(window.scrollY - lastScrollY) > 1) {
          window.scrollTo({ top: lastScrollY });
        }
      });
    },
    { capture: true }
  );
})();
