const { createApp, ref, reactive, computed, onMounted, watch } = Vue;

createApp({
    setup() {
        const severities = ["CRITICAL", "HIGH", "MEDIUM", "LOW"];
        const incidents = ref([]);
        const archiveSummary = reactive({ openCount: 0, criticalCount: 0, assignedCount: 0, resolvedCount: 0, totalCount: 0, filteredCount: 0 });
        const pagination = reactive({ page: 1, pageSize: 25, totalCount: 0, totalPages: 1 });
        const workflowStatuses = ref(["NEW", "TRIAGED", "IN_PROGRESS", "ESCALATED", "RESOLVED", "FALSE_POSITIVE"]);
        const workflowTransitions = ref({});
        const isLoading = ref(false);
        const isAnalyzing = ref(false);
        const isSavingForm = ref(false);
        const sessionKeyInput = ref("");
        const hasSession = ref(!!window.API_KEY);
        const sessionExpiryLabel = ref("");
        const searchQuery = ref("");
        const filterSev = ref("ALL");
        const filterStatus = ref("ALL");
        const selectedIds = ref([]);
        const activeIncident = ref(null);
        const showFormModal = ref(false);
        const formMode = ref("create");
        const tagDraft = ref("");
        const workflowDraft = reactive({ owner: "" });
        const form = reactive(createEmptyForm());

        const isAllSelected = computed(() =>
            incidents.value.length > 0 &&
            incidents.value.every(inc => selectedIds.value.includes(inc.id)));

        const knownOwners = computed(() =>
            [...new Set(incidents.value.map(inc => inc.owner).filter(Boolean))].sort((a, b) => a.localeCompare(b)));

        const openIncidentCount = computed(() => archiveSummary.openCount);
        const criticalIncidentCount = computed(() => archiveSummary.criticalCount);
        const assignedIncidentCount = computed(() => archiveSummary.assignedCount);
        const resolvedIncidentCount = computed(() => archiveSummary.resolvedCount);
        const showingCount = computed(() => incidents.value.length);

        const allowedTransitions = computed(() => {
            if (!activeIncident.value) return [];
            return workflowTransitions.value[activeIncident.value.status] || [];
        });

        const formStatusOptions = computed(() => {
            if (formMode.value === "create") return ["NEW", "TRIAGED"];
            const current = form.originalStatus || form.status || "NEW";
            const allowed = workflowTransitions.value[current] || [];
            return [current, ...allowed.filter(status => status !== current)];
        });

        watch(activeIncident, (incident) => {
            workflowDraft.owner = incident?.owner || "";
        });
        watch([searchQuery, filterSev, filterStatus], () => {
            pagination.page = 1;
            fetchIncidents();
        });

        function createEmptyForm() {
            return {
                id: "",
                title: "",
                severity: "MEDIUM",
                attacker: "",
                summary: "",
                tags: [],
                status: "NEW",
                owner: "",
                source: "",
                affectedAsset: "",
                firstSeenLocal: "",
                lastSeenLocal: "",
                resolutionNote: "",
                originalStatus: "NEW"
            };
        }

        function showToast(msg, type = "info") {
            const bg = type === "alert"
                ? "linear-gradient(to right, #ff5f6d, #ffc371)"
                : "linear-gradient(to right, #000, #004e92)";
            Toastify({
                text: msg,
                duration: 3200,
                gravity: "top",
                position: "right",
                style: {
                    background: bg,
                    border: "1px solid var(--cyan)",
                    color: type === "alert" ? "#000" : "var(--cyan)",
                    fontFamily: "Orbitron"
                }
            }).showToast();
        }

        function syncSessionMeta() {
            const session = window.CSIRT_AUTH?.getSession?.();
            hasSession.value = !!(window.API_KEY && session);
            sessionExpiryLabel.value = session?.expiresAt
                ? new Date(session.expiresAt).toLocaleString("id-ID", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" })
                : "--";
        }

        function loginSession() {
            const session = window.CSIRT_AUTH?.saveKey?.(sessionKeyInput.value);
            if (!session) {
                showToast("Access key is required", "alert");
                return;
            }

            sessionKeyInput.value = "";
            syncSessionMeta();
            fetchWorkflow();
            fetchIncidents();
            showToast("Session started", "cyber");
        }

        function logoutSession() {
            window.CSIRT_AUTH?.logout?.();
        }

        function toggleAll(e) {
            selectedIds.value = e.target.checked ? incidents.value.map(i => i.id) : [];
        }

        async function fetchWorkflow() {
            if (!window.API_KEY) return;
            try {
                const res = await fetch("/api/incidents/workflow", { headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                const data = await res.json();
                workflowStatuses.value = data.statuses || workflowStatuses.value;
                workflowTransitions.value = data.transitions || {};
            } catch (e) {
                showToast("Failed to load workflow metadata", "alert");
            }
        }

        async function fetchIncidents() {
            if (!window.API_KEY) {
                showToast("Please start a session first", "alert");
                return;
            }

            isLoading.value = true;
            try {
                const params = new URLSearchParams({
                    search: searchQuery.value,
                    severity: filterSev.value,
                    status: filterStatus.value,
                    page: String(pagination.page),
                    pageSize: String(pagination.pageSize)
                });

                const res = await fetch(`/api/incidents/archive?${params.toString()}`, { headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                const data = await res.json();

                // FIX: Pengamanan tingkat tinggi saat parsing JSON dari .NET
                // Jika .NET mengembalikan Array murni (bukan object), kita tangani
                const isArray = Array.isArray(data);
                incidents.value = isArray ? data : (data.items || []);

                // Amankan Pagination
                pagination.page = data.page || pagination.page;
                pagination.pageSize = data.pageSize || pagination.pageSize;
                pagination.totalCount = data.totalCount || incidents.value.length;
                pagination.totalPages = data.totalPages || 1;

                // FIX UTAMA: Gunakan fallback object kosong {} jika data.summary tidak dikirim oleh .NET
                const summary = data.summary || {};

                archiveSummary.openCount = summary.openCount || 0;
                archiveSummary.criticalCount = summary.criticalCount || 0;
                archiveSummary.assignedCount = summary.assignedCount || 0;
                archiveSummary.resolvedCount = summary.resolvedCount || 0;
                archiveSummary.totalCount = summary.totalCount || 0;
                archiveSummary.filteredCount = summary.filteredCount || 0;

                selectedIds.value = selectedIds.value.filter(id => incidents.value.some(inc => inc.id === id));
                if (activeIncident.value) {
                    activeIncident.value = incidents.value.find(i => i.id === activeIncident.value.id) || null;
                }
            } catch (e) {
                showToast("Failed to fetch database", "alert");
            } finally {
                isLoading.value = false;
            }
        }

        async function deleteIncident(id) {
            if (!confirm(`WARNING: Are you sure you want to permanently delete incident ${id}?`)) return;
            try {
                const res = await fetch(`/api/incidents/${id}`, { method: "DELETE", headers: { "X-API-KEY": API_KEY } });
                if (!res.ok) throw new Error();
                incidents.value = incidents.value.filter(i => i.id !== id);
                selectedIds.value = selectedIds.value.filter(selectedId => selectedId !== id);
                if (activeIncident.value?.id === id) closeDetail();
                if (incidents.value.length === 1 && pagination.page > 1) pagination.page--;
                await fetchIncidents();
                showToast("Record Deleted", "cyber");
            } catch (e) {
                showToast("Delete failed", "alert");
            }
        }

        async function updateStatus(id, newStatus) {
            try {
                const res = await fetch(`/api/incidents/${id}/status`, {
                    method: "PUT",
                    headers: { "Content-Type": "application/json", "X-API-KEY": API_KEY },
                    body: JSON.stringify(newStatus)
                });
                const data = await res.json();
                if (!res.ok) throw new Error(data.message || "Update failed");
                await fetchIncidents();
                showToast(`Status updated to ${newStatus}`, "cyber");
            } catch (e) {
                showToast(e.message || "Update failed", "alert");
            }
        }

        async function bulkTriaged() {
            if (!confirm(`Mark ${selectedIds.value.length} incidents as TRIAGED?`)) return;
            try {
                const res = await fetch("/api/incidents/bulk-status", {
                    method: "PUT",
                    headers: { "Content-Type": "application/json", "X-API-KEY": API_KEY },
                    body: JSON.stringify({ ids: selectedIds.value, status: "TRIAGED" })
                });
                const data = await res.json();
                if (!res.ok) throw new Error(data.message || "Batch update failed");
                selectedIds.value = [];
                await fetchIncidents();
                showToast(data.message || "Selected incidents moved to TRIAGED", "cyber");
            } catch (e) {
                showToast(e.message || "Batch update failed", "alert");
            }
        }

        const bulkResolve = bulkTriaged;

        async function analyzeIncident() {
            if (!activeIncident.value) return;
            isAnalyzing.value = true;
            try {
                const res = await fetch(`/api/incidents/analyze/${activeIncident.value.id}`, {
                    method: "POST",
                    headers: { "X-API-KEY": API_KEY }
                });
                const data = await res.json();
                if (!res.ok) throw new Error(data.message || "Analysis failed");
                activeIncident.value.aiAnalysis = data.analysis;
                await fetchIncidents();
                showToast("AI Analysis Complete", "cyber");
            } catch (e) {
                showToast(e.message || "Analysis failed", "alert");
            } finally {
                isAnalyzing.value = false;
            }
        }

        function openDetail(incident) {
            activeIncident.value = JSON.parse(JSON.stringify(incident));
            workflowDraft.owner = incident.owner || "";
        }

        function closeDetail() {
            activeIncident.value = null;
            workflowDraft.owner = "";
        }

        function openCreateForm() {
            Object.assign(form, createEmptyForm());
            tagDraft.value = "";
            formMode.value = "create";
            showFormModal.value = true;
        }

        function openEditForm(incident) {
            const source = incident || activeIncident.value;
            if (!source) return;

            Object.assign(form, {
                id: source.id || "",
                title: source.title || "",
                severity: source.severity || "MEDIUM",
                attacker: source.attacker || "",
                summary: source.summary || "",
                tags: [...(source.tags || [])],
                status: source.status || "NEW",
                owner: source.owner || "",
                source: source.source || "",
                affectedAsset: source.affectedAsset || "",
                firstSeenLocal: toLocalDateTimeValue(source.firstSeen || source.date),
                lastSeenLocal: toLocalDateTimeValue(source.lastSeen || source.date),
                resolutionNote: source.resolutionNote || "",
                originalStatus: source.status || "NEW"
            });
            tagDraft.value = "";
            formMode.value = "edit";
            showFormModal.value = true;
        }

        function closeForm() {
            showFormModal.value = false;
        }

        function addTag() {
            const value = tagDraft.value.trim().replace(/\s+/g, "-").toLowerCase();
            if (!value) return;
            if (!form.tags.includes(value)) form.tags.push(value);
            tagDraft.value = "";
        }

        function removeTag(tag) {
            form.tags = form.tags.filter(t => t !== tag);
        }

        async function submitForm() {
            if (!window.API_KEY) {
                showToast("Please start a session first", "alert");
                return;
            }

            isSavingForm.value = true;
            try {
                const payload = {
                    id: formMode.value === "edit" ? form.id : undefined,
                    title: form.title,
                    severity: form.severity,
                    attacker: form.attacker,
                    summary: form.summary,
                    tags: form.tags,
                    status: form.status,
                    owner: form.owner || null,
                    source: form.source || null,
                    affectedAsset: form.affectedAsset || null,
                    firstSeen: form.firstSeenLocal ? new Date(form.firstSeenLocal).toISOString() : null,
                    lastSeen: form.lastSeenLocal ? new Date(form.lastSeenLocal).toISOString() : null,
                    resolutionNote: form.resolutionNote || null
                };

                const isEdit = formMode.value === "edit";
                const res = await fetch(isEdit ? `/api/incidents/${form.id}` : "/api/incidents", {
                    method: isEdit ? "PUT" : "POST",
                    headers: { "Content-Type": "application/json", "X-API-KEY": API_KEY },
                    body: JSON.stringify(payload)
                });
                const data = await res.json();
                if (!res.ok) throw new Error(data.message || "Save failed");

                pagination.page = 1;
                await fetchIncidents();
                closeForm();
                if (isEdit && activeIncident.value?.id === form.id) {
                    openDetail(incidents.value.find(i => i.id === form.id));
                }
                showToast(isEdit ? "Incident updated" : "Incident created", "cyber");
            } catch (e) {
                showToast(e.message || "Save failed", "alert");
            } finally {
                isSavingForm.value = false;
            }
        }

        async function saveAssignment() {
            if (!activeIncident.value) return;
            const updated = JSON.parse(JSON.stringify(activeIncident.value));
            updated.owner = workflowDraft.owner || null;

            try {
                const res = await fetch(`/api/incidents/${updated.id}`, {
                    method: "PUT",
                    headers: { "Content-Type": "application/json", "X-API-KEY": API_KEY },
                    body: JSON.stringify(updated)
                });
                const data = await res.json();
                if (!res.ok) throw new Error(data.message || "Assignment update failed");
                await fetchIncidents();
                openDetail(incidents.value.find(i => i.id === updated.id));
                showToast("Owner assignment saved", "cyber");
            } catch (e) {
                showToast(e.message || "Assignment update failed", "alert");
            }
        }

        async function transitionIncident(status) {
            if (!activeIncident.value) return;
            await updateStatus(activeIncident.value.id, status);
            openDetail(incidents.value.find(i => i.id === activeIncident.value.id));
        }

        function goToPage(page) {
            if (page < 1 || page > pagination.totalPages || page === pagination.page) return;
            pagination.page = page;
            fetchIncidents();
        }

        function updatePageSize(size) {
            if (size === pagination.pageSize) return;
            pagination.pageSize = size;
            pagination.page = 1;
            fetchIncidents();
        }

        function formatDate(value) {
            if (!value) return "--";
            return new Date(value).toLocaleString("id-ID", {
                year: "numeric",
                month: "short",
                day: "2-digit",
                hour: "2-digit",
                minute: "2-digit"
            });
        }

        function formatAuditDate(value) {
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

        function getSevClass(severity) {
            if (severity === "CRITICAL") return "sev-crt";
            if (severity === "HIGH") return "sev-hgh";
            if (severity === "MEDIUM") return "sev-med";
            return "sev-low";
        }

        function getStatusClass(status) {
            if (status === "RESOLVED") return "status-resolved";
            if (status === "IN_PROGRESS" || status === "ESCALATED" || status === "TRIAGED") return "status-investigating";
            return "status-open";
        }

        function toLocalDateTimeValue(value) {
            if (!value) return "";
            const date = new Date(value);
            const pad = (n) => String(n).padStart(2, "0");
            return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
        }

        onMounted(() => {
            new Typed("#typed-output", { strings: ["ACCESSING DATABASE...", "ARCHIVE LINK ESTABLISHED", "ANALYST WORKSTATION READY"], typeSpeed: 30, backSpeed: 20, loop: true });
            syncSessionMeta();
            if (window.API_KEY) {
                fetchWorkflow();
                fetchIncidents();
            }
        });

        return {
            severities,
            incidents,
            archiveSummary,
            pagination,
            workflowStatuses,
            isLoading,
            isAnalyzing,
            isSavingForm,
            sessionKeyInput,
            hasSession,
            sessionExpiryLabel,
            searchQuery,
            filterSev,
            filterStatus,
            selectedIds,
            activeIncident,
            showFormModal,
            formMode,
            form,
            tagDraft,
            workflowDraft,
            isAllSelected,
            knownOwners,
            openIncidentCount,
            criticalIncidentCount,
            assignedIncidentCount,
            resolvedIncidentCount,
            showingCount,
            allowedTransitions,
            formStatusOptions,
            loginSession,
            logoutSession,
            toggleAll,
            fetchIncidents,
            deleteIncident,
            bulkResolve,
            bulkTriaged,
            analyzeIncident,
            openDetail,
            closeDetail,
            openCreateForm,
            openEditForm,
            closeForm,
            addTag,
            removeTag,
            submitForm,
            saveAssignment,
            transitionIncident,
            goToPage,
            updatePageSize,
            formatDate,
            formatAuditDate,
            getSevClass,
            getStatusClass
        };
    }
}).mount("#app");
