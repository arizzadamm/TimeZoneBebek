(function () {
    const SESSION_KEY = "CSIRT_SESSION";
    const LEGACY_KEY = "CSIRT_KEY";
    const ARCHIVE_PATH = "/archive";
    const DEFAULT_HOURS = 8;

    const readSession = () => {
        const raw = localStorage.getItem(SESSION_KEY);
        if (raw) {
            try {
                const parsed = JSON.parse(raw);
                if (parsed && typeof parsed.key === "string" && typeof parsed.expiresAt === "string") {
                    return parsed;
                }
            } catch (_) {
                localStorage.removeItem(SESSION_KEY);
            }
        }

        const legacy = localStorage.getItem(LEGACY_KEY);
        if (!legacy) {
            return null;
        }

        const migrated = createSession(legacy.trim(), DEFAULT_HOURS);
        saveSession(migrated);
        localStorage.removeItem(LEGACY_KEY);
        return migrated;
    };

    const createSession = (key, durationHours) => {
        const expiresAt = new Date(Date.now() + durationHours * 60 * 60 * 1000).toISOString();
        return { key, expiresAt };
    };

    const saveSession = (session) => {
        localStorage.setItem(SESSION_KEY, JSON.stringify(session));
    };

    const clearSession = () => {
        localStorage.removeItem(SESSION_KEY);
        localStorage.removeItem(LEGACY_KEY);
    };

    const session = readSession();
    const isExpired = !session || !session.expiresAt || new Date(session.expiresAt).getTime() <= Date.now();

    if (isExpired) {
        clearSession();
    }

    const path = window.location.pathname.toLowerCase();
    const isArchivePage = path === ARCHIVE_PATH || path.startsWith(ARCHIVE_PATH + "/");

    if ((!session || isExpired) && !isArchivePage) {
        alert("ACCESS DENIED: Session missing or expired. Please login again via Archive Dashboard.");
        window.location.href = ARCHIVE_PATH;
    }

    window.API_KEY = !session || isExpired ? "" : session.key.trim();
    window.CSIRT_AUTH = {
        getSession: () => readSession(),
        saveKey: (key, durationHours = DEFAULT_HOURS) => {
            if (!key || !key.trim()) return null;
            const nextSession = createSession(key.trim(), durationHours);
            saveSession(nextSession);
            window.API_KEY = nextSession.key;
            return nextSession;
        },
        logout: () => {
            clearSession();
            window.API_KEY = "";
            window.location.href = ARCHIVE_PATH;
        }
    };
})();
