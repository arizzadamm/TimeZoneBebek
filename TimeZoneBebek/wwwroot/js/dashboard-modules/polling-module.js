(function (global) {
    const createPollingRuntime = (deps) => {
        const isPageVisible = () => document.visibilityState === "visible";

        const refreshVisibleData = () => {
            if (!isPageVisible()) return;
            deps.fetchSocData();
            deps.fetchAllIncidents();
            deps.fetchHealth();
            deps.fetchAttendanceSummary();
        };

        const start = () => {
            document.addEventListener("visibilitychange", refreshVisibleData);
            return refreshVisibleData;
        };

        const stop = (handler) => {
            document.removeEventListener("visibilitychange", handler);
        };

        return { start, stop, isPageVisible };
    };

    global.DashboardPollingModule = { createPollingRuntime };
})(window);
