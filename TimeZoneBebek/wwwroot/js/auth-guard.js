(function () {
    const SESSION_KEY = "CSIRT_SESSION";
    const LEGACY_KEY = "CSIRT_KEY";
    const LOGIN_PATH = "/login";
    const DASHBOARD_PATH = "/";
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

    const toAbsolutePath = (path, query = "") => `${window.location.origin}${path}${query}`;

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
    const isLoginPage = path === LOGIN_PATH || path.startsWith(LOGIN_PATH + "/");

    if ((!session || isExpired) && !isLoginPage) {
        const next = encodeURIComponent(window.location.pathname + window.location.search + window.location.hash);
        alert("ACCESS DENIED: Session missing or expired. Please login again.");
        window.location.href = toAbsolutePath(LOGIN_PATH, `?returnUrl=${next}`);
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
            window.location.href = toAbsolutePath(LOGIN_PATH);
        }
    };

    if (isLoginPage && session && !isExpired) {
        const params = new URLSearchParams(window.location.search);
        const returnUrl = params.get("returnUrl");
        const nextUrl = returnUrl && returnUrl.startsWith("/") ? returnUrl : DASHBOARD_PATH;
        if (window.location.pathname + window.location.search !== nextUrl) {
            window.location.replace(nextUrl);
        }
    }
})();
