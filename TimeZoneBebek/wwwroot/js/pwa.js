(() => {
    if (!("serviceWorker" in navigator)) return;

    window.addEventListener("load", async () => {
        try {
            await navigator.serviceWorker.register("/sw.js");
        } catch (error) {
            console.warn("PWA service worker registration failed", error);
        }
    });
})();
