(function (global) {
    const createClockRuntime = (deps) => {
        let timer = null;
        let lastSec = -1;

        const tick = () => {
            const now = new Date();
            const ms = now.getMilliseconds();
            const s = now.getSeconds();
            const m = now.getMinutes();
            const h = now.getHours();

            deps.clockHands.s = (s + (ms / 1000)) * 6;
            deps.clockHands.m = (m * 6) + (s * 0.1);
            deps.clockHands.h = ((h % 12) * 30) + (m * 0.5);

            lastSec = s;
            deps.clocks.wib = deps.fW.format(now).replace(/\./g, ":");
            deps.clocks.wita = deps.fWa.format(now).replace(/\./g, ":");
            deps.clocks.wit = deps.fWt.format(now).replace(/\./g, ":");
            deps.clocks.utc = deps.fU.format(now).replace(/\./g, ":");

            const shiftStart = new Date(now);
            shiftStart.setHours(deps.config.START, 0, 0, 0);
            const shiftEnd = new Date(now);
            shiftEnd.setHours(deps.config.END, now.getDay() === 5 ? deps.config.FRIDAY_END_MINUTE : 0, 0, 0);
            const isWithinShift = now >= shiftStart && now < shiftEnd;
            deps.clocks.wibStyle = { color: isWithinShift ? "var(--cyan)" : "var(--red)", textShadow: isWithinShift ? "0 0 20px rgba(0, 255, 204, 0.2)" : "0 0 20px rgba(255, 51, 102, 0.4)" };

            const dy = ["MINGGU", "SENIN", "SELASA", "RABU", "KAMIS", "JUMAT", "SABTU"];
            const mt = ["JAN", "FEB", "MAR", "APR", "MEI", "JUN", "JUL", "AGU", "SEP", "OKT", "NOV", "DES"];
            deps.date.dayName = dy[now.getDay()];
            deps.date.fullDate = `${String(now.getDate()).padStart(2, "0")} ${mt[now.getMonth()]} ${now.getFullYear()}`;
            deps.date.hijri = deps.fH.format(now);

            const endHour = shiftEnd.getHours();
            const endMinute = shiftEnd.getMinutes();
            if (h === deps.config.START && m === 0 && s < 2 && !deps.flags.shiftAlerted) { deps.triggerModal("START"); deps.flags.shiftAlerted = true; }
            else if (h === endHour && m === endMinute && s < 2 && !deps.flags.shiftAlerted) { deps.triggerModal("END"); deps.flags.shiftAlerted = true; }
            else if (s > 5) deps.flags.shiftAlerted = false;

            deps.tickAttendanceDurations();
            deps.updatePrayerData(now, h, m, s);
        };

        const start = () => {
            if (timer) clearTimeout(timer);

            const loop = () => {
                tick();
                const delay = 1000 - (Date.now() % 1000);
                timer = setTimeout(loop, Math.max(delay, 50));
            };

            tick();
            const delay = 1000 - (Date.now() % 1000);
            timer = setTimeout(loop, Math.max(delay, 50));
        };

        const stop = () => {
            if (timer) clearTimeout(timer);
            timer = null;
        };

        return { start, stop, tick };
    };

    global.DashboardClockModule = { createClockRuntime };
})(window);
