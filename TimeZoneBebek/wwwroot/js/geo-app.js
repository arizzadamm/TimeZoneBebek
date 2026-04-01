const { createApp, ref, reactive, computed, watch, onMounted, onUnmounted } = Vue;

createApp({
    setup() {
        const LOCAL_TOKEN = window.API_KEY || "";
        const HOME_LAT = -6.2088;
        const HOME_LON = 106.8456;
        const MAX_FEED = 14;
        const MAX_HISTORY = 80;
        const MAX_SIGNATURES = 250;
        const severityOptions = ["ALL", "CRITICAL", "HIGH", "MEDIUM", "LOW"];

        const isAudioEnabled = ref(localStorage.getItem("GEO_AUDIO") === "true");
        const isAutoHunt = ref(false);
        const ipInput = ref("");
        const latencyMs = ref("--");
        const statusText = ref("● SYSTEM ONLINE");
        const statusColor = ref("var(--green)");
        const viewMode = ref(localStorage.getItem("GEO_VIEW_MODE") || "analytic");

        const hud = reactive({ ip: "READY", city: "---", coord: "---", visible: true });
        const intel = reactive({
            cc: "xx",
            ip: "READY",
            country: "---",
            city: "---",
            coord: "---",
            isp: "-",
            org: "-",
            asn: "-",
            timezone: "-",
            typeLabel: "NO TARGET",
            typeColor: "var(--green)",
            severity: "LOW",
            severityColor: "var(--green)",
            firstSeen: null,
            lastSeen: null,
            hitCount: 0,
            eventCount: 0,
            note: "Select a threat from the feed or run a manual trace to build the dossier."
        });
        const filters = reactive({ severity: "ALL", type: "ALL", country: "ALL" });
        const pinnedTarget = reactive({ enabled: false, ip: null });
        const replay = reactive({ isPlaying: false, index: 0 });

        const threatLog = ref([]);
        const eventHistory = ref([]);
        const countryStats = ref([]);
        const toasts = ref([]);

        const traceCache = new Map();
        const seenSignatures = new Set();
        const seenSignatureOrder = [];

        let world = null;
        let connection = null;
        let sunLight = null;
        let activeProjectiles = [];
        let activeExplosions = [];
        let activeContrails = [];
        let activeShockwaves = [];
        let activeArcs = [];
        let animationId = null;
        let pingInterval = null;
        let replayInterval = null;
        let alarmCooldownTimer = null;
        let alarmArmed = true;

        const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        const sfx = {
            sonar: new Howl({ src: ["/assets/sounds/sonar.mp3"], volume: 0.6, spatial: true }),
            alarm: new Howl({ src: ["/assets/sounds/alarm.mp3"], volume: 0.4, loop: false, rate: 1.0 }),
            lock: new Howl({ src: ["/assets/sounds/lock.mp3"], volume: 0.7 })
        };

        const legendItems = [
            { label: "Critical / DDOS", color: "#ff3366", description: "Lonjakan volumetrik yang butuh prioritas tertinggi." },
            { label: "High / Brute Force", color: "#bc13fe", description: "Percobaan autentikasi agresif atau brute force." },
            { label: "Medium / Web Attack", color: "#ffcc00", description: "Pola abuse aplikasi web dan probing terarah." },
            { label: "Low / Suspicious", color: "#00cccc", description: "Anomali yang masih perlu dipantau sebelum triage." }
        ];
        const guidanceTips = [
            { title: "Dashboard mode", description: "Mode ringkas untuk wallboard: fokus ke origin stats dan active feed." },
            { title: "Analytic mode", description: "Mode investigasi lengkap dengan dossier, filter, dan replay." },
            { title: "Pin target", description: "Gunakan pin agar dossier tidak tergantikan event live berikutnya." },
            { title: "Replay timeline", description: "Putar ulang event baru untuk handover atau presentasi insiden." }
        ];

        const latencyColor = computed(() => {
            if (latencyMs.value === "--") return "#666";
            const value = parseInt(latencyMs.value, 10);
            if (value < 100) return "var(--green)";
            if (value < 300) return "var(--yellow)";
            return "var(--red)";
        });

        const typeOptions = computed(() => ["ALL", ...Array.from(new Set(threatLog.value.map(threat => threat.typeKey))).filter(Boolean)]);
        const countryOptions = computed(() => ["ALL", ...Array.from(new Set(threatLog.value.map(threat => threat.countryLabel))).filter(Boolean).sort()]);

        const matchesFilters = (threat) => {
            const severityOk = filters.severity === "ALL" || threat.severity === filters.severity;
            const typeOk = filters.type === "ALL" || threat.typeKey === filters.type;
            const countryOk = filters.country === "ALL" || threat.countryLabel === filters.country;
            return severityOk && typeOk && countryOk;
        };

        const filteredThreatLog = computed(() => threatLog.value.filter(matchesFilters));
        const filteredEventHistory = computed(() => eventHistory.value.filter(matchesFilters));
        const replayTimeline = computed(() => filteredEventHistory.value.slice(0, 40).reverse());
        const primaryLabelThreats = computed(() => {
            const pool = [];
            const addUnique = (threat) => {
                if (!threat || !threat.ip || typeof threat.lat !== "number" || typeof threat.lon !== "number") return;
                if (pool.some((item) => item.ip === threat.ip)) return;
                pool.push(threat);
            };

            if (pinnedTarget.enabled && pinnedTarget.ip) addUnique(eventHistory.value.find((event) => event.ip === pinnedTarget.ip));
            if (intel.ip && intel.ip !== "READY") addUnique(eventHistory.value.find((event) => event.ip === intel.ip));

            filteredThreatLog.value
                .slice()
                .sort((a, b) => (b.count || 0) - (a.count || 0))
                .slice(0, 8)
                .forEach(addUnique);

            return pool.slice(0, 8);
        });
        const replayCurrentEvent = computed(() => {
            if (!replayTimeline.value.length) return null;
            return replayTimeline.value[Math.min(replay.index, replayTimeline.value.length - 1)] || null;
        });

        const setViewMode = (mode) => {
            viewMode.value = mode;
            localStorage.setItem("GEO_VIEW_MODE", mode);
        };

        const unlockAudio = () => {
            if (audioCtx.state === "suspended") audioCtx.resume();
        };

        const toggleAudio = () => {
            isAudioEnabled.value = !isAudioEnabled.value;
            localStorage.setItem("GEO_AUDIO", String(isAudioEnabled.value));
            Howler.mute(!isAudioEnabled.value);
            if (!isAudioEnabled.value) {
                if (sfx.alarm.playing()) sfx.alarm.stop();
                window.speechSynthesis.cancel();
            }
        };

        const playSonar = (lon = 0) => {
            if (!isAudioEnabled.value) return;
            const id = sfx.sonar.play();
            sfx.sonar.stereo(lon / 180, id);
        };

        const playLockSound = () => {
            if (isAudioEnabled.value) sfx.lock.play();
        };

        const playAlarm = (active) => {
            if (!isAudioEnabled.value) return;
            if (active) {
                if (alarmArmed && !sfx.alarm.playing()) {
                    alarmArmed = false;
                    sfx.alarm.play();
                    if (alarmCooldownTimer) clearTimeout(alarmCooldownTimer);
                    alarmCooldownTimer = setTimeout(() => {
                        alarmArmed = true;
                    }, 4500);
                }
                return;
            }
            if (alarmCooldownTimer) {
                clearTimeout(alarmCooldownTimer);
                alarmCooldownTimer = null;
            }
            alarmArmed = true;
        };

        const speakThreat = (countriesSet) => {
            if (!isAudioEnabled.value || !("speechSynthesis" in window)) return;
            const countries = Array.from(countriesSet).filter(Boolean).slice(0, 2);
            if (!countries.length) return;
            const utterance = new SpeechSynthesisUtterance(`Alert. Traffic from ${countries.join(", ")}.`);
            utterance.rate = 0.9;
            utterance.pitch = 0.7;
            window.speechSynthesis.cancel();
            window.speechSynthesis.speak(utterance);
        };

        const getAttackConfig = (threat) => {
            const palette = { DDOS: "#ff3366", MALWARE: "#00ff99", BRUTE: "#bc13fe", WEB: "#ffcc00", DEFAULT: "#00cccc" };
            let attackType = threat.type;
            if (!attackType) {
                if (threat.count > 500) attackType = "DDOS";
                else if (threat.count > 200) attackType = "BRUTE";
                else attackType = "SUSPICIOUS";
            }

            if (attackType === "DDOS") return { key: "DDOS", type: "VOLUMETRIC DDoS", color: palette.DDOS, icon: "🔥" };
            if (attackType === "MALWARE") return { key: "MALWARE", type: "MALWARE / BOTNET", color: palette.MALWARE, icon: "🦠" };
            if (attackType === "BRUTE" || attackType === "BRUTEFORCE") return { key: "BRUTEFORCE", type: "SSH BRUTEFORCE", color: palette.BRUTE, icon: "🔓" };
            if (attackType === "WEB" || attackType === "WEB_ATTACK") return { key: "WEB_ATTACK", type: "WEB INJECTION", color: palette.WEB, icon: "💉" };
            return { key: "SUSPICIOUS", type: "SUSPICIOUS TRAFFIC", color: palette.DEFAULT, icon: "⚠" };
        };

        const getSeverityFromCount = (count) => {
            if (count > 500) return "CRITICAL";
            if (count > 200) return "HIGH";
            if (count > 80) return "MEDIUM";
            return "LOW";
        };

        const getSeverityColor = (severity) => ({
            CRITICAL: "var(--red)",
            HIGH: "var(--yellow)",
            MEDIUM: "#00d1ff",
            LOW: "var(--green)"
        }[severity] || "var(--cyan)");

        const makeSignature = (threat, cfg) => `${threat.ip}|${threat.count}|${cfg.key}|${threat.countryCode || threat.country || "UNK"}`;

        const rememberSignature = (signature) => {
            if (seenSignatures.has(signature)) return false;
            seenSignatures.add(signature);
            seenSignatureOrder.push(signature);
            while (seenSignatureOrder.length > MAX_SIGNATURES) {
                const expired = seenSignatureOrder.shift();
                if (expired) seenSignatures.delete(expired);
            }
            return true;
        };

        const decorateThreat = (threat, receivedAt = new Date().toISOString()) => {
            const cfg = getAttackConfig(threat);
            const severity = getSeverityFromCount(threat.count || 0);
            return {
                ...threat,
                signature: makeSignature(threat, cfg),
                receivedAt,
                cc: (threat.countryCode || "xx").toLowerCase(),
                countryLabel: threat.country || threat.countryCode || "UNK",
                typeKey: cfg.key,
                typeLabel: cfg.type,
                color: cfg.color,
                icon: cfg.icon,
                severity,
                severityColor: getSeverityColor(severity)
            };
        };

        const formatStamp = (value) => value
            ? new Date(value).toLocaleString("id-ID", {
                year: "numeric",
                month: "short",
                day: "2-digit",
                hour: "2-digit",
                minute: "2-digit",
                second: "2-digit"
            })
            : "--";

        const formatCount = (value) => Number(value || 0).toLocaleString("id-ID");

        const refreshCountryStats = () => {
            const countryMap = {};
            filteredThreatLog.value.forEach((threat) => {
                const key = threat.countryLabel || "UNK";
                if (!countryMap[key]) countryMap[key] = { country: key, count: 0, cc: threat.cc || "xx" };
                countryMap[key].count += threat.count || 0;
            });

            const sorted = Object.values(countryMap).sort((a, b) => b.count - a.count).slice(0, 6);
            const maxVal = sorted[0]?.count || 1;
            countryStats.value = sorted.map((item) => ({
                ...item,
                percent: (item.count / maxVal) * 100,
                color: item.count / maxVal > 0.8 ? "var(--red)" : "var(--cyan)"
            }));
        };

        const renderThreatLabels = () => {
            if (!world) return;
            world.htmlElementsData(primaryLabelThreats.value.map((threat) => ({
                lat: threat.lat,
                lon: threat.lon,
                ip: threat.ip,
                severity: threat.severity,
                color: threat.color
            })));
        };

        const refreshIntelEventStats = (ip) => {
            const related = eventHistory.value.filter((event) => event.ip === ip);
            const latest = related[0];
            const oldest = related[related.length - 1];
            intel.firstSeen = oldest?.receivedAt || null;
            intel.lastSeen = latest?.receivedAt || null;
            intel.hitCount = latest?.count || 0;
            intel.eventCount = related.length;
            if (latest) {
                intel.typeLabel = latest.typeLabel;
                intel.typeColor = latest.color;
                intel.severity = latest.severity;
                intel.severityColor = latest.severityColor;
            }
        };

        const updateIntel = (payload, context = {}) => {
            intel.cc = (payload.countryCode || context.countryCode || "xx").toLowerCase();
            intel.ip = payload.query || payload.ip || intel.ip;
            intel.country = payload.country || context.countryLabel || intel.country;
            intel.city = [payload.city, payload.regionName].filter(Boolean).join(", ") || payload.country || context.countryLabel || intel.city;
            intel.coord = typeof payload.lat === "number" && typeof payload.lon === "number" ? `${payload.lat}, ${payload.lon}` : intel.coord;
            intel.isp = payload.isp || intel.isp || "-";
            intel.org = payload.org || payload.isp || intel.org || "-";
            intel.asn = payload.as || intel.asn || "-";
            intel.timezone = payload.timezone || intel.timezone || "-";
            if (context.typeLabel) intel.typeLabel = context.typeLabel;
            if (context.typeColor) intel.typeColor = context.typeColor;
            if (context.severity) intel.severity = context.severity;
            if (context.severityColor) intel.severityColor = context.severityColor;
            intel.note = context.note || intel.note;
            refreshIntelEventStats(intel.ip);
        };

        const loadGeoTrace = async (ip) => {
            if (!ip || !LOCAL_TOKEN) return null;
            if (traceCache.has(ip)) return traceCache.get(ip);
            try {
                const response = await fetch(`/api/geo-trace?ip=${ip}`, { headers: { "X-API-KEY": LOCAL_TOKEN } });
                const data = await response.json();
                if (data.status === "success") {
                    traceCache.set(ip, data);
                    return data;
                }
            } catch (e) {
            }
            return null;
        };

        const syncHud = (payload) => {
            hud.visible = true;
            hud.ip = payload.query || payload.ip || "READY";
            hud.city = [payload.city, payload.countryCode || payload.country].filter(Boolean).join(", ") || payload.countryLabel || "---";
            hud.coord = typeof payload.lat === "number" && typeof payload.lon === "number" ? `${payload.lat}, ${payload.lon}` : "---";
        };

        const focusOnThreat = (payload, duration = 1600) => {
            if (world && typeof payload.lat === "number" && typeof payload.lon === "number") {
                world.pointOfView({ lat: payload.lat, lng: payload.lon, altitude: 1.28 }, duration);
            }
        };

        const launchProjectile = (startLat, startLng, endLat, endLng, color) => {
            if (!world) return;
            const startPos = world.getCoords(startLat, startLng, 0.5);
            const endPos = world.getCoords(endLat, endLng, 0.1);
            const vStart = new THREE.Vector3(startPos.x, startPos.y, startPos.z);
            const vEnd = new THREE.Vector3(endPos.x, endPos.y, endPos.z);
            const distance = vStart.distanceTo(vEnd);
            const mid = vStart.clone().add(vEnd).multiplyScalar(0.5).normalize().multiplyScalar(100 + (distance * 0.8));
            const curve = new THREE.QuadraticBezierCurve3(vStart, mid, vEnd);
            const tubeGeo = new THREE.TubeGeometry(curve, 12, 0.15, 3, false);
            const tubeMat = new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.9, blending: THREE.AdditiveBlending, depthWrite: false });
            const trailMesh = new THREE.Mesh(tubeGeo, tubeMat);
            trailMesh.geometry.setDrawRange(0, 0);
            world.scene().add(trailMesh);
            activeProjectiles.push({ curve, progress: 0, speed: 0.03 + (Math.random() * 0.02), color, trail: trailMesh, maxIndex: tubeGeo.index.count });
        };

        const queueArc = (threat) => {
            activeArcs.unshift({
                startLat: threat.lat,
                startLng: threat.lon,
                endLat: HOME_LAT,
                endLng: HOME_LON,
                color: [threat.color, "rgba(118, 228, 240, 0.95)"],
                stroke: threat.severity === "CRITICAL" ? 0.95 : threat.severity === "HIGH" ? 0.75 : 0.55,
                altitude: threat.severity === "CRITICAL" ? 0.28 : threat.severity === "HIGH" ? 0.24 : 0.18,
                dashLength: threat.severity === "CRITICAL" ? 0.55 : 0.42,
                dashGap: threat.severity === "CRITICAL" ? 1.1 : 1.6,
                animateTime: threat.severity === "CRITICAL" ? 1800 : 2500
            });
            activeArcs = activeArcs.slice(0, 18);
            if (world) world.arcsData(activeArcs);
        };

        const createShockwave = (position, color) => {
            const mesh = new THREE.Mesh(new THREE.RingGeometry(0.1, 0.3, 16), new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.4, side: THREE.DoubleSide }));
            mesh.position.copy(position);
            mesh.lookAt(new THREE.Vector3(0, 0, 0));
            world.scene().add(mesh);
            activeShockwaves.push({ mesh, scale: 1, opacity: 0.8 });
        };

        const createExplosion = (position, color) => {
            const mesh = new THREE.Mesh(new THREE.IcosahedronGeometry(0.5, 1), new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.7 }));
            mesh.position.copy(position);
            world.scene().add(mesh);
            activeExplosions.push({ mesh, scale: 1, opacity: 1 });
        };

        const animateWarRoom = () => {
            animationId = requestAnimationFrame(animateWarRoom);

            if (sunLight) {
                const time = Date.now() * 0.0001;
                sunLight.position.x = Math.cos(time) * 200;
                sunLight.position.z = Math.sin(time) * 200;
            }

            for (let i = activeProjectiles.length - 1; i >= 0; i--) {
                const projectile = activeProjectiles[i];
                projectile.progress += projectile.speed;
                if (projectile.trail) projectile.trail.geometry.setDrawRange(0, Math.floor(projectile.progress * projectile.maxIndex));
                if (projectile.progress >= 1) {
                    const impactPos = projectile.curve.getPoint(1);
                    createExplosion(impactPos, projectile.color);
                    createShockwave(impactPos, projectile.color);
                    if (projectile.trail) {
                        projectile.trail.geometry.setDrawRange(0, projectile.maxIndex);
                        activeContrails.push({ mesh: projectile.trail, opacity: 0.9 });
                    }
                    activeProjectiles.splice(i, 1);
                }
            }

            for (let i = activeExplosions.length - 1; i >= 0; i--) {
                const explosion = activeExplosions[i];
                explosion.scale += 0.2;
                explosion.opacity -= 0.03;
                explosion.mesh.scale.set(explosion.scale, explosion.scale, explosion.scale);
                explosion.mesh.material.opacity = explosion.opacity;
                if (explosion.opacity <= 0) {
                    world.scene().remove(explosion.mesh);
                    explosion.mesh.geometry.dispose();
                    explosion.mesh.material.dispose();
                    activeExplosions.splice(i, 1);
                }
            }

            for (let i = activeContrails.length - 1; i >= 0; i--) {
                const contrail = activeContrails[i];
                contrail.opacity -= 0.02;
                contrail.mesh.material.opacity = contrail.opacity;
                if (contrail.opacity <= 0) {
                    world.scene().remove(contrail.mesh);
                    contrail.mesh.geometry.dispose();
                    contrail.mesh.material.dispose();
                    activeContrails.splice(i, 1);
                }
            }

            for (let i = activeShockwaves.length - 1; i >= 0; i--) {
                const shockwave = activeShockwaves[i];
                shockwave.scale += 0.15;
                shockwave.opacity -= 0.02;
                shockwave.mesh.scale.set(shockwave.scale, shockwave.scale, shockwave.scale);
                shockwave.mesh.material.opacity = shockwave.opacity;
                if (shockwave.opacity <= 0) {
                    world.scene().remove(shockwave.mesh);
                    shockwave.mesh.geometry.dispose();
                    shockwave.mesh.material.dispose();
                    activeShockwaves.splice(i, 1);
                }
            }
        };

        const initGlobe = () => {
            const container = document.querySelector("#globeViz");
            world = Globe()(container)
                .globeImageUrl("//unpkg.com/three-globe/example/img/earth-dark.jpg")
                .bumpImageUrl("//unpkg.com/three-globe/example/img/earth-topology.png")
                .backgroundColor("rgba(0,0,0,0)")
                .atmosphereColor("#57d2ea")
                .atmosphereAltitude(0.12)
                .arcColor("color")
                .arcDashLength("dashLength")
                .arcDashGap("dashGap")
                .arcDashAnimateTime("animateTime")
                .arcStroke("stroke")
                .arcAltitude("altitude")
                .arcAltitudeAutoScale(0.16)
                .arcDashInitialGap(() => Math.random() * 0.9)
                .ringsData([])
                .ringColor("color")
                .ringMaxRadius(4)
                .ringPropagationSpeed(2.2)
                .ringRepeatPeriod(900)
                .htmlElementsData([])
                .htmlLat("lat")
                .htmlLng("lon")
                .htmlAltitude(() => 0.02)
                .htmlElement((d) => {
                    const wrapper = document.createElement("div");
                    wrapper.className = "globe-label-node";
                    const chip = document.createElement("div");
                    chip.className = "globe-ip-label";
                    chip.style.setProperty("--label-color", d.color);
                    chip.innerHTML = `<strong>${d.ip}</strong><span>${d.severity}</span>`;
                    wrapper.appendChild(chip);
                    return wrapper;
                })
                .width(container.offsetWidth)
                .height(container.offsetHeight)
                .showGraticules(false);

            const scene = world.scene();
            const starGeo = new THREE.BufferGeometry();
            const starPos = [];
            for (let i = 0; i < 950; i++) starPos.push((Math.random() - 0.5) * 2000, (Math.random() - 0.5) * 2000, (Math.random() - 0.5) * 2000);
            starGeo.setAttribute("position", new THREE.Float32BufferAttribute(starPos, 3));
            scene.add(new THREE.Points(starGeo, new THREE.PointsMaterial({ color: 0xb8ecff, size: 0.58, transparent: true, opacity: 0.72 })));

            scene.children.filter((obj) => obj.type === "DirectionalLight" || obj.type === "AmbientLight").forEach((light) => scene.remove(light));
            const globeMaterial = world.globeMaterial();
            globeMaterial.color = new THREE.Color("#143345");
            globeMaterial.emissive = new THREE.Color("#07141c");
            globeMaterial.emissiveIntensity = 0.35;
            globeMaterial.shininess = 0.8;
            if ("specular" in globeMaterial) globeMaterial.specular = new THREE.Color("#204357");

            sunLight = new THREE.DirectionalLight(0xcaf6ff, 1.35);
            sunLight.position.set(150, 30, 50);
            scene.add(sunLight);
            scene.add(new THREE.AmbientLight(0x203040, 0.95));
            scene.add(new THREE.HemisphereLight(0x8feaff, 0x041018, 0.72));

            world.controls().autoRotate = true;
            world.controls().autoRotateSpeed = 0.24;
            world.controls().enableDamping = true;
            world.controls().dampingFactor = 0.06;
            world.controls().minDistance = 140;
            world.controls().maxDistance = 340;
            world.pointOfView({ lat: HOME_LAT, lng: HOME_LON, altitude: 2.15 });
            window.addEventListener("resize", () => {
                if (!world) return;
                world.width(container.offsetWidth);
                world.height(container.offsetHeight);
            });
            animateWarRoom();
        };

        const pushToast = (threat) => {
            const id = `${Date.now()}_${Math.random()}`;
            toasts.value.push({ id, ip: threat.ip, type: threat.typeLabel, color: threat.color, icon: threat.icon, cc: threat.cc, severity: threat.severity });
            setTimeout(() => {
                toasts.value = toasts.value.filter((toast) => toast.id !== id);
            }, 4000);
        };

        const syncIntelFromThreat = async (threat, pin = false, note = null) => {
            if (!threat) return;
            if (pin) {
                pinnedTarget.enabled = true;
                pinnedTarget.ip = threat.ip;
            }

            syncHud(threat);
            focusOnThreat(threat);
            playLockSound();

            const trace = await loadGeoTrace(threat.ip);
            if (trace) {
                updateIntel(trace, {
                    countryCode: trace.countryCode || threat.cc,
                    countryLabel: trace.country || threat.countryLabel,
                    typeLabel: threat.typeLabel,
                    typeColor: threat.color,
                    severity: threat.severity,
                    severityColor: threat.severityColor,
                    note: note || (pin ? "Pinned target aktif. Dossier ini tidak akan tergantikan oleh event live lain." : "Threat selected from feed. Pin it if you want to keep focus here.")
                });
                syncHud(trace);
            } else {
                updateIntel(threat, {
                    countryCode: threat.cc,
                    countryLabel: threat.countryLabel,
                    typeLabel: threat.typeLabel,
                    typeColor: threat.color,
                    severity: threat.severity,
                    severityColor: threat.severityColor,
                    note: note || "Threat selected from feed. Pin it if you want to keep focus here."
                });
            }
        };

        const processThreatData = async (data) => {
            if (!world || !Array.isArray(data) || !data.length) return;

            const receivedAt = new Date().toISOString();
            const freshEvents = [];

            data.map((threat) => decorateThreat(threat, receivedAt)).forEach((threat) => {
                if (!rememberSignature(threat.signature)) return;
                freshEvents.push(threat);
            });

            if (!freshEvents.length) return;

            threatLog.value = [...freshEvents, ...threatLog.value].slice(0, MAX_FEED);
            eventHistory.value = [...freshEvents, ...eventHistory.value].slice(0, MAX_HISTORY);

            const visibleFresh = freshEvents.filter(matchesFilters);
            const countries = new Set();
            let highestCount = 0;

            visibleFresh.forEach((threat) => {
                queueArc(threat);
                launchProjectile(threat.lat, threat.lon, HOME_LAT, HOME_LON, threat.color);
                countries.add(threat.countryLabel);
                highestCount = Math.max(highestCount, threat.count || 0);
                pushToast(threat);
            });

            renderThreatLabels();
            refreshCountryStats();

            if (visibleFresh.length) {
                highestCount > 500 ? playAlarm(true) : (playAlarm(false), playSonar(visibleFresh[0].lon));
                speakThreat(countries);
            } else {
                playAlarm(false);
            }

            if (pinnedTarget.enabled && pinnedTarget.ip) {
                const pinned = eventHistory.value.find((event) => event.ip === pinnedTarget.ip);
                if (pinned) {
                    await syncIntelFromThreat(pinned, false, "Pinned target aktif. Dossier tetap mengikuti target ini selama live feed berjalan.");
                }
            } else if (freshEvents[0]) {
                await syncIntelFromThreat(freshEvents[0], false, "Fresh alert selected automatically from the newest unique event.");
            }
        };

        const initSignalR = () => {
            connection = new signalR.HubConnectionBuilder().withUrl("/threatHub").withAutomaticReconnect().build();

            connection.on("ReceiveThreats", async (data) => {
                await processThreatData(data);
            });

            connection.onreconnecting(() => {
                statusText.value = "● RECONNECTING SOCKET...";
                statusColor.value = "var(--yellow)";
            });

            connection.onreconnected(() => {
                statusText.value = "● LIVE FEED (SOCKET ACTIVE)";
                statusColor.value = "var(--red)";
            });

            connection.onclose(() => {
                if (isAutoHunt.value) {
                    statusText.value = "● SOCKET DEAD";
                    statusColor.value = "#666";
                }
            });

            pingInterval = setInterval(async () => {
                if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
                const started = Date.now();
                try {
                    await connection.invoke("Ping");
                    latencyMs.value = Date.now() - started;
                } catch (e) {
                }
            }, 2000);
        };

        const toggleLiveMode = async () => {
            if (!connection) return;

            if (connection.state === signalR.HubConnectionState.Connected) {
                await connection.stop();
                isAutoHunt.value = false;
                statusText.value = "● SYSTEM READY (MANUAL)";
                statusColor.value = "var(--green)";
                hud.visible = true;
                return;
            }

            try {
                statusText.value = "● INITIALIZING SOCKET...";
                statusColor.value = "var(--yellow)";
                await connection.start();
                isAutoHunt.value = true;
                statusText.value = "● LIVE FEED (SOCKET ACTIVE)";
                statusColor.value = "var(--red)";
            } catch (e) {
                statusText.value = "● CONNECTION FAILED";
                statusColor.value = "var(--red)";
            }
        };

        const manualTrace = async () => {
            if (!ipInput.value) return;
            hud.visible = true;
            hud.ip = "TRACING...";
            hud.city = "---";
            hud.coord = "---";

            const trace = await loadGeoTrace(ipInput.value);
            if (!trace) {
                alert("Trace Failed");
                return;
            }

            const tracedThreat = decorateThreat({
                ip: trace.query,
                lat: trace.lat,
                lon: trace.lon,
                country: trace.country,
                countryCode: trace.countryCode,
                type: "MANUAL_TRACE",
                count: 1
            });

            launchProjectile(trace.lat, trace.lon, HOME_LAT, HOME_LON, tracedThreat.color);
            await syncIntelFromThreat(tracedThreat, false, "Manual trace loaded. Pin this target if you want to keep it in focus.");
        };

        const inspectIp = async (targetIp, lat, lon) => {
            await syncIntelFromThreat(
                { ip: targetIp, lat, lon },
                false,
                pinnedTarget.enabled && pinnedTarget.ip === targetIp
                    ? "Pinned target aktif. Dossier ini akan tetap terkunci."
                    : "Live inspection loaded from feed selection."
            );
        };

        const inspectThreat = async (threat, pin = false) => {
            await syncIntelFromThreat(threat, pin);
        };

        const clearFilters = () => {
            filters.severity = "ALL";
            filters.type = "ALL";
            filters.country = "ALL";
        };

        const stopReplayPlayback = () => {
            replay.isPlaying = false;
            if (replayInterval) {
                clearInterval(replayInterval);
                replayInterval = null;
            }
        };

        const replayThreat = async (event) => {
            if (!event) return;
            launchProjectile(event.lat, event.lon, HOME_LAT, HOME_LON, event.color);
            await syncIntelFromThreat(event, false, "Replay mode active. Use pin if this target needs persistent focus.");
        };

        const selectReplayEvent = async (event) => {
            const index = replayTimeline.value.findIndex((item) => item.signature === event.signature && item.receivedAt === event.receivedAt);
            if (index >= 0) replay.index = index;
            await replayThreat(event);
        };

        const toggleReplayPlayback = () => {
            if (replay.isPlaying) {
                stopReplayPlayback();
                return;
            }
            if (!replayTimeline.value.length) return;

            replay.isPlaying = true;
            replayInterval = setInterval(async () => {
                if (!replayTimeline.value.length) {
                    stopReplayPlayback();
                    return;
                }
                replay.index = replay.index >= replayTimeline.value.length - 1 ? 0 : replay.index + 1;
                await replayThreat(replayTimeline.value[replay.index]);
            }, 2400);
        };

        const stepReplay = async (direction) => {
            if (!replayTimeline.value.length) return;
            stopReplayPlayback();
            const maxIndex = replayTimeline.value.length - 1;
            replay.index = Math.min(Math.max(replay.index + direction, 0), maxIndex);
            await replayThreat(replayTimeline.value[replay.index]);
        };

        const togglePinTarget = () => {
            if (!intel.ip || intel.ip === "READY") return;
            pinnedTarget.enabled = !pinnedTarget.enabled;
            pinnedTarget.ip = pinnedTarget.enabled ? intel.ip : null;
            intel.note = pinnedTarget.enabled
                ? "Pinned target aktif. Feed live tetap berjalan, tetapi dossier akan menjaga fokus ini."
                : "Pin dilepas. Dossier akan mengikuti target yang Anda pilih berikutnya.";
        };

        const clearPinTarget = () => {
            pinnedTarget.enabled = false;
            pinnedTarget.ip = null;
            intel.note = "Pin dilepas. Pilih target baru atau gunakan replay untuk melanjutkan investigasi.";
        };

        const resetGlobe = () => {
            if (world) {
                world.arcsData([]);
                world.ringsData([]);
                world.htmlElementsData([]);
            }
            activeArcs = [];
            hud.ip = "CLEARED";
            hud.city = "---";
            hud.coord = "---";
            clearPinTarget();
            stopReplayPlayback();
        };

        const handleKeydown = (event) => {
            if (document.activeElement.tagName === "INPUT") return;
            const key = event.key.toLowerCase();
            if (key === "m") toggleAudio();
            if (key === "h") toggleLiveMode();
            if (key === "c") resetGlobe();
            if (key === "p") togglePinTarget();
            if (key === "r") toggleReplayPlayback();
            if (key === "v") setViewMode(viewMode.value === "analytic" ? "dashboard" : "analytic");
            if (key === "f") {
                if (!document.fullscreenElement) document.documentElement.requestFullscreen();
                else document.exitFullscreen();
            }
        };

        watch(() => [filters.severity, filters.type, filters.country], () => {
            refreshCountryStats();
            renderThreatLabels();
            if (!replayTimeline.value.length) {
                replay.index = 0;
                stopReplayPlayback();
            } else if (replay.index > replayTimeline.value.length - 1) {
                replay.index = replayTimeline.value.length - 1;
            }
        });

        watch(primaryLabelThreats, () => {
            renderThreatLabels();
        });

        onMounted(() => {
            initGlobe();
            initSignalR();
            document.addEventListener("keydown", handleKeydown);
            if (isAudioEnabled.value) Howler.mute(false);
            refreshCountryStats();
        });

        onUnmounted(() => {
            if (animationId) cancelAnimationFrame(animationId);
            if (pingInterval) clearInterval(pingInterval);
            stopReplayPlayback();
            if (alarmCooldownTimer) clearTimeout(alarmCooldownTimer);
            document.removeEventListener("keydown", handleKeydown);
            if (connection) connection.stop();
        });

        return {
            isAudioEnabled,
            isAutoHunt,
            ipInput,
            latencyMs,
            statusText,
            statusColor,
            latencyColor,
            viewMode,
            hud,
            intel,
            filters,
            pinnedTarget,
            replay,
            replayTimeline,
            replayCurrentEvent,
            threatLog,
            filteredThreatLog,
            filteredEventHistory,
            countryStats,
            toasts,
            severityOptions,
            typeOptions,
            countryOptions,
            legendItems,
            guidanceTips,
            toggleAudio,
            toggleLiveMode,
            manualTrace,
            resetGlobe,
            inspectIp,
            inspectThreat,
            unlockAudio,
            clearFilters,
            selectReplayEvent,
            replayThreat,
            toggleReplayPlayback,
            stepReplay,
            togglePinTarget,
            clearPinTarget,
            setViewMode,
            formatStamp,
            formatCount
        };
    }
}).mount("#app");
