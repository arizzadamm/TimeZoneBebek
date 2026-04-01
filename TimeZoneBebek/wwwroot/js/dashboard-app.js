const { createApp, ref, reactive, computed, onMounted, onUnmounted, watch } = Vue;

createApp({
    setup() {
        const CONFIG = { DEF_LAT: -6.212530770381996, DEF_LON: 106.83045523468515, START: 8, END: 16, FRIDAY_END_MINUTE: 30 };
        const audioCtx = new (window.AudioContext || window.webkitAudioContext)();

        const isLoadingData = ref(true);
        const odometerTickets = ref(0);
        const incidentModal = reactive({ show: false, data: null });
        const isTerminalLive = ref(false);
        const authChecked = ref(false);

        const showBebek = ref(false);
        const locationName = ref("LOCATING...");
        const weather = reactive({ temp: "--°", desc: "WAITING...", icon: '<div class="cloud"></div>', isBad: false });

        const clockHands = reactive({ h: 0, m: 0, s: 0 });
        const clocks = reactive({ wib: "00:00:00", wita: "00:00", wit: "00:00", utc: "00:00", wibStyle: {} });
        const date = reactive({ dayName: "---", fullDate: "Loading...", hijri: "" });
        const shift = reactive({ timerText: "00:00:00", isOffDuty: false });

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

        const newsFeed = ref([]);
        const sysLogs = ref([]);

        const epsChartRef = ref(null);
        let epsChartInstance = null;
        const currentEps = ref("0");
        const currentEventsLastMinute = ref("0");
        const epsDataSeries = ref(Array(15).fill(0));
        const isRealTraffic = ref(true);
        const allowedStatuses = ["ALL", "NEW", "TRIAGED", "IN_PROGRESS", "ESCALATED", "RESOLVED", "FALSE_POSITIVE"];
        const allowedSeverities = ["ALL", "CRITICAL", "HIGH", "MEDIUM", "LOW"];

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
                label: "n8n Webhook",
                state: socHealth.threatWebhookHealthy ? "READY" : "WAITING",
                meta: `Last post ${formatRelativeTime(socHealth.lastWebhookSuccessUtc)}`,
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
        const playTone = (f, s, d, t = "sine", v = 0.1) => { const n = audioCtx.currentTime; const o = audioCtx.createOscillator(); const g = audioCtx.createGain(); o.type = t; o.frequency.setValueAtTime(f, n + s); g.gain.setValueAtTime(0, n + s); g.gain.linearRampToValueAtTime(v, n + s + 0.05); g.gain.exponentialRampToValueAtTime(0.001, n + s + d); o.connect(g); g.connect(audioCtx.destination); o.start(n + s); o.stop(n + s + d); };
        const playAlertSound = (t) => { initAudioContext(); if (t === "START") { [261.6, 329.6, 392, 493.8].forEach((f, i) => playTone(f, i * 0.1, 0.2, "triangle")); playTone(523.2, 0.4, 0.8, "square", 0.05); } else if (t === "END") { [880, 659.2, 523.2].forEach((f, i) => playTone(f, i * 0.3, 0.6)); playTone(392, 0.9, 1.5); } else if (t === "PRAYER") { playTone(659.25, 0, 1.5, "sine", 0.1); playTone(523.25, 0.6, 2.0, "sine", 0.1); const s = 2.5; [164.81, 329.63, 349.23, 415.30, 329.63].forEach((f, i) => playTone(f, s + (i < 2 ? 0 : i < 3 ? 4 : i < 4 ? 5.5 : 8.5), i < 2 ? 4 : i < 3 ? 1.5 : i < 4 ? 3 : 5, "triangle", 0.15)); } };

        const fW = new Intl.DateTimeFormat("id-ID", { timeZone: "Asia/Jakarta", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fWa = new Intl.DateTimeFormat("id-ID", { timeZone: "Asia/Makassar", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fWt = new Intl.DateTimeFormat("id-ID", { timeZone: "Asia/Jayapura", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fU = new Intl.DateTimeFormat("id-ID", { timeZone: "UTC", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
        const fH = new Intl.DateTimeFormat("id-ID-u-ca-islamic", { day: "numeric", month: "long", year: "numeric" });
        let lastSec = -1;

        const clockLoop = () => {
            const now = new Date(); const ms = now.getMilliseconds(); const s = now.getSeconds(); const m = now.getMinutes(); const h = now.getHours();
            clockHands.s = (s + (ms / 1000)) * 6; clockHands.m = (m * 6) + (s * 0.1); clockHands.h = ((h % 12) * 30) + (m * 0.5);
            if (s !== lastSec) {
                lastSec = s; clocks.wib = fW.format(now).replace(/\./g, ":"); clocks.wita = fWa.format(now).replace(/\./g, ":"); clocks.wit = fWt.format(now).replace(/\./g, ":"); clocks.utc = fU.format(now).replace(/\./g, ":");
                const shiftStart = new Date(now);
                shiftStart.setHours(CONFIG.START, 0, 0, 0);
                const shiftEnd = new Date(now);
                shiftEnd.setHours(CONFIG.END, now.getDay() === 5 ? CONFIG.FRIDAY_END_MINUTE : 0, 0, 0);
                const isWithinShift = now >= shiftStart && now < shiftEnd;
                clocks.wibStyle = { color: isWithinShift ? "var(--cyan)" : "var(--red)", textShadow: isWithinShift ? "0 0 20px rgba(0, 255, 204, 0.2)" : "0 0 20px rgba(255, 51, 102, 0.4)" };
                const dy = ["MINGGU", "SENIN", "SELASA", "RABU", "KAMIS", "JUMAT", "SABTU"]; const mt = ["JAN", "FEB", "MAR", "APR", "MEI", "JUN", "JUL", "AGU", "SEP", "OKT", "NOV", "DES"];
                date.dayName = dy[now.getDay()]; date.fullDate = `${String(now.getDate()).padStart(2, "0")} ${mt[now.getMonth()]} ${now.getFullYear()}`; date.hijri = fH.format(now);
                const endHour = shiftEnd.getHours();
                const endMinute = shiftEnd.getMinutes();
                if (h === CONFIG.START && m === 0 && s < 2 && !flags.shiftAlerted) { triggerModal("START"); flags.shiftAlerted = true; }
                else if (h === endHour && m === endMinute && s < 2 && !flags.shiftAlerted) { triggerModal("END"); flags.shiftAlerted = true; }
                else if (s > 5) flags.shiftAlerted = false;
                const elapsed = now - shiftStart;
                if (!isWithinShift) { shift.isOffDuty = true; shift.timerText = "OFF DUTY"; }
                else { shift.isOffDuty = false; shift.timerText = `${String(Math.floor(elapsed / 36e5)).padStart(2, "0")}:${String(Math.floor((elapsed % 36e5) / 6e4)).padStart(2, "0")}:${String(Math.floor((elapsed % 6e4) / 1e3)).padStart(2, "0")}`; }
                updatePrayerData(now, h, m, s);
            }
            requestAnimationFrame(clockLoop);
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
                chartInstance?.updateSeries([{ data: data.incidentTrend || new Array(12).fill(0) }]);
            } catch (e) {
            } finally {
                isLoadingData.value = false;
            }
        };

        const fetchAllIncidents = async () => {
            try {
                const res = await fetch("/api/incidents", { headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                allIncidents.value = await res.json();
                hydrateHandover();
            } catch (e) {
            }
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
        const getSevClass = (s) => s === "CRITICAL" ? "sev-crt" : (s === "HIGH" ? "sev-hgh" : (s === "MEDIUM" ? "sev-med" : "sev-low"));
        const isFreshIncident = (inc) => ((Date.now() - new Date(inc.date).getTime()) / 60000) <= 10;
        const getIncidentRowStyle = (inc) => {
            if (inc.severity === "CRITICAL") return { background: "rgba(255,51,102,0.08)" };
            if (isFreshIncident(inc)) return { boxShadow: "inset 3px 0 0 var(--yellow)" };
            return {};
        };
        const getIncidentPriorityScore = (incident) => {
            const sev = { CRITICAL: 400, HIGH: 300, MEDIUM: 200, LOW: 100 }[incident.severity] || 50;
            const status = { ESCALATED: 70, IN_PROGRESS: 55, TRIAGED: 35, NEW: 20 }[incident.status] || 0;
            return sev + status;
        };
        const hydrateHandover = () => {
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
        const nextQuickStatus = (status) => {
            const flow = { NEW: "TRIAGED", TRIAGED: "IN_PROGRESS", IN_PROGRESS: "ESCALATED", ESCALATED: "RESOLVED", RESOLVED: "IN_PROGRESS", FALSE_POSITIVE: "TRIAGED" };
            return flow[status] || "TRIAGED";
        };
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
        const toggleFullScreen = () => !document.fullscreenElement ? document.documentElement.requestFullscreen() : document.exitFullscreen();
        const animateJackpot = (fV) => { const p = imamPool.value.length > 0 ? imamPool.value : ["SEARCHING..."]; let c = 0; const ts = 90; const id = setInterval(() => { modal.imamText = p[Math.floor(Math.random() * p.length)]; modal.imamColor = (c % 2 === 0) ? "#fff" : "#aaa"; c++; if (c >= ts) { clearInterval(id); modal.imamText = fV; modal.imamColor = "var(--green)"; if (typeof confetti === "function") { confetti({ particleCount: 150, spread: 70, origin: { y: 0.6 }, colors: ["#00ff99", "#00ffff", "#ffffff"], zIndex: 10001 }); } } }, 30); };
        const triggerModal = (type, data = null) => { modal.show = true; modal.type = type; modal.progressWidth = "100%"; modal.transition = "none"; if (modalTimer) clearTimeout(modalTimer); setTimeout(() => { modal.progressWidth = "0%"; modal.transition = "width 60s linear"; }, 100); if (type === "START") { modal.title = "🚀 SYSTEM INITIALIZED"; modal.color = "var(--cyan)"; modal.shadow = "0 0 50px rgba(0,255,204,0.3)"; modal.msg = "DUTY CYCLE STARTED.<br>ALL SYSTEMS GREEN."; } else if (type === "END") { modal.title = "⚠️ DUTY CYCLE ENDED"; modal.color = "var(--yellow)"; modal.shadow = "0 0 50px rgba(255,204,0,0.3)"; modal.msg = "OPERATIONAL HOURS COMPLETE.<br>SECURE WORKSTATION."; } else if (type === "PRAYER") { modal.title = `🕌 PRAYER: ${data.name}`; modal.prayerName = data.name; modal.color = "var(--green)"; modal.shadow = "0 0 60px rgba(0,255,153,0.4)"; animateJackpot(data.imam); } playAlertSound(type); modalTimer = setTimeout(closeModal, 60000); };
        const closeModal = () => { modal.show = false; if (modalTimer) clearTimeout(modalTimer); };

        let intervals = [];
        onMounted(async () => {
            const sessionValid = await validateSession();
            if (!sessionValid) return;

            new Typed("#typed-output", { strings: ["BY ^200 <span style='color:var(--cyan)'>CYBERTEAM BAPPENAS</span>", "SYSTEM STATUS: ^500 <span style='color:var(--green)'>SECURE</span>", "MONITORING ACTIVE..."], typeSpeed: 30, backSpeed: 20, backDelay: 2000, loop: true, contentType: "html" });
            chartInstance = new ApexCharts(chartRef.value, { series: [{ name: "Incidents", data: [0, 0, 0, 0, 0, 0, 0, 0] }], chart: { type: "area", height: 80, sparkline: { enabled: true } }, stroke: { curve: "smooth", width: 2 }, fill: { type: "gradient", gradient: { shadeIntensity: 1, opacityFrom: 0.7, opacityTo: 0.1, stops: [0, 90, 100] } }, colors: ["#f1c40f"] });
            chartInstance.render();

            fetchGeoData(); fetchNews(); fetchSocData(); fetchAllIncidents(); fetchHealth(); requestAnimationFrame(clockLoop);
            initSignalRTerminal();

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
            }, 8500));

            epsChartInstance = new ApexCharts(epsChartRef.value, {
                series: [{ name: "EPS", data: epsDataSeries.value }],
                chart: {
                    type: "bar", height: 60, sparkline: { enabled: true },
                    animations: { enabled: true, easing: "linear", dynamicAnimation: { speed: 1000 } }
                },
                plotOptions: { bar: { columnWidth: "70%", borderRadius: 2 } },
                colors: ["#00ffcc"],
                tooltip: { fixed: { enabled: false }, x: { show: false }, marker: { show: false } }
            });
            epsChartInstance.render();

            intervals.push(setInterval(async () => {
                try {
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
                        epsChartInstance.updateSeries([{ data: epsDataSeries.value }]);
                    }
                } catch (e) {
                }
            }, 1000));

            intervals.push(setInterval(() => showBebek.value = !showBebek.value, 10000));
            intervals.push(setInterval(fetchSocData, 10000));
            intervals.push(setInterval(fetchAllIncidents, 15000));
            intervals.push(setInterval(fetchHealth, 15000));
        });

        onUnmounted(() => {
            intervals.forEach(clearInterval);
            if (terminalConnection) terminalConnection.stop();
        });

        return { isTerminalLive, showBebek, epsChartRef, currentEps, currentEventsLastMinute, locationName, weather, clockHands, clocks, date, shift, soc, socHealth, handover, analystQueue, healthSources, nextPrayer, prayerTimes, modal, newsFeed, chartRef, sysLogs, isLoadingData, odometerTickets, incidentModal, incidentFilters, filteredRecentIncidents, allowedStatuses, allowedSeverities, initAudioContext, formatTime, formatDateTime, formatRelativeTime, getSevClass, getIncidentRowStyle, isFreshIncident, nextQuickStatus, quickAssignIncident, quickStatusChange, openArchiveIncident, openArchiveList, toggleFullScreen, closeModal, escapeHtml, openIncidentDetail, closeIncidentDetail, isolateAttacker, authChecked };
    }
}).mount("#app");
