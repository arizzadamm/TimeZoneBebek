const { createApp, ref, computed, onMounted } = Vue;

createApp({
    setup() {
        const accessKey = ref("");
        const isSubmitting = ref(false);
        const statusMessage = ref("");
        const statusType = ref("info");
        const inputRef = ref(null);
        const params = new URLSearchParams(window.location.search);
        const rawReturnUrl = params.get("returnUrl") || "/";
        const returnUrl = rawReturnUrl.startsWith("/") ? rawReturnUrl : "/";

        const returnLabel = computed(() => returnUrl.valueOf() === "/" ? "Dashboard" : returnUrl);

        async function validateKey(key) {
            const res = await fetch("/api/incidents/workflow", {
                headers: { "X-API-KEY": key }
            });

            if (!res.ok) {
                throw new Error(res.status === 401 || res.status === 403
                    ? "Access key tidak valid."
                    : `Validation failed (${res.status}).`);
            }
        }

        async function submitLogin() {
            const key = accessKey.value.trim();
            if (!key) {
                statusMessage.value = "ACCESS KEY REQUIRED";
                statusType.value = "error";
                return;
            }

            isSubmitting.value = true;
            statusMessage.value = "VERIFYING...";
            statusType.value = "info";

            try {
                await validateKey(key);
                const session = window.CSIRT_AUTH?.saveKey?.(key);
                if (!session) {
                    throw new Error("SESSION FAILED");
                }

                statusMessage.value = "ACCESS GRANTED";
                statusType.value = "info";
                window.location.replace(returnUrl);
            } catch (error) {
                statusMessage.value = (error.message || "AUTH FAILED").toUpperCase();
                statusType.value = "error";
            } finally {
                isSubmitting.value = false;
            }
        }

        onMounted(() => {
            inputRef.value?.focus();
        });

        return {
            accessKey,
            isSubmitting,
            statusMessage,
            statusType,
            returnLabel,
            inputRef,
            submitLogin
        };
    }
}).mount("#app");
