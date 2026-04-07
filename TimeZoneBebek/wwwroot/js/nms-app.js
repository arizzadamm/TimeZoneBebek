const { createApp, ref, computed, onMounted, onBeforeUnmount, nextTick, watch } = Vue;

createApp({
    setup() {
        const EMPTY_SNAPSHOT = { generatedAtUtc: null, refreshIntervalSeconds: 15, summary: {}, categories: [] };
        const snapshot = ref(EMPTY_SNAPSHOT);
        const history = ref({ range: "5m", generatedAtUtc: null, points: [] });
        const isLoading = ref(true);
        const isHistoryLoading = ref(false);
        const connectionState = ref("CONNECTING");
        const lastError = ref("");
        const activeItemName = ref("");
        const fullscreenState = ref("PENDING");
        const isWallboardMode = ref(false);
        const viewMode = ref(localStorage.getItem("NMS_VIEW_MODE") || "dashboard");
        const historyRange = ref(localStorage.getItem("NMS_HISTORY_RANGE") || "5m");
        const downTargetFilter = ref("active");
        const historyRanges = ["5m", "30m", "1h", "6h", "24h"];
        const availabilityChartRef = ref(null);
        const categoryChartRef = ref(null);
        const latencyChartRef = ref(null);

        let hubConnection = null;
        let fallbackTimer = null;
        let fullscreenHandler = null;
        let availabilityChart = null;
        let categoryChart = null;
        let latencyChart = null;

        const categories = computed(() => (snapshot.value.categories || []).map(category => ({
            ...category,
            items: [...(category.items || [])].sort((left, right) => getStatusPriority(left.status) - getStatusPriority(right.status))
        })));
        const summary = computed(() => snapshot.value.summary || {});
        const totalCards = computed(() => summary.value.totalItems || 0);
        const healthyCards = computed(() => summary.value.upCount || 0);
        const downCards = computed(() => summary.value.downCount || 0);
        const degradedCards = computed(() => summary.value.degradedCount || 0);
        const unknownCards = computed(() => summary.value.unknownCount || 0);
        const activeItem = computed(() =>
            categories.value.flatMap(category => category.items || []).find(item => item.name === activeItemName.value) || null);
        const currentTargetStates = computed(() => {
            const map = new Map();
            for (const category of categories.value) {
                for (const item of category.items || []) {
                    for (const target of item.targets || []) {
                        const key = `${item.name}__${target.name}__${target.target || ""}`;
                        map.set(key, {
                            status: target.status || "UNKNOWN",
                            checkedAtUtc: item.checkedAtUtc || snapshot.value.generatedAtUtc || null
                        });
                    }
                }
            }
            return map;
        });

        const historySummary = computed(() => {
            const points = history.value.points || [];
            const latencyValues = points.map(point => point.averageLatencyMs).filter(value => value != null);
            return {
                sampleCount: points.length,
                averageLatencyMs: latencyValues.length ? Math.round(latencyValues.reduce((sum, value) => sum + value, 0) / latencyValues.length) : null,
                peakDown: points.reduce((peak, point) => Math.max(peak, point.downCount || 0), 0),
                peakDegraded: points.reduce((peak, point) => Math.max(peak, point.degradedCount || 0), 0)
            };
        });

        const downTargetSummary = computed(() => {
            const map = new Map();
            for (const point of history.value.points || []) {
                for (const target of point.downTargets || []) {
                    const key = `${target.itemName}__${target.targetName}__${target.targetAddress}`;
                    const existing = map.get(key);
                    if (!existing) {
                        map.set(key, {
                            itemName: target.itemName,
                            targetName: target.targetName,
                            targetAddress: target.targetAddress,
                            detail: target.Detail || target.detail || "",
                            lastSeenUtc: target.seenAtUtc || target.SeenAtUtc || point.generatedAtUtc,
                            hitCount: 1
                        });
                        continue;
                    }

                    existing.hitCount += 1;
                    const seenAt = target.seenAtUtc || target.SeenAtUtc || point.generatedAtUtc;
                    if (new Date(seenAt).getTime() >= new Date(existing.lastSeenUtc).getTime()) {
                        existing.lastSeenUtc = seenAt;
                        existing.detail = target.Detail || target.detail || existing.detail;
                    }
                }
            }

            const enriched = [...map.values()].map((target) => {
                const key = `${target.itemName}__${target.targetName}__${target.targetAddress}`;
                const current = currentTargetStates.value.get(key);
                const currentStatus = current?.status || "RECOVERED";
                return {
                    ...target,
                    currentStatus,
                    isActiveDown: currentStatus === "DOWN",
                    checkedAtUtc: current?.checkedAtUtc || null
                };
            });

            return enriched
                .filter((target) => downTargetFilter.value === "all" || target.isActiveDown)
                .sort((left, right) => {
                    if (left.isActiveDown !== right.isActiveDown) return left.isActiveDown ? -1 : 1;
                    if (left.hitCount !== right.hitCount) return right.hitCount - left.hitCount;
                    return new Date(right.lastSeenUtc).getTime() - new Date(left.lastSeenUtc).getTime();
                })
                .slice(0, 12);
        });

        function getStatusClass(status) {
            if (status === "UP") return "is-up";
            if (status === "DOWN") return "is-down";
            if (status === "DEGRADED") return "is-degraded";
            return "is-unknown";
        }

        function getStatusPriority(status) {
            if (status === "DOWN") return 0;
            if (status === "DEGRADED") return 1;
            if (status === "UNKNOWN") return 2;
            return 3;
        }

        function formatTimestamp(value) {
            if (!value) return "--";
            return new Date(value).toLocaleString("id-ID", {
                year: "numeric",
                month: "short",
                day: "2-digit",
                hour: "2-digit",
                minute: "2-digit",
                second: "2-digit"
            });
        }

        function formatTimeLabel(value) {
            if (!value) return "--";
            return new Date(value).toLocaleTimeString("id-ID", {
                hour: "2-digit",
                minute: "2-digit"
            });
        }

        function formatLatency(value) {
            return value == null ? "--" : `${value} ms`;
        }

        function applySnapshot(data) {
            snapshot.value = data || EMPTY_SNAPSHOT;
            connectionState.value = hubConnection?.state === "Connected" ? "LIVE" : connectionState.value;
            if (activeItemName.value && !activeItem.value) {
                activeItemName.value = "";
            }
        }

        function ensureChart(instanceRef, hostRef, configFactory) {
            if (!hostRef.value) return instanceRef;
            if (instanceRef) return instanceRef;
            instanceRef = new ApexCharts(hostRef.value, configFactory());
            instanceRef.render();
            return instanceRef;
        }

        function baseChartOptions() {
            return {
                chart: {
                    type: "line",
                    height: 280,
                    toolbar: { show: false },
                    background: "transparent",
                    animations: { enabled: true, easing: "easeinout", speed: 450 }
                },
                legend: {
                    labels: { colors: "#a9c2bd" },
                    fontFamily: "JetBrains Mono"
                },
                xaxis: {
                    categories: [],
                    labels: { style: { colors: "#7f9a95", fontFamily: "JetBrains Mono" } },
                    axisBorder: { color: "rgba(0,255,204,0.12)" },
                    axisTicks: { color: "rgba(0,255,204,0.12)" }
                },
                yaxis: {
                    tickAmount: 4,
                    forceNiceScale: true,
                    decimalsInFloat: 0,
                    labels: {
                        minWidth: 28,
                        maxWidth: 34,
                        offsetX: -4,
                        style: { colors: "#6f8783", fontFamily: "JetBrains Mono", fontSize: "10px" },
                        formatter: (value) => {
                            if (!Number.isFinite(value)) return "";
                            if (Math.abs(value) >= 1000) {
                                return `${Math.round(value / 100) / 10}k`;
                            }
                            return `${Math.round(value)}`;
                        }
                    }
                },
                stroke: { curve: "smooth", width: 2.2 },
                grid: { borderColor: "rgba(0,255,204,0.08)" },
                tooltip: { theme: "dark" },
                dataLabels: { enabled: false }
            };
        }

        function ensureCharts() {
            availabilityChart = ensureChart(availabilityChart, availabilityChartRef, () => ({
                ...baseChartOptions(),
                colors: ["#39ff14", "#ff4a78", "#ffc24c", "#9ab1ac"],
                series: []
            }));

            categoryChart = ensureChart(categoryChart, categoryChartRef, () => ({
                ...baseChartOptions(),
                colors: ["#ff4a78", "#ffc24c", "#39ff14", "#00e1ff"],
                series: []
            }));

            latencyChart = ensureChart(latencyChart, latencyChartRef, () => ({
                ...baseChartOptions(),
                colors: ["#00e1ff"],
                series: []
            }));
        }

        function updateCharts() {
            if (viewMode.value !== "analytic") return;
            ensureCharts();

            const points = history.value.points || [];
            const labels = points.map(point => formatTimeLabel(point.generatedAtUtc));

            availabilityChart?.updateOptions({
                xaxis: { categories: labels }
            }, false, false);
            availabilityChart?.updateSeries([
                { name: "UP", data: points.map(point => point.upCount || 0) },
                { name: "DOWN", data: points.map(point => point.downCount || 0) },
                { name: "DEGRADED", data: points.map(point => point.degradedCount || 0) },
                { name: "UNKNOWN", data: points.map(point => point.unknownCount || 0) }
            ], true);

            const categoryNames = [...new Set(points.flatMap(point => (point.categories || []).map(category => category.name)))];
            categoryChart?.updateOptions({
                xaxis: { categories: labels }
            }, false, false);
            categoryChart?.updateSeries(categoryNames.slice(0, 4).map(name => ({
                name,
                data: points.map(point => {
                    const category = (point.categories || []).find(entry => entry.name === name);
                    if (!category) return 0;
                    return (category.downCount || 0) + (category.degradedCount || 0);
                })
            })), true);

            latencyChart?.updateOptions({
                xaxis: { categories: labels }
            }, false, false);
            latencyChart?.updateSeries([
                { name: "Avg Latency", data: points.map(point => point.averageLatencyMs || 0) }
            ], true);
        }

        async function fetchSnapshot() {
            try {
                const res = await fetch("/api/nms/status", {
                    headers: { "X-API-KEY": API_KEY }
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                applySnapshot(await res.json());
                lastError.value = "";
            } catch (error) {
                lastError.value = error.message || "Unable to fetch NMS status";
                connectionState.value = "DEGRADED";
            } finally {
                isLoading.value = false;
            }
        }

        async function fetchHistory() {
            isHistoryLoading.value = true;
            try {
                const res = await fetch(`/api/nms/history?range=${encodeURIComponent(historyRange.value)}`, {
                    headers: { "X-API-KEY": API_KEY }
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                history.value = await res.json();
                lastError.value = "";
                await nextTick();
                updateCharts();
            } catch (error) {
                lastError.value = error.message || "Unable to fetch NMS history";
            } finally {
                isHistoryLoading.value = false;
            }
        }

        async function initSignalR() {
            hubConnection = new signalR.HubConnectionBuilder()
                .withUrl("/nmsHub")
                .withAutomaticReconnect()
                .build();

            hubConnection.on("NmsStatusUpdated", async (data) => {
                applySnapshot(data);
                connectionState.value = "LIVE";
                isLoading.value = false;
                if (viewMode.value === "analytic") {
                    await fetchHistory();
                }
            });

            hubConnection.onreconnecting(() => {
                connectionState.value = "RECONNECTING";
            });

            hubConnection.onreconnected(() => {
                connectionState.value = "LIVE";
            });

            hubConnection.onclose(() => {
                connectionState.value = "DEGRADED";
            });

            try {
                await hubConnection.start();
                connectionState.value = "LIVE";
            } catch (error) {
                connectionState.value = "DEGRADED";
                lastError.value = error.message || "SignalR connection failed";
            }
        }

        function startFallbackPolling() {
            stopFallbackPolling();
            fallbackTimer = setInterval(async () => {
                await fetchSnapshot();
                if (viewMode.value === "analytic") {
                    await fetchHistory();
                }
            }, 30000);
        }

        function stopFallbackPolling() {
            if (fallbackTimer) {
                clearInterval(fallbackTimer);
                fallbackTimer = null;
            }
        }

        function openItem(item) {
            activeItemName.value = item.name;
        }

        function closeItem() {
            activeItemName.value = "";
        }

        function openWallboard() {
            window.location.href = "/nms?wallboard=1";
        }

        function setViewMode(mode) {
            viewMode.value = mode;
        }

        async function setHistoryRange(range) {
            if (historyRange.value === range && history.value.points?.length) return;
            historyRange.value = range;
            await fetchHistory();
        }

        async function enterFullscreen() {
            if (!document.fullscreenEnabled) {
                fullscreenState.value = "UNSUPPORTED";
                return;
            }

            if (document.fullscreenElement) {
                fullscreenState.value = "ACTIVE";
                return;
            }

            try {
                await document.documentElement.requestFullscreen();
                fullscreenState.value = "ACTIVE";
            } catch {
                fullscreenState.value = "BLOCKED";
            }
        }

        function syncWallboardMode(enabled) {
            isWallboardMode.value = enabled;
            document.body.classList.toggle("nms-wallboard", enabled);
        }

        watch(viewMode, async (mode) => {
            localStorage.setItem("NMS_VIEW_MODE", mode);
            if (mode === "analytic") {
                await nextTick();
                await fetchHistory();
            }
        });

        watch(historyRange, (range) => {
            localStorage.setItem("NMS_HISTORY_RANGE", range);
        });

        onMounted(async () => {
            const params = new URLSearchParams(window.location.search);
            const wallboardParam = (params.get("wallboard") || "").trim().toLowerCase();
            const shouldEnableWallboard = wallboardParam === "1" || wallboardParam === "true" || wallboardParam === "yes";

            syncWallboardMode(shouldEnableWallboard);
            await fetchSnapshot();
            await initSignalR();
            startFallbackPolling();
            if (viewMode.value === "analytic") {
                await fetchHistory();
            }
            if (shouldEnableWallboard) {
                await enterFullscreen();
            } else {
                fullscreenState.value = document.fullscreenElement ? "ACTIVE" : "IDLE";
            }

            fullscreenHandler = () => {
                fullscreenState.value = document.fullscreenElement ? "ACTIVE" : "IDLE";
            };
            document.addEventListener("fullscreenchange", fullscreenHandler);
        });

        onBeforeUnmount(async () => {
            syncWallboardMode(false);
            stopFallbackPolling();
            availabilityChart?.destroy();
            categoryChart?.destroy();
            latencyChart?.destroy();
            if (fullscreenHandler) {
                document.removeEventListener("fullscreenchange", fullscreenHandler);
                fullscreenHandler = null;
            }
            if (hubConnection) {
                await hubConnection.stop();
            }
        });

        return {
            snapshot,
            categories,
            summary,
            history,
            historySummary,
            downTargetSummary,
            historyRange,
            downTargetFilter,
            historyRanges,
            viewMode,
            isLoading,
            isHistoryLoading,
            connectionState,
            fullscreenState,
            isWallboardMode,
            lastError,
            activeItem,
            totalCards,
            healthyCards,
            downCards,
            degradedCards,
            unknownCards,
            availabilityChartRef,
            categoryChartRef,
            latencyChartRef,
            getStatusClass,
            formatTimestamp,
            formatLatency,
            openItem,
            closeItem,
            openWallboard,
            setViewMode,
            setHistoryRange,
            enterFullscreen,
            fetchSnapshot
        };
    }
}).mount("#app");
