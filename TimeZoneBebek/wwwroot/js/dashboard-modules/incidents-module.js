(function (global) {
    const createIncidentsRuntime = (deps) => {
        const getIncidentPriorityScore = (incident) => {
            const sev = { CRITICAL: 400, HIGH: 300, MEDIUM: 200, LOW: 100 }[incident.severity] || 50;
            const status = { ESCALATED: 70, IN_PROGRESS: 55, TRIAGED: 35, NEW: 20 }[incident.status] || 0;
            return sev + status;
        };

        const isFreshIncident = (incident) => ((Date.now() - new Date(incident.date).getTime()) / 60000) <= 10;

        const getIncidentRowStyle = (incident) => {
            if (incident.severity === "CRITICAL") return { background: "rgba(255,51,102,0.08)" };
            if (isFreshIncident(incident)) return { boxShadow: "inset 3px 0 0 var(--yellow)" };
            return {};
        };

        const nextQuickStatus = (status) => {
            const flow = { NEW: "TRIAGED", TRIAGED: "IN_PROGRESS", IN_PROGRESS: "ESCALATED", ESCALATED: "RESOLVED", RESOLVED: "IN_PROGRESS", FALSE_POSITIVE: "TRIAGED" };
            return flow[status] || "TRIAGED";
        };

        const hydrateHandover = (allIncidents, handover) => {
            const open = allIncidents.filter(i => !["RESOLVED", "FALSE_POSITIVE"].includes(i.status));
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

        const buildDashboardFeed = (recentIncidents) =>
            [...(recentIncidents || [])]
                .sort((a, b) => {
                    const priorityDiff = getIncidentPriorityScore(b) - getIncidentPriorityScore(a);
                    if (priorityDiff !== 0) return priorityDiff;
                    return new Date(b.date).getTime() - new Date(a.date).getTime();
                })
                .slice(0, 8);

        const processIncomingIncidentAlerts = ({ incidents, knownIds, setKnownIds, onAlert }) => {
            const nextIds = new Set(incidents.filter(i => i?.id).map(i => i.id));
            if (!knownIds.hydrated) {
                nextIds.forEach(id => knownIds.ids.add(id));
                knownIds.hydrated = true;
                return;
            }

            const freshIncidents = incidents
                .filter(inc => inc?.id && !knownIds.ids.has(inc.id))
                .filter(inc => {
                    const ts = new Date(inc.date).getTime();
                    return Number.isFinite(ts) && (Date.now() - ts) <= deps.freshWindowMs;
                })
                .sort((a, b) => new Date(b.date).getTime() - new Date(a.date).getTime());

            nextIds.forEach(id => knownIds.ids.add(id));
            if (freshIncidents.length === 0) return;

            const now = Date.now();
            if ((now - knownIds.lastAlertAt) < deps.alertCooldownMs) return;
            knownIds.lastAlertAt = now;
            onAlert(freshIncidents);
        };

        return {
            getIncidentPriorityScore,
            isFreshIncident,
            getIncidentRowStyle,
            nextQuickStatus,
            hydrateHandover,
            buildDashboardFeed,
            processIncomingIncidentAlerts
        };
    };

    global.DashboardIncidentsModule = { createIncidentsRuntime };
})(window);
