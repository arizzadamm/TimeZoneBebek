const { createApp, ref, reactive, computed, onMounted, onUnmounted, watch } = Vue;

createApp({
    setup() {
        const CONFIG = { DEF_LAT: -6.212530770381996, DEF_LON: 106.83045523468515, START: 8, END: 16, FRIDAY_END_MINUTE: 30 };
        const audioCtx = new (window.AudioContext || window.webkitAudioContext)();

        const isLoadingData = ref(true);
        const viewMode = ref(localStorage.getItem("SOC_DASH_VIEW_MODE") || "dashboard");
        const odometerTickets = ref(0);
        const incidentModal = reactive({ show: false, data: null });
        const incidentAlert = reactive({ show: false, title: "", message: "", severity: "MEDIUM", count: 0 });
        const isTerminalLive = ref(false);
        const authChecked = ref(false);

        const showBebek = ref(false);
        const locationName = ref("LOCATING...");
        const weather = reactive({ temp: "--°", desc: "WAITING...", icon: '<div class="cloud"></div>', isBad: false });

        const clockHands = reactive({ h: 0, m: 0, s: 0 });
        const clocks = reactive({ wib: "00:00:00", wita: "00:00", wit: "00:00", utc: "00:00", wibStyle: {} });
        const date = reactive({ dayName: "---", fullDate: "Loading...", hijri: "" });
        const attendance = reactive({
            entries: [],
            activeCount: 0,
            clockOutCount: 0,
            lastSyncText: "SYNCING...",
            isOffline: false
        });

        const soc = reactive({ openTickets: 0, criticalOpenTickets: 0, highOpenTickets: 0, defconLevel: "DEFCON 5", defconDesc: "NORMAL OPERATIONS", defconColor: "var(--green)", defconShadow: "none", topAttacker: "---", topAttackerPct: 0, recentIncidents: [], statusCounts: {} });
        const socHealth = reactive({ feedHealthy: false, elasticHealthy: false, epsHealthy: false, threatWebhookHealthy: false, lastBroadcastUtc: null, lastElasticSuccessUtc: null, lastWebhookSuccessUtc: null, lastEpsSuccessUtc: null });
        const incidentFilters = reactive({ query: "", severity: "ALL", status: "ALL", unresolvedOnly: true });
        const allIncidents = ref([]);
        const analystIdentity = "SHIFT-ALPHA";
        const handover = reactive({ unassigned: 0, escalated: 0, newLast4h: 0, activeOwners: 0, note: "No active handover note." });
        const chartRef = ref(null);
        let chartInstance = null;

        const prayerTimes = ref({});
        const imamPool = ref([]);
        const nextPrayer = reactive({ name: "---", time: "--:--", countdown: "", isNear: false });

        const modal = reactive({ show: false, type: "", title: "", msg: "", color: "var(--yellow)", shadow: "none", progressWidth: "100%", transition: "none", prayerName: "", imamText: "...", imamColor: "#aaa" });
        let modalTimer = null;
        const flags = { shiftAlerted: false, prayerAlerted: false };
        const incidentAlertCooldownMs = 45000;
        const incidentAlertFreshWindowMs = 3 * 60 * 1000;
        const knownIncidentIds = new Set();
        let hasHydratedIncidentIds = false;
        let lastIncidentAlertAt = 0;
        let incidentAlertTimer = null;
        let incidentAudio = null;

        const newsFeed = ref([]);
        const sysLogs = ref([]);

        const epsChartRef = ref(null);
        let epsChartInstance = null;
        let chartsRuntime = null;
        let incidentsRuntime = null;
        const currentEps = ref("0");
        const currentEventsLastMinute = ref("0");
        const epsDataSeries = ref(Array(15).fill(0));
        const isRealTraffic = ref(true);
        const allowedStatuses = ["ALL", "NEW", "TRIAGED", "IN_PROGRESS", "ESCALATED", "RESOLVED", "FALSE_POSITIVE"];
        const allowedSeverities = ["ALL", "CRITICAL", "HIGH", "MEDIUM", "LOW"];
        let attendanceToastNode = null;
        let attendanceToastTimers = [];
        let lastAttendanceSnapshot = new Map();
        let attendanceHydrated = false;
        let attendanceRuntime = null;
        let clockRuntime = null;
        let pollingRefreshHandler = null;
        const incidentTracker = { ids: new Set(), hydrated: false, lastAlertAt: 0 };
        const attendancePollMs = 30000;

        const filteredRecentIncidents = computed(() => {
            const q = incidentFilters.query.trim().toLowerCase();
            return soc.recentIncidents.filter((inc) => {
                const matchesQuery = !q || [inc.id, inc.title, inc.attacker, inc.affectedAsset, inc.source].filter(Boolean).some(v => String(v).toLowerCase().includes(q));
                const matchesSeverity = incidentFilters.severity === "ALL" || inc.severity === incidentFilters.severity;
                const matchesStatus = incidentFilters.status === "ALL" || inc.status === incidentFilters.status;
                const matchesResolution = !incidentFilters.unresolvedOnly || !["RESOLVED", "FALSE_POSITIVE"].includes(inc.status);
                return matchesQuery && matchesSeverity && matchesStatus && matchesResolution;
            });
        });

        const attendanceEntries = computed(() => Array.isArray(attendance.entries) ? attendance.entries : []);
        const attendanceActiveCount = computed(() => Number.isFinite(attendance.activeCount) ? attendance.activeCount : 0);
        const attendanceClockOutCount = computed(() => Number.isFinite(attendance.clockOutCount) ? attendance.clockOutCount : 0);
        const attendanceLastSyncText = computed(() => attendance.lastSyncText || "SYNCING...");
        const attendanceIsOffline = computed(() => !!attendance.isOffline);
        const attendanceDisplayEntries = computed(() => attendanceEntries.value);
        const sysLogDisplay = computed(() => sysLogs.value.slice(0, 12));

        const analystQueue = computed(() => {
            const actionable = allIncidents.value.filter(i => !["RESOLVED", "FALSE_POSITIVE"].includes(i.status));
            const prioritySort = (a, b) => getIncidentPriorityScore(b) - getIncidentPriorityScore(a);
            return {
                unassigned: actionable.filter(i => !i.owner).sort(prioritySort).slice(0, 3),
                inProgress: actionable.filter(i => ["TRIAGED", "IN_PROGRESS"].includes(i.status)).sort(prioritySort).slice(0, 3),
                escalated: actionable.filter(i => i.status === "ESCALATED").sort(prioritySort).slice(0, 3)
            };
        });

        const healthSources = computed(() => ([
            {
                key: "elastic",
                label: "Elastic Feed",
                state: socHealth.elasticHealthy ? "UP" : "DOWN",
                meta: `Last success ${formatRelativeTime(socHealth.lastElasticSuccessUtc)}`,
                cssClass: socHealth.elasticHealthy ? "source-chip-ok" : "source-chip-down"
            },
            {
                key: "eps",
                label: "Event Rate",
                state: socHealth.epsHealthy ? "STREAMING" : "DEGRADED",
                meta: `Last success ${formatRelativeTime(socHealth.lastEpsSuccessUtc)}`,
                cssClass: socHealth.epsHealthy ? "source-chip-ok" : "source-chip-down"
            },
            {
                key: "webhook",
                label: "Incident Ingest",
                state: socHealth.threatWebhookHealthy ? "EXTERNAL" : "DISABLED",
                meta: `Last activity ${formatRelativeTime(socHealth.lastWebhookSuccessUtc)}`,
                cssClass: socHealth.threatWebhookHealthy ? "source-chip-ok" : "source-chip-warn"
            },
            {
                key: "broadcast",
                label: "Live Broadcast",
                state: socHealth.feedHealthy ? "SYNCED" : "STALE",
                meta: `Last feed ${formatRelativeTime(socHealth.lastBroadcastUtc)}`,
                cssClass: socHealth.feedHealthy ? "source-chip-ok" : "source-chip-warn"
            }
        ]));

        const healthPriority = (cssClass) => ({ "source-chip-down": 0, "source-chip-warn": 1, "source-chip-ok": 2 }[cssClass] ?? 3);
        const sortedHealthSources = computed(() =>
            [...healthSources.value].sort((a, b) => {
                const rankDiff = healthPriority(a.cssClass) - healthPriority(b.cssClass);
                return rankDiff !== 0 ? rankDiff : a.label.localeCompare(b.label);
            })
        );

        const dashboardFeed = computed(() =>
            incidentsRuntime
                ? incidentsRuntime.buildDashboardFeed(filteredRecentIncidents.value)
                : [...filteredRecentIncidents.value]
                    .sort((a, b) => {
                        const priorityDiff = getIncidentPriorityScore(b) - getIncidentPriorityScore(a);
                        if (priorityDiff !== 0) return priorityDiff;
                        return new Date(b.date).getTime() - new Date(a.date).getTime();
                    })
                    .slice(0, 8)
        );

        watch(() => soc.openTickets, (newVal) => {
            let start = odometerTickets.value;
            const duration = 1000;
            const startTime = performance.now();
            const animateCount = (time) => {
                const progress = Math.min((time - startTime) / duration, 1);
                odometerTickets.value = start + (newVal - start) * progress;
                if (progress < 1) requestAnimationFrame(animateCount);
            };
            requestAnimationFrame(animateCount);
        });

        const syncDashboardMode = () => {
            document.body.classList.toggle("soc-dashboard-mode", viewMode.value === "dashboard");
        };

        watch(viewMode, (mode) => {
            localStorage.setItem("SOC_DASH_VIEW_MODE", mode);
            syncDashboardMode();
        });

        let terminalConnection = null;

        const initSignalRTerminal = async () => {
            terminalConnection = new signalR.HubConnectionBuilder()
                .withUrl("/threatHub")
                .withAutomaticReconnect()
                .build();

            const addLog = (msg) => {
                const t = new Date();
                const timeStr = `${t.toLocaleTimeString("id-ID")}::${String(t.getMilliseconds()).padStart(3, "0")}`;
                sysLogs.value.unshift(`[${timeStr}] ${msg}`);
                if (sysLogs.value.length > 25) sysLogs.value.pop();
            };

            addLog("<span style='color:var(--yellow)'>[SYS] INITIALIZING ELASTIC STREAM...</span>");
            const processedLogs = new Set();

            terminalConnection.on("ReceiveThreats", (data) => {
                data.forEach(t => {
                    const logSignature = `${t.ip}_${t.count}`;
                    if (!processedLogs.has(logSignature)) {
                        processedLogs.add(logSignature);
                        if (processedLogs.size > 100) {
                            const firstItem = processedLogs.values().next().value;
                            processedLogs.delete(firstItem);
                        }

                        const ip = t.ip || "UNKNOWN_IP";
                        const country = t.country || "UNK";
                        let type = t.type;
                        if (!type) {
                            if (t.count > 500) type = "DDOS";
                            else if (t.count > 200) type = "BRUTEFORCE";
                            else type = "SUSPICIOUS";
                        }

                        const color = t.count > 200 ? "var(--red)" : "var(--yellow)";
                        addLog(`<span style='color:#fff'>INBOUND FROM ${ip} (${country})</span> ..... <span style='color:${color}'>${type} DETECTED</span>`);
                    }
                });
            });

            terminalConnection.onreconnecting(() => {
                isTerminalLive.value = false;
                addLog("<span style='color:var(--yellow)'>[SYS] CONNECTION LOST. RECONNECTING...</span>");
            });

            terminalConnection.onreconnected(() => {
                isTerminalLive.value = true;
                addLog("<span style='color:var(--green)'>[SYS] SOCKET RECONNECTED.</span>");
            });

            try {
                await terminalConnection.start();
                isTerminalLive.value = true;
                addLog("<span style='color:var(--green)'>[SYS] REAL-TIME THREAT STREAM ACTIVE.</span>");
            } catch (e) {
                isTerminalLive.value = false;
                addLog("<span style='color:var(--red)'>[SYS] CONNECTION FAILED. RETRYING...</span>");
            }
        };

        const openIncidentDetail = (inc) => { incidentModal.data = inc; incidentModal.show = true; };
        const closeIncidentDetail = () => { incidentModal.show = false; incidentModal.data = null; };
        const isolateAttacker = (ip) => {
            closeIncidentDetail();
            Toastify({ text: `INITIATING FIREWALL BLOCK FOR ${ip}...`, duration: 2000, style: { background: "linear-gradient(to right, #ff5f6d, #ffc371)", color: "#000", fontFamily: "Orbitron" } }).showToast();
            setTimeout(() => { Toastify({ text: `IP ${ip} SUCCESSFULLY ISOLATED`, duration: 3000, style: { background: "#111", border: "1px solid var(--cyan)", color: "var(--cyan)", fontFamily: "Orbitron" } }).showToast(); }, 2000);
        };

        const initAudioContext = () => { if (audioCtx.state === "suspended") audioCtx.resume(); };
        const initIncidentAudio = () => {
            if (!incidentAudio) {
                incidentAudio = new Audio("/assets/sounds/alarm.mp3");
                incidentAudio.preload = "auto";
                incidentAudio.volume = 0.45;
            }

            return incidentAudio;
        };
        const playTone = (f, s, d, t = "sine", v = 0.1) => { const n = audioCtx.currentTime; const o = audioCtx.createOscillator(); const g = audioCtx.createGain(); o.type = t; o.frequency.setValueAtTime(f, n + s); g.gain.setValueAtTime(0, n + s); g.gain.linearRampToValueAtTime(v, n + s + 0.05); g.gain.exponentialRampToValueAtTime(0.001, n + s + d); o.connect(g); g.connect(audioCtx.destination); o.start(n + s); o.stop(n + s + d); };
        const playAlertSound = (t) => { initAudioContext(); if (t === "START") { [261.6, 329.6, 392, 493.8].forEach((f, i) => playTone(f, i * 0.1, 0.2, "triangle")); playTone(523.2, 0.4, 0.8, "square", 0.05); } else if (t === "END") { [880, 659.2, 523.2].forEach((f, i) => playTone(f, i * 0.3, 0.6)); playTone(392, 0.9, 1.5); } else if (t === "PRAYER") { playTone(659.25, 0, 1.5, "sine", 0.1); playTone(523.25, 0.6, 2.0, "sine", 0.1); const s = 2.5; [164.81, 329.63, 349.23, 415.30, 329.63].forEach((f, i) => playTone(f, s + (i < 2 ? 0 : i < 3 ? 4 : i < 4 ? 5.5 : 8.5), i < 2 ? 4 : i < 3 ? 1.5 : i < 4 ? 3 : 5, "triangle", 0.15)); } };
        const playIncidentAlert = async () => {
            try {
                initAudioContext();
                const audio = initIncidentAudio();
                audio.currentTime = 0;
                await audio.play();
            } catch (e) {
            }
        };

        const fW = new Intl.DateTimeFormat("id-ID", { timeZone: "Asia/Jakarta", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fWa = new Intl.DateTimeFormat("id-ID", { timeZone: "Asia/Makassar", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fWt = new Intl.DateTimeFormat("id-ID", { timeZone: "Asia/Jayapura", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fU = new Intl.DateTimeFormat("id-ID", { timeZone: "UTC", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fH = new Intl.DateTimeFormat("id-ID-u-ca-islamic", { day: "numeric", month: "long", year: "numeric" });
        let lastSec = -1;
        let clockTimer = null;

        const formatDurationFromSeconds = (totalSeconds) => {
            const safeSeconds = Math.max(Number.isFinite(totalSeconds) ? Math.floor(totalSeconds) : 0, 0);
            const hours = String(Math.floor(safeSeconds / 3600)).padStart(2, "0");
            const minutes = String(Math.floor((safeSeconds % 3600) / 60)).padStart(2, "0");
            const seconds = String(safeSeconds % 60).padStart(2, "0");
            return `${hours}:${minutes}:${seconds}`;
        };

        const parseDurationToSeconds = (value) => {
            if (typeof value === "number" && Number.isFinite(value)) return value;
            if (typeof value !== "string") return null;

            const trimmed = value.trim();
            if (!trimmed) return null;
            if (/^\d+$/.test(trimmed)) return Number(trimmed);

            const hhmmss = trimmed.match(/(?:(\d+):)?(\d{1,2}):(\d{2})$/);
            if (hhmmss) {
                return (Number(hhmmss[1] || 0) * 3600) + (Number(hhmmss[2] || 0) * 60) + Number(hhmmss[3] || 0);
            }

            const verbose = trimmed.match(/(?:(\d+)\s*h(?:ours?)?)?\s*(?:(\d+)\s*m(?:in(?:utes?)?)?)?\s*(?:(\d+)\s*s(?:ec(?:onds?)?)?)?/i);
            if (verbose && (verbose[1] || verbose[2] || verbose[3])) {
                return (Number(verbose[1] || 0) * 3600) + (Number(verbose[2] || 0) * 60) + Number(verbose[3] || 0);
            }

            return null;
        };

        const parseDateValue = (value) => {
            if (!value) return null;
            const dateValue = new Date(value);
            return Number.isNaN(dateValue.getTime()) ? null : dateValue;
        };

        const formatAttendanceTime = (value) => {
            const parsed = parseDateValue(value);
            if (!parsed) return "--:--";
            return `${parsed.toLocaleTimeString("id-ID", { timeZone: "Asia/Jakarta", hour: "2-digit", minute: "2-digit", hour12: false })} WIB`;
        };

        const isWithinWorkingHours = (now = new Date()) => {
            const shiftStart = new Date(now);
            shiftStart.setHours(CONFIG.START, 0, 0, 0);
            const shiftEnd = new Date(now);
            shiftEnd.setHours(CONFIG.END, now.getDay() === 5 ? CONFIG.FRIDAY_END_MINUTE : 0, 0, 0);
            return now >= shiftStart && now < shiftEnd;
        };

        const extractName = (source) => {
            if (!source || typeof source !== "object") return null;
            const candidates = ["name", "employeeName", "userName", "deviceName", "title", "label"];
            for (const key of candidates) {
                const value = source[key];
                if (typeof value === "string" && value.trim()) return value.trim();
            }
            return null;
        };

        const extractDurationSeconds = (source) => {
            if (!source || typeof source !== "object") return null;
            const keys = ["duration", "durationText", "duration_time", "durationTime", "elapsed", "elapsedTime", "uptime", "seconds", "durationSeconds", "duration_seconds"];
            for (const key of keys) {
                const seconds = parseDurationToSeconds(source[key]);
                if (seconds !== null) return seconds;
            }

            const startAt = parseDateValue(source.startAt || source.startedAt || source.clockAt || source.clockedAt || source.checkInAt || source.checkOutAt || source.timestamp || source.time);
            if (startAt) return Math.max(Math.floor((Date.now() - startAt.getTime()) / 1000), 0);
            return null;
        };

        const isTruthyStatus = (value) => {
            if (typeof value === "boolean") return value;
            if (typeof value === "string") {
                const normalized = value.trim().toLowerCase();
                return !["offline", "off", "inactive", "false", "0", "selesai", "done"].includes(normalized);
            }
            if (typeof value === "number") return value !== 0;
            return null;
        };

        const findAttendanceCandidate = (payload, type) => {
            if (!payload || typeof payload !== "object") return null;

            const keyMatchers = type === "clockIn"
                ? ["clockin", "clock_in", "checkin", "check_in", "clockinstatus", "checkinstatus"]
                : ["clockout", "clock_out", "checkout", "check_out", "clockoutstatus", "checkoutstatus"];

            const queue = [payload];
            while (queue.length) {
                const current = queue.shift();
                if (!current || typeof current !== "object") continue;

                if (Array.isArray(current)) {
                    current.forEach(item => queue.push(item));
                    continue;
                }

                for (const [key, value] of Object.entries(current)) {
                    const normalizedKey = key.replace(/[\s-]/g, "").toLowerCase();
                    if (keyMatchers.some(matcher => normalizedKey === matcher || normalizedKey.startsWith(matcher) || normalizedKey.endsWith(matcher))) {
                        if (value && typeof value === "object") return value;
                    }
                    if (value && typeof value === "object") queue.push(value);
                }
            }

            return null;
        };

        const normalizeAttendanceEntries = (payload) => {
            const items = Array.isArray(payload?.data?.items) ? payload.data.items : (Array.isArray(payload?.items) ? payload.items : []);

            const entries = items
                .filter(item => item && typeof item === "object" && String(item.status || "").toUpperCase() !== "ERROR")
                .map(item => {
                    const clockInTime = parseDateValue(item.clockInTime);
                    const clockOutTime = parseDateValue(item.clockOutTime);
                    if (!clockInTime && !clockOutTime) return null;

                    const isClockOut = !!clockOutTime;
                    const durationSeconds = extractDurationSeconds(item) ?? 0;
                    const statusText = String(item.status || "").toUpperCase();
                    const isRunning = !isClockOut && statusText === "OPENED";

                    return {
                        key: item.appUserId || item.bitrixUserId || item.name,
                        name: item.name || "UNKNOWN",
                        statusLabel: isClockOut ? "CLOCK OUT" : "CLOCK IN",
                        status: statusText,
                        clockInTime: item.clockInTime || null,
                        clockOutTime: item.clockOutTime || null,
                        durationText: formatDurationFromSeconds(durationSeconds),
                        baseDurationSeconds: durationSeconds,
                        syncStartedAtMs: Date.now(),
                        isRunning,
                        isClockOut,
                        primaryTime: isClockOut ? clockOutTime : clockInTime
                    };
                })
                .filter(Boolean)
                .sort((a, b) => {
                    if (a.isClockOut !== b.isClockOut) return a.isClockOut ? 1 : -1;
                    if (b.baseDurationSeconds !== a.baseDurationSeconds) return b.baseDurationSeconds - a.baseDurationSeconds;
                    return (b.primaryTime?.getTime() || 0) - (a.primaryTime?.getTime() || 0);
                });

            return {
                entries,
                activeCount: entries.filter(entry => !entry.isClockOut).length,
                clockOutCount: entries.filter(entry => entry.isClockOut).length
            };
        };

        const applyAttendanceSummary = (summary) => {
            attendance.entries = summary.entries;
            attendance.activeCount = summary.activeCount;
            attendance.clockOutCount = summary.clockOutCount;
            attendance.isOffline = false;
        };

        const buildAttendanceSnapshot = (entries) => {
            const snapshot = new Map();
            (Array.isArray(entries) ? entries : []).forEach(entry => {
                if (!entry || !entry.key) return;
                snapshot.set(String(entry.key), {
                    status: entry.status,
                    clockInTime: entry.clockInTime || null,
                    clockOutTime: entry.clockOutTime || null,
                    isClockOut: !!entry.isClockOut
                });
            });
            return snapshot;
        };

        const clearAttendanceToast = () => {
            attendanceToastTimers.forEach(clearTimeout);
            attendanceToastTimers = [];
            if (attendanceToastNode) {
                attendanceToastNode.remove();
                attendanceToastNode = null;
            }
        };

        const playAttendanceChime = async () => {
            try {
                initAudioContext();
                if (audioCtx.state === "suspended") await audioCtx.resume();

                const now = audioCtx.currentTime;
                const notes = [
                    { freq: 523.25, offset: 0, duration: 0.14, volume: 0.028 },
                    { freq: 659.25, offset: 0.12, duration: 0.16, volume: 0.024 },
                    { freq: 783.99, offset: 0.24, duration: 0.18, volume: 0.02 }
                ];

                notes.forEach(({ freq, offset, duration, volume }) => {
                    const osc = audioCtx.createOscillator();
                    const gain = audioCtx.createGain();
                    osc.type = "sine";
                    osc.frequency.setValueAtTime(freq, now + offset);
                    gain.gain.setValueAtTime(0.0001, now + offset);
                    gain.gain.linearRampToValueAtTime(volume, now + offset + 0.02);
                    gain.gain.exponentialRampToValueAtTime(0.0001, now + offset + duration);
                    osc.connect(gain);
                    gain.connect(audioCtx.destination);
                    osc.start(now + offset);
                    osc.stop(now + offset + duration + 0.05);
                });
            } catch (e) {
            }
        };

        const showAttendanceToast = ({ title, meta, accent = "var(--cyan)" }) => {
            clearAttendanceToast();

            const layer = document.createElement("div");
            layer.className = "attendance-toast-layer";

            const card = document.createElement("div");
            card.className = "attendance-toast-card";
            card.style.borderColor = accent;

            const marker = document.createElement("div");
            marker.className = "attendance-toast-marker";
            marker.style.background = accent;
            marker.style.boxShadow = `0 0 16px ${accent}`;

            const content = document.createElement("div");
            content.className = "attendance-toast-content";

            const titleEl = document.createElement("div");
            titleEl.className = "attendance-toast-title";
            titleEl.textContent = title;

            const metaEl = document.createElement("div");
            metaEl.className = "attendance-toast-meta";
            metaEl.textContent = meta;

            content.appendChild(titleEl);
            content.appendChild(metaEl);
            card.appendChild(marker);
            card.appendChild(content);
            layer.appendChild(card);
            document.body.appendChild(layer);
            attendanceToastNode = layer;

            requestAnimationFrame(() => {
                card.classList.add("show");
            });

            attendanceToastTimers.push(setTimeout(() => {
                card.classList.add("hide");
            }, 4200));

            attendanceToastTimers.push(setTimeout(() => {
                clearAttendanceToast();
            }, 4900));
        };

        const notifyAttendanceChange = (previousMap, entries) => {
            if (!isWithinWorkingHours()) return;

            const currentMap = buildAttendanceSnapshot(entries);
            const changes = [];

            currentMap.forEach((current, key) => {
                const previous = previousMap.get(key);
                if (!previous) {
                    if (current.isClockOut && current.clockOutTime) {
                        changes.push({
                            type: "CLOCK_OUT",
                            key,
                            name: entries.find(entry => String(entry.key) === String(key))?.name || key,
                            clockInTime: current.clockInTime,
                            clockOutTime: current.clockOutTime
                        });
                    } else if (current.status === "OPENED" && current.clockInTime) {
                        changes.push({
                            type: "CLOCK_IN",
                            key,
                            name: entries.find(entry => String(entry.key) === String(key))?.name || key,
                            clockInTime: current.clockInTime,
                            clockOutTime: current.clockOutTime
                        });
                    }
                    return;
                }

                const becameClockOut = !previous.isClockOut && current.isClockOut && current.clockOutTime;
                const becameOpen = previous.status !== "OPENED" && current.status === "OPENED" && current.clockInTime;

                if (becameClockOut) {
                    changes.push({
                        type: "CLOCK_OUT",
                        key,
                        name: entries.find(entry => String(entry.key) === String(key))?.name || key,
                        clockInTime: current.clockInTime || previous.clockInTime,
                        clockOutTime: current.clockOutTime
                    });
                } else if (becameOpen) {
                    changes.push({
                        type: "CLOCK_IN",
                        key,
                        name: entries.find(entry => String(entry.key) === String(key))?.name || key,
                        clockInTime: current.clockInTime,
                        clockOutTime: current.clockOutTime
                    });
                }
            });

            if (changes.length === 0) return;

            const latest = changes
                .sort((a, b) => {
                    const aTime = parseDateValue(a.clockOutTime || a.clockInTime)?.getTime() || 0;
                    const bTime = parseDateValue(b.clockOutTime || b.clockInTime)?.getTime() || 0;
                    return bTime - aTime;
                })[0];
            const headlineName = latest.name || "UNKNOWN";

            if (latest.type === "CLOCK_IN") {
                showAttendanceToast({
                    title: `${headlineName} clock in`,
                    meta: `jam ${formatAttendanceTime(latest.clockInTime)}`,
                    accent: "var(--cyan)"
                });
            } else {
                showAttendanceToast({
                    title: `${headlineName} clock out`,
                    meta: `clock in jam ${formatAttendanceTime(latest.clockInTime)}`,
                    accent: "var(--red)"
                });
            }

            playAttendanceChime();
        };

        const tickAttendanceDurations = () => {
            attendance.entries.forEach(item => {
                const elapsedSinceSync = item.isRunning ? Math.max(Math.floor((Date.now() - item.syncStartedAtMs) / 1000), 0) : 0;
                item.durationText = formatDurationFromSeconds(item.baseDurationSeconds + elapsedSinceSync);
            });
        };

        const updateClockState = () => {
            const now = new Date();
            const ms = now.getMilliseconds();
            const s = now.getSeconds();
            const m = now.getMinutes();
            const h = now.getHours();

            clockHands.s = (s + (ms / 1000)) * 6;
            clockHands.m = (m * 6) + (s * 0.1);
            clockHands.h = ((h % 12) * 30) + (m * 0.5);

            lastSec = s;
            clocks.wib = fW.format(now).replace(/\./g, ":");
            clocks.wita = fWa.format(now).replace(/\./g, ":");
            clocks.wit = fWt.format(now).replace(/\./g, ":");
            clocks.utc = fU.format(now).replace(/\./g, ":");

            const shiftStart = new Date(now);
            shiftStart.setHours(CONFIG.START, 0, 0, 0);
            const shiftEnd = new Date(now);
            shiftEnd.setHours(CONFIG.END, now.getDay() === 5 ? CONFIG.FRIDAY_END_MINUTE : 0, 0, 0);
            const isWithinShift = now >= shiftStart && now < shiftEnd;
            clocks.wibStyle = { color: isWithinShift ? "var(--cyan)" : "var(--red)", textShadow: isWithinShift ? "0 0 20px rgba(0, 255, 204, 0.2)" : "0 0 20px rgba(255, 51, 102, 0.4)" };

            const dy = ["MINGGU", "SENIN", "SELASA", "RABU", "KAMIS", "JUMAT", "SABTU"];
            const mt = ["JAN", "FEB", "MAR", "APR", "MEI", "JUN", "JUL", "AGU", "SEP", "OKT", "NOV", "DES"];
            date.dayName = dy[now.getDay()];
            date.fullDate = `${String(now.getDate()).padStart(2, "0")} ${mt[now.getMonth()]} ${now.getFullYear()}`;
            date.hijri = fH.format(now);

            const endHour = shiftEnd.getHours();
            const endMinute = shiftEnd.getMinutes();
            if (h === CONFIG.START && m === 0 && s < 2 && !flags.shiftAlerted) { triggerModal("START"); flags.shiftAlerted = true; }
            else if (h === endHour && m === endMinute && s < 2 && !flags.shiftAlerted) { triggerModal("END"); flags.shiftAlerted = true; }
            else if (s > 5) flags.shiftAlerted = false;

            tickAttendanceDurations();
            updatePrayerData(now, h, m, s);
        };

        const startClockLoop = () => {
            if (clockTimer) clearTimeout(clockTimer);
            const tick = () => {
                updateClockState();
                const delay = 1000 - (Date.now() % 1000);
                clockTimer = setTimeout(tick, Math.max(delay, 50));
            };

            updateClockState();
            const delay = 1000 - (Date.now() % 1000);
            clockTimer = setTimeout(tick, Math.max(delay, 50));
        };

        const updatePrayerData = (n, h, m, s) => {
            const cT = `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`; const cM = (h * 60) + m; let fN = null; let mD = Infinity;
            Object.entries(prayerTimes.value).forEach(([pn, pt]) => {
                if (pt === cT && s < 2 && !flags.prayerAlerted) { const im = imamPool.value.length ? imamPool.value[Math.floor(Math.random() * imamPool.value.length)] : "TBA"; triggerModal("PRAYER", { name: pn, imam: im }); flags.prayerAlerted = true; }
                const [ph, pm] = pt.split(":").map(Number); const pms = (ph * 60) + pm;
                if (pms > cM && (pms - cM) < mD) { mD = pms - cM; fN = { pn, pt }; }
            });
            if (s > 5) flags.prayerAlerted = false;
            if (fN) { nextPrayer.name = fN.pn.toUpperCase(); nextPrayer.time = fN.pt; const hl = Math.floor(mD / 60), ml = mD % 60; nextPrayer.countdown = `-${String(hl).padStart(2, "0")}:${String(ml).padStart(2, "0")}`; nextPrayer.isNear = (hl === 0 && ml < 10); }
            else { nextPrayer.name = "FAJR"; nextPrayer.time = prayerTimes.value.Fajr || "--:--"; nextPrayer.countdown = ""; nextPrayer.isNear = false; }
        };

        const fetchGeoData = async () => {
            fetch("/api/imam-list", { headers: { "X-API-KEY": API_KEY } }).then(r => r.json()).then(d => imamPool.value = d).catch(() => { });
            if (navigator.geolocation) navigator.geolocation.getCurrentPosition((pos) => { getApis(pos.coords.latitude, pos.coords.longitude); }, () => { getApis(CONFIG.DEF_LAT, CONFIG.DEF_LON); });
            else getApis(CONFIG.DEF_LAT, CONFIG.DEF_LON);
        };

        const getApis = async (lat, lon) => {
            const d = new Date();
            fetch(`https://api.aladhan.com/v1/timings/${d.getDate()}-${d.getMonth() + 1}-${d.getFullYear()}?latitude=${lat}&longitude=${lon}&method=20`)
                .then(r => r.json())
                .then(res => {
                    if (res.data && res.data.timings) {
                        const t = res.data.timings;
                        prayerTimes.value = { Fajr: t.Fajr, Dhuhr: t.Dhuhr, Asr: t.Asr, Maghrib: t.Maghrib, Isha: t.Isha };
                    }
                }).catch(() => console.warn("API Sholat offline"));

            fetch(`https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}&current_weather=true&timezone=auto`)
                .then(r => r.json())
                .then(res => {
                    if (res.current_weather) {
                        const w = res.current_weather; weather.temp = `${Math.round(w.temperature)}°`;
                        const cd = { 0: { t: "CLEAR SKY", i: '<div class="sun"></div>' }, 1: { t: "CLOUDY", i: '<div class="cloud"></div>' }, 51: { t: "RAIN", i: '<div class="rain-cloud"></div><div class="rain-drop"></div>' } };
                        const mp = cd[w.weathercode === 0 ? 0 : (w.weathercode <= 3 ? 1 : 51)];
                        weather.desc = mp.t; weather.icon = mp.i; weather.isBad = (w.weathercode >= 51);
                    }
                }).catch(() => weather.desc = "OFFLINE");

            fetch(`https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=${lat}&longitude=${lon}&localityLanguage=id`)
                .then(r => r.json())
                .then(res => {
                    locationName.value = (res.locality && res.city ? `${res.locality}, ${res.city}` : res.city || "UNKNOWN").toUpperCase();
                }).catch(() => locationName.value = "OFFLINE");
        };

        const validateSession = async () => {
            if (!window.API_KEY) {
                if (window.CSIRT_AUTH) window.CSIRT_AUTH.logout();
                return false;
            }

            try {
                const res = await fetch("/api/auth/validate", { headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                authChecked.value = true;
                return true;
            } catch (e) {
                if (window.CSIRT_AUTH) window.CSIRT_AUTH.logout();
                return false;
            }
        };

        const fetchSocData = async () => {
            try {
                const res = await fetch("/api/incidents/summary", { headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                const data = await res.json();
                soc.openTickets = data.openTickets || 0;
                soc.criticalOpenTickets = data.criticalOpenTickets || 0;
                soc.highOpenTickets = data.highOpenTickets || 0;
                soc.defconLevel = data.defconLevel || "DEFCON 5";
                soc.defconDesc = data.defconDesc || "NORMAL OPERATIONS";
                soc.defconColor = data.defconColor || "var(--green)";
                soc.defconShadow = data.defconShadow || "none";
                soc.topAttacker = data.topAttacker || "---";
                soc.topAttackerPct = data.topAttackerPct || 0;
                soc.recentIncidents = data.recentIncidents || [];
                soc.statusCounts = data.statusCounts || {};
                const incidentTrend = data.incidentTrend || new Array(12).fill(0);
                if (chartsRuntime) chartsRuntime.updateIncidents(incidentTrend);
                else chartInstance?.updateSeries([{ data: incidentTrend }]);
            } catch (e) {
            } finally {
                isLoadingData.value = false;
            }
        };

        const fetchAllIncidents = async () => {
            try {
                const res = await fetch("/api/incidents", { headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                const incidents = await res.json();
                allIncidents.value = incidents;
                processIncomingIncidentAlerts(incidents);
                hydrateHandover();
            } catch (e) {
            }
        };
        const processIncomingIncidentAlerts = (incidents) => {
            if (incidentsRuntime) {
                incidentsRuntime.processIncomingIncidentAlerts({
                    incidents,
                    knownIds: incidentTracker,
                    onAlert: triggerIncidentAlert
                });
                return;
            }

            const nextIds = new Set(incidents.filter(i => i?.id).map(i => i.id));
            if (!incidentTracker.hydrated) {
                nextIds.forEach(id => incidentTracker.ids.add(id));
                incidentTracker.hydrated = true;
                return;
            }

            const freshIncidents = incidents
                .filter(inc => inc?.id && !incidentTracker.ids.has(inc.id))
                .filter(inc => {
                    const ts = new Date(inc.date).getTime();
                    return Number.isFinite(ts) && (Date.now() - ts) <= incidentAlertFreshWindowMs;
                })
                .sort((a, b) => new Date(b.date).getTime() - new Date(a.date).getTime());

            nextIds.forEach(id => incidentTracker.ids.add(id));
            if (freshIncidents.length === 0)
                return;

            const now = Date.now();
            if ((now - incidentTracker.lastAlertAt) < incidentAlertCooldownMs)
                return;

            incidentTracker.lastAlertAt = now;
            triggerIncidentAlert(freshIncidents);
        };
        const triggerIncidentAlert = (incidents) => {
            const [latest] = incidents;
            incidentAlert.show = true;
            incidentAlert.count = incidents.length;
            incidentAlert.severity = latest.severity || "MEDIUM";
            incidentAlert.title = incidents.length === 1
                ? `INCIDENT BARU: ${latest.title || latest.id || "UNIDENTIFIED"}`
                : `${incidents.length} INCIDENT BARU MASUK`;
            incidentAlert.message = incidents.length === 1
                ? `${latest.attacker || "UNKNOWN"} -> ${latest.affectedAsset || latest.source || "UNSPECIFIED TARGET"}`
                : `${latest.attacker || "UNKNOWN"} dan ${incidents.length - 1} event lain menunggu triage`;

            playIncidentAlert();
            Toastify({
                text: incidents.length === 1
                    ? `Incident baru: ${latest.title || latest.id}`
                    : `${incidents.length} incident baru diterima`,
                duration: 5000,
                gravity: "top",
                position: "right",
                style: {
                    background: "linear-gradient(to right, #ff5f6d, #ffc371)",
                    color: "#000",
                    fontFamily: "Orbitron"
                }
            }).showToast();

            if (incidentAlertTimer)
                clearTimeout(incidentAlertTimer);

            incidentAlertTimer = setTimeout(() => {
                incidentAlert.show = false;
            }, 12000);
        };
        const closeIncidentAlert = () => {
            incidentAlert.show = false;
            if (incidentAlertTimer)
                clearTimeout(incidentAlertTimer);
        };

        const fetchHealth = async () => {
            try {
                const res = await fetch("/api/health", { headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                const data = await res.json();
                socHealth.feedHealthy = !!data.feedHealthy;
                socHealth.elasticHealthy = !!data.elasticHealthy;
                socHealth.epsHealthy = !!data.epsHealthy;
                socHealth.threatWebhookHealthy = !!data.threatWebhookHealthy;
                socHealth.lastBroadcastUtc = data.lastBroadcastUtc;
                socHealth.lastElasticSuccessUtc = data.lastElasticSuccessUtc;
                socHealth.lastWebhookSuccessUtc = data.lastWebhookSuccessUtc;
                socHealth.lastEpsSuccessUtc = data.lastEpsSuccessUtc;
            } catch (e) {
            }
        };

        const fetchAttendanceSummary = async () => {
            try {
                const res = await fetch("/api/attendance/summary", { headers: { "X-API-KEY": API_KEY } });
                const payload = await res.json();
                if (!res.ok) throw new Error(payload?.message || "Attendance sync failed");

                const normalized = attendanceRuntime
                    ? attendanceRuntime.sync(payload)
                    : normalizeAttendanceEntries(payload);
                applyAttendanceSummary(normalized);
                attendance.lastSyncText = `Last sync ${new Date().toLocaleTimeString("id-ID", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
            } catch (e) {
                attendance.entries = [];
                attendance.activeCount = 0;
                attendance.clockOutCount = 0;
                attendance.isOffline = true;
                if (attendanceRuntime) attendanceRuntime.clearToast();
                else clearAttendanceToast();
                attendance.lastSyncText = e?.message || "Attendance sync failed";
            }
        };

        const refreshVisibleData = () => {
            if (!isPageVisible()) return;
            fetchSocData();
            fetchAllIncidents();
            fetchHealth();
            fetchAttendanceSummary();
        };

        const fetchNews = () => { fetch("/api/news", { headers: { "X-API-KEY": API_KEY } }).then(r => r.json()).then(d => { if (d.length) newsFeed.value = d; }); };
        const escapeHtml = (text) => { if (!text) return ""; return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#039;"); };
        const formatTime = (d) => new Date(d).toLocaleTimeString("id-ID", { hour: "2-digit", minute: "2-digit" });
        const formatDateTime = (d) => new Date(d).toLocaleString("id-ID", { year: "numeric", month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit" });
        const formatRelativeTime = (d) => {
            if (!d) return "never";
            const diffMs = Date.now() - new Date(d).getTime();
            const diffMin = Math.max(Math.floor(diffMs / 60000), 0);
            if (diffMin < 1) return "just now";
            if (diffMin < 60) return `${diffMin}m ago`;
            return `${Math.floor(diffMin / 60)}h ago`;
        };
        const isPageVisible = () => document.visibilityState === "visible";
        const getSevClass = (s) => s === "CRITICAL" ? "sev-crt" : (s === "HIGH" ? "sev-hgh" : (s === "MEDIUM" ? "sev-med" : "sev-low"));
        const isFreshIncident = (inc) => incidentsRuntime ? incidentsRuntime.isFreshIncident(inc) : ((Date.now() - new Date(inc.date).getTime()) / 60000) <= 10;
        const getIncidentRowStyle = (inc) => incidentsRuntime ? incidentsRuntime.getIncidentRowStyle(inc) : (inc.severity === "CRITICAL" ? { background: "rgba(255,51,102,0.08)" } : (isFreshIncident(inc) ? { boxShadow: "inset 3px 0 0 var(--yellow)" } : {}));
        const getIncidentPriorityScore = (incident) => incidentsRuntime ? incidentsRuntime.getIncidentPriorityScore(incident) : (({ CRITICAL: 400, HIGH: 300, MEDIUM: 200, LOW: 100 }[incident.severity] || 50) + ({ ESCALATED: 70, IN_PROGRESS: 55, TRIAGED: 35, NEW: 20 }[incident.status] || 0));
        const hydrateHandover = () => {
            if (incidentsRuntime) {
                incidentsRuntime.hydrateHandover(allIncidents.value, handover);
                return;
            }

            const open = allIncidents.value.filter(i => !["RESOLVED", "FALSE_POSITIVE"].includes(i.status));
            const fourHoursAgo = Date.now() - (4 * 60 * 60 * 1000);
            const activeOwners = new Set(open.map(i => i.owner).filter(Boolean));
            handover.unassigned = open.filter(i => !i.owner).length;
            handover.escalated = open.filter(i => i.status === "ESCALATED").length;
            handover.newLast4h = open.filter(i => new Date(i.date).getTime() >= fourHoursAgo).length;
            handover.activeOwners = activeOwners.size;
            if (handover.escalated > 0) handover.note = `${handover.escalated} escalated case(s) require senior review before handover.`;
            else if (handover.unassigned > 0) handover.note = `${handover.unassigned} incident(s) are still unassigned and should be picked up next shift.`;
            else handover.note = "No blocked handover items. Queue is stable and fully triaged.";
        };
        const nextQuickStatus = (status) => incidentsRuntime ? incidentsRuntime.nextQuickStatus(status) : ({ NEW: "TRIAGED", TRIAGED: "IN_PROGRESS", IN_PROGRESS: "ESCALATED", ESCALATED: "RESOLVED", RESOLVED: "IN_PROGRESS", FALSE_POSITIVE: "TRIAGED" }[status] || "TRIAGED");
        const updateIncidentRecord = async (incident) => {
            const res = await fetch(`/api/incidents/${incident.id}`, {
                method: "PUT",
                headers: { "Content-Type": "application/json", "X-API-KEY": API_KEY },
                body: JSON.stringify(incident)
            });
            const data = await res.json();
            if (!res.ok) throw new Error(data.message || "Update failed");
            await fetchSocData();
            await fetchAllIncidents();
            const refreshed = allIncidents.value.find(i => i.id === incident.id);
            if (refreshed) incidentModal.data = refreshed;
        };
        const quickAssignIncident = async (incident) => {
            try {
                const updated = { ...incident, owner: incident.owner || analystIdentity };
                await updateIncidentRecord(updated);
                Toastify({ text: `Assigned to ${updated.owner}`, duration: 2500, style: { background: "#111", border: "1px solid var(--cyan)", color: "var(--cyan)", fontFamily: "Orbitron" } }).showToast();
            } catch (e) {
                Toastify({ text: e.message || "Assign failed", duration: 2500, style: { background: "linear-gradient(to right, #ff5f6d, #ffc371)", color: "#000", fontFamily: "Orbitron" } }).showToast();
            }
        };
        const quickStatusChange = async (incident, status) => {
            try {
                const res = await fetch(`/api/incidents/${incident.id}/status`, {
                    method: "PUT",
                    headers: { "Content-Type": "application/json", "X-API-KEY": API_KEY },
                    body: JSON.stringify(status)
                });
                const data = await res.json();
                if (!res.ok) throw new Error(data.message || "Status update failed");
                await fetchSocData();
                await fetchAllIncidents();
                const refreshed = allIncidents.value.find(i => i.id === incident.id);
                if (refreshed) incidentModal.data = refreshed;
                Toastify({ text: `Status -> ${status}`, duration: 2500, style: { background: "#111", border: "1px solid var(--cyan)", color: "var(--cyan)", fontFamily: "Orbitron" } }).showToast();
            } catch (e) {
                Toastify({ text: e.message || "Status update failed", duration: 2500, style: { background: "linear-gradient(to right, #ff5f6d, #ffc371)", color: "#000", fontFamily: "Orbitron" } }).showToast();
            }
        };
        const openArchiveIncident = (id) => {
            window.location.href = `/archive#${encodeURIComponent(id)}`;
        };
        const openArchiveList = () => {
            window.location.href = "/archive";
        };
        const setViewMode = (mode) => { viewMode.value = mode; };
        const toggleFullScreen = () => !document.fullscreenElement ? document.documentElement.requestFullscreen() : document.exitFullscreen();
        const animateJackpot = (fV) => { const p = imamPool.value.length > 0 ? imamPool.value : ["SEARCHING..."]; let c = 0; const ts = 90; const id = setInterval(() => { modal.imamText = p[Math.floor(Math.random() * p.length)]; modal.imamColor = (c % 2 === 0) ? "#fff" : "#aaa"; c++; if (c >= ts) { clearInterval(id); modal.imamText = fV; modal.imamColor = "var(--green)"; if (typeof confetti === "function") { confetti({ particleCount: 150, spread: 70, origin: { y: 0.6 }, colors: ["#00ff99", "#00ffff", "#ffffff"], zIndex: 10001 }); } } }, 30); };
        const triggerModal = (type, data = null) => { modal.show = true; modal.type = type; modal.progressWidth = "100%"; modal.transition = "none"; if (modalTimer) clearTimeout(modalTimer); setTimeout(() => { modal.progressWidth = "0%"; modal.transition = "width 60s linear"; }, 100); if (type === "START") { modal.title = "🚀 SYSTEM INITIALIZED"; modal.color = "var(--cyan)"; modal.shadow = "0 0 50px rgba(0,255,204,0.3)"; modal.msg = "DUTY CYCLE STARTED.<br>ALL SYSTEMS GREEN."; } else if (type === "END") { modal.title = "⚠️ DUTY CYCLE ENDED"; modal.color = "var(--yellow)"; modal.shadow = "0 0 50px rgba(255,204,0,0.3)"; modal.msg = "OPERATIONAL HOURS COMPLETE.<br>SECURE WORKSTATION."; } else if (type === "PRAYER") { modal.title = `🕌 PRAYER: ${data.name}`; modal.prayerName = data.name; modal.color = "var(--green)"; modal.shadow = "0 0 60px rgba(0,255,153,0.4)"; animateJackpot(data.imam); } playAlertSound(type); modalTimer = setTimeout(closeModal, 60000); };
        const closeModal = () => { modal.show = false; if (modalTimer) clearTimeout(modalTimer); };

        let intervals = [];
        onMounted(async () => {
            const sessionValid = await validateSession();
            if (!sessionValid) return;

            new Typed("#typed-output", { strings: ["BY ^200 <span style='color:var(--cyan)'>CYBERTEAM BAPPENAS</span>", "SYSTEM STATUS: ^500 <span style='color:var(--green)'>SECURE</span>", "MONITORING ACTIVE..."], typeSpeed: 30, backSpeed: 20, backDelay: 2000, loop: true, contentType: "html" });
            if (window.DashboardAttendanceModule) {
                attendanceRuntime = window.DashboardAttendanceModule.createAttendanceRuntime({
                    config: CONFIG,
                    audioCtx,
                    initAudioContext,
                    attendanceToastNode,
                    attendanceToastTimers,
                    lastAttendanceSnapshot
                });
            }
            if (window.DashboardClockModule) {
                clockRuntime = window.DashboardClockModule.createClockRuntime({
                    config: CONFIG,
                    clockHands,
                    clocks,
                    date,
                    fW,
                    fWa,
                    fWt,
                    fU,
                    fH,
                    flags,
                    triggerModal,
                    tickAttendanceDurations,
                    updatePrayerData
                });
            }
            if (window.DashboardIncidentsModule) {
                incidentsRuntime = window.DashboardIncidentsModule.createIncidentsRuntime({
                    freshWindowMs: incidentAlertFreshWindowMs,
                    alertCooldownMs: incidentAlertCooldownMs
                });
            }
            if (window.DashboardChartsModule) {
                chartsRuntime = window.DashboardChartsModule.createChartsRuntime({
                    chartRef,
                    epsChartRef,
                    epsDataSeries
                });
            }
            if (window.DashboardPollingModule) {
                pollingRefreshHandler = window.DashboardPollingModule.createPollingRuntime({
                    fetchSocData,
                    fetchAllIncidents,
                    fetchHealth,
                    fetchAttendanceSummary
                }).start();
            }
            chartInstance = chartsRuntime ? chartsRuntime.initIncidentChart() : new ApexCharts(chartRef.value, { series: [{ name: "Incidents", data: [0, 0, 0, 0, 0, 0, 0, 0] }], chart: { type: "area", height: 80, sparkline: { enabled: true } }, stroke: { curve: "smooth", width: 2 }, fill: { type: "gradient", gradient: { shadeIntensity: 1, opacityFrom: 0.7, opacityTo: 0.1, stops: [0, 90, 100] } }, colors: ["#f1c40f"] });
            if (!chartsRuntime) chartInstance.render();

            fetchGeoData(); fetchNews(); fetchSocData(); fetchAllIncidents(); fetchHealth(); fetchAttendanceSummary(); clockRuntime?.start();
            initSignalRTerminal();
            syncDashboardMode();

            intervals.push(setInterval(() => {
                const sysActions = [
                    "FW_RULE_SYNC ..... <span style='color:var(--green)'>OK</span>",
                    "ELASTIC_NODE_PING ..... <span style='color:var(--green)'>3ms</span>",
                    "MEM_USAGE_CHECK ..... <span style='color:var(--cyan)'>STABLE</span>",
                    "AUTH_GATEWAY_POLL ..... <span style='color:var(--green)'>SECURE</span>",
                    "N8N_WORKFLOW_SYNC ..... <span style='color:var(--cyan)'>IDLE</span>",
                    "DB_CLUSTER_HEALTH ..... <span style='color:var(--green)'>GREEN</span>"
                ];
                const randomAction = sysActions[Math.floor(Math.random() * sysActions.length)];
                const t = new Date();
                const timeStr = `${t.toLocaleTimeString("id-ID")}::${String(t.getMilliseconds()).padStart(3, "0")}`;

                sysLogs.value.unshift(`[${timeStr}] <span style='color:#666'>[SYS_HBT]</span> ${randomAction}`);
                if (sysLogs.value.length > 25) sysLogs.value.pop();
            }, 15000));

            epsChartInstance = chartsRuntime ? chartsRuntime.initEpsChart() : new ApexCharts(epsChartRef.value, {
                series: [{ name: "EPS", data: epsDataSeries.value }],
                chart: {
                    type: "bar", height: 60, sparkline: { enabled: true },
                    animations: { enabled: true, easing: "linear", dynamicAnimation: { speed: 1000 } }
                },
                plotOptions: { bar: { columnWidth: "70%", borderRadius: 2 } },
                colors: ["#00ffcc"],
                tooltip: { fixed: { enabled: false }, x: { show: false }, marker: { show: false } }
            });
            if (!chartsRuntime) epsChartInstance.render();

            intervals.push(setInterval(async () => {
                try {
                    if (!isPageVisible()) return;
                    const res = await fetch("/api/eps", { headers: { "X-API-KEY": API_KEY } });
                    if (res.ok) {
                        const data = await res.json();
                        let realEps = data.eventsPerSecond || 0;
                        const lastMinute = data.eventsLastMinute || 0;
                        currentEventsLastMinute.value = lastMinute.toLocaleString("id-ID");

                        if (realEps === 0) {
                            isRealTraffic.value = false;
                            const fakeEps = Math.floor(Math.random() * (25 - 12 + 1)) + 12;
                            currentEps.value = fakeEps.toString();
                            epsDataSeries.value.push(fakeEps);
                        } else {
                            isRealTraffic.value = true;
                            currentEps.value = realEps.toLocaleString("id-ID");
                            epsDataSeries.value.push(realEps);
                        }

                        if (epsDataSeries.value.length > 15) epsDataSeries.value.shift();
                        if (chartsRuntime) chartsRuntime.updateEps(epsDataSeries.value);
                        else epsChartInstance.updateSeries([{ data: epsDataSeries.value }]);
                    }
                } catch (e) {
                }
            }, 1500));

            intervals.push(setInterval(() => showBebek.value = !showBebek.value, 10000));
            intervals.push(setInterval(() => { if (isPageVisible()) fetchSocData(); }, 20000));
            intervals.push(setInterval(() => { if (isPageVisible()) fetchAllIncidents(); }, 30000));
            intervals.push(setInterval(() => { if (isPageVisible()) fetchHealth(); }, 30000));
            intervals.push(setInterval(() => { if (isPageVisible()) fetchAttendanceSummary(); }, attendancePollMs));
        });

        onUnmounted(() => {
            intervals.forEach(clearInterval);
            if (clockRuntime) clockRuntime.stop();
            if (chartsRuntime) chartsRuntime.stop();
            if (terminalConnection) terminalConnection.stop();
            if (incidentAlertTimer) clearTimeout(incidentAlertTimer);
            if (attendanceRuntime) attendanceRuntime.clearToast();
            else clearAttendanceToast();
            if (pollingRefreshHandler) document.removeEventListener("visibilitychange", pollingRefreshHandler);
            document.body.classList.remove("soc-dashboard-mode");
        });

        return { isTerminalLive, showBebek, epsChartRef, currentEps, currentEventsLastMinute, locationName, weather, clockHands, clocks, date, attendance, attendanceEntries, attendanceDisplayEntries, attendanceActiveCount, attendanceClockOutCount, attendanceLastSyncText, attendanceIsOffline, soc, socHealth, handover, analystQueue, healthSources, sortedHealthSources, dashboardFeed, nextPrayer, prayerTimes, modal, incidentAlert, newsFeed, chartRef, sysLogs, sysLogDisplay, isLoadingData, odometerTickets, incidentModal, incidentFilters, filteredRecentIncidents, allowedStatuses, allowedSeverities, viewMode, setViewMode, initAudioContext, formatTime, formatDateTime, formatRelativeTime, getSevClass, getIncidentRowStyle, isFreshIncident, nextQuickStatus, quickAssignIncident, quickStatusChange, openArchiveIncident, openArchiveList, toggleFullScreen, closeModal, closeIncidentAlert, escapeHtml, openIncidentDetail, closeIncidentDetail, isolateAttacker, authChecked };
    }
}).mount("#app");
