(function (global) {
    const toArray = (value) => Array.isArray(value) ? value : [];

    const parseDateValue = (value) => {
        if (!value) return null;
        const dateValue = new Date(value);
        return Number.isNaN(dateValue.getTime()) ? null : dateValue;
    };

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

    const formatAttendanceTime = (value) => {
        const parsed = parseDateValue(value);
        if (!parsed) return "--:--";
        return `${parsed.toLocaleTimeString("id-ID", { timeZone: "Asia/Jakarta", hour: "2-digit", minute: "2-digit", hour12: false })} WIB`;
    };

    const isWithinWorkingHours = (config, now = new Date()) => {
        const shiftStart = new Date(now);
        shiftStart.setHours(config.START, 0, 0, 0);
        const shiftEnd = new Date(now);
        shiftEnd.setHours(config.END, now.getDay() === 5 ? config.FRIDAY_END_MINUTE : 0, 0, 0);
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

    const normalizeAttendanceEntries = (payload) => {
        const items = toArray(payload?.data?.items).length ? payload.data.items : toArray(payload?.items);

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

    const createAttendanceRuntime = (deps) => {
        let toastNode = null;
        let toastTimers = [];
        let lastSnapshot = new Map();
        let hydrated = false;

        const clearToast = () => {
            toastTimers.forEach(clearTimeout);
            toastTimers = [];
            if (toastNode) {
                toastNode.remove();
                toastNode = null;
            }
        };

        const buildSnapshot = (entries) => {
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

        const playChime = async () => {
            try {
                deps.initAudioContext();
                if (deps.audioCtx.state === "suspended") await deps.audioCtx.resume();

                const now = deps.audioCtx.currentTime;
                const notes = [
                    { freq: 523.25, offset: 0, duration: 0.14, volume: 0.028 },
                    { freq: 659.25, offset: 0.12, duration: 0.16, volume: 0.024 },
                    { freq: 783.99, offset: 0.24, duration: 0.18, volume: 0.02 }
                ];

                notes.forEach(({ freq, offset, duration, volume }) => {
                    const osc = deps.audioCtx.createOscillator();
                    const gain = deps.audioCtx.createGain();
                    osc.type = "sine";
                    osc.frequency.setValueAtTime(freq, now + offset);
                    gain.gain.setValueAtTime(0.0001, now + offset);
                    gain.gain.linearRampToValueAtTime(volume, now + offset + 0.02);
                    gain.gain.exponentialRampToValueAtTime(0.0001, now + offset + duration);
                    osc.connect(gain);
                    gain.connect(deps.audioCtx.destination);
                    osc.start(now + offset);
                    osc.stop(now + offset + duration + 0.05);
                });
            } catch (e) {
            }
        };

        const showToast = ({ title, meta, accent = "var(--cyan)" }) => {
            clearToast();

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
            toastNode = layer;

            requestAnimationFrame(() => card.classList.add("show"));
            toastTimers.push(setTimeout(() => card.classList.add("hide"), 4200));
            toastTimers.push(setTimeout(() => clearToast(), 4900));
        };

        const notifyChange = (config, entries) => {
            if (!isWithinWorkingHours(config)) return;

            const currentSnapshot = buildSnapshot(entries);
            const changes = [];

            currentSnapshot.forEach((current, key) => {
                const previous = lastSnapshot.get(key);
                const entry = entries.find(item => String(item.key) === String(key));
                const name = entry?.name || key;

                if (!previous) {
                    if (current.isClockOut && current.clockOutTime) {
                        changes.push({ type: "CLOCK_OUT", name, clockInTime: current.clockInTime, clockOutTime: current.clockOutTime });
                    } else if (current.status === "OPENED" && current.clockInTime) {
                        changes.push({ type: "CLOCK_IN", name, clockInTime: current.clockInTime, clockOutTime: current.clockOutTime });
                    }
                    return;
                }

                const becameClockOut = !previous.isClockOut && current.isClockOut && current.clockOutTime;
                const becameOpen = previous.status !== "OPENED" && current.status === "OPENED" && current.clockInTime;

                if (becameClockOut) {
                    changes.push({ type: "CLOCK_OUT", name, clockInTime: current.clockInTime || previous.clockInTime, clockOutTime: current.clockOutTime });
                } else if (becameOpen) {
                    changes.push({ type: "CLOCK_IN", name, clockInTime: current.clockInTime, clockOutTime: current.clockOutTime });
                }
            });

            if (changes.length === 0) {
                lastSnapshot = currentSnapshot;
                return;
            }

            const latest = changes.sort((a, b) => {
                const aTime = parseDateValue(a.clockOutTime || a.clockInTime)?.getTime() || 0;
                const bTime = parseDateValue(b.clockOutTime || b.clockInTime)?.getTime() || 0;
                return bTime - aTime;
            })[0];

            if (latest.type === "CLOCK_IN") {
                showToast({ title: `${latest.name} clock in`, meta: `jam ${formatAttendanceTime(latest.clockInTime)}`, accent: "var(--cyan)" });
            } else {
                showToast({ title: `${latest.name} clock out`, meta: `clock in jam ${formatAttendanceTime(latest.clockInTime)}`, accent: "var(--red)" });
            }

            playChime();
            lastSnapshot = currentSnapshot;
        };

        const updateDurations = (entries) => {
            entries.forEach(item => {
                const elapsedSinceSync = item.isRunning ? Math.max(Math.floor((Date.now() - item.syncStartedAtMs) / 1000), 0) : 0;
                item.durationText = formatDurationFromSeconds(item.baseDurationSeconds + elapsedSinceSync);
            });
        };

        const sync = (payload, onHydrated) => {
            const normalized = normalizeAttendanceEntries(payload);
            if (hydrated) {
                notifyChange(deps.config, normalized.entries);
            } else {
                hydrated = true;
                lastSnapshot = buildSnapshot(normalized.entries);
                if (typeof onHydrated === "function") onHydrated();
            }
            updateDurations(normalized.entries);
            return normalized;
        };

        return {
            clearToast,
            sync,
            updateDurations,
            notifyChange,
            formatAttendanceTime,
            formatDurationFromSeconds,
            parseDateValue,
            normalizeAttendanceEntries
        };
    };

    global.DashboardAttendanceModule = {
        createAttendanceRuntime,
        normalizeAttendanceEntries,
        formatAttendanceTime,
        formatDurationFromSeconds,
        parseDateValue,
        isWithinWorkingHours,
        extractName,
        extractDurationSeconds
    };
})(window);
