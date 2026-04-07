namespace TimeZoneBebek.Helpers
{
    public static class UIHelpers
    {
        public static string GetPreloader() => @"
        <div id='preloader'>
            <div class='preloader-shell'>
                <div class='preloader-emblem'>
                    <div class='preloader-ring ring-a'></div>
                    <div class='preloader-ring ring-b'></div>
                    <div class='preloader-core'>
                        <div class='duck-loader'>🦆</div>
                    </div>
                    <div class='preloader-scan'></div>
                </div>
                <div class='preloader-copy'>
                    <div class='preloader-kicker'>BAPPENAS CSIRT</div>
                    <div class='preloader-title'>Secure Operations Console</div>
                    <div class='loading-text' id='preloader-status'>Initializing command surface...</div>
                </div>
                <div class='preloader-progress'>
                    <div class='preloader-progress-bar' id='preloader-progress-bar'></div>
                </div>
                <div class='preloader-bootlog' id='preloader-bootlog'>
                    <div class='bootlog-row active'><span>01</span><span>Authenticating session</span></div>
                    <div class='bootlog-row'><span>02</span><span>Syncing incident feed</span></div>
                    <div class='bootlog-row'><span>03</span><span>Priming geo telemetry</span></div>
                    <div class='bootlog-row'><span>04</span><span>Loading live surfaces</span></div>
                </div>
            </div>
        </div>
        <script>
            window.addEventListener('load', ()=>{
                const status = document.getElementById('preloader-status');
                const bar = document.getElementById('preloader-progress-bar');
                const bootRows = Array.from(document.querySelectorAll('#preloader-bootlog .bootlog-row'));
                const phases = [
                    { text: 'Authenticating secure session...', width: '24%' },
                    { text: 'Syncing incident feed...', width: '52%' },
                    { text: 'Priming geo telemetry...', width: '78%' },
                    { text: 'Loading command surface...', width: '100%' }
                ];
                phases.forEach((phase, index) => {
                    setTimeout(() => {
                        if (status) status.textContent = phase.text;
                        if (bar) bar.style.width = phase.width;
                        bootRows.forEach((row, rowIndex) => {
                            row.classList.toggle('active', rowIndex === index);
                            row.classList.toggle('complete', rowIndex < index);
                        });
                    }, index * 280);
                });
                setTimeout(()=>{
                    document.body.classList.add('loaded');
                    const preloader = document.getElementById('preloader');
                    if (preloader) {
                        preloader.style.opacity = '0';
                        setTimeout(()=>{ preloader.style.display='none'; }, 850);
                    }
                }, 1400);
            });
        </script>";

        public static string GetSidebar(string active) => $@"
        <script src='https://cdn.jsdelivr.net/npm/particles.js@2.0.0/particles.min.js'></script>
        <script src='https://unpkg.com/typed.js@2.0.16/dist/typed.umd.js'></script>
        <link rel='stylesheet' type='text/css' href='https://cdn.jsdelivr.net/npm/toastify-js/src/toastify.min.css'>
        <script type='text/javascript' src='https://cdn.jsdelivr.net/npm/toastify-js'></script>
        <div id='mySidebar' class='sidebar'>
            <div class='sidebar-shell'>
                <div class='sidebar-header'>
                    <div class='sidebar-kicker'>SOC NAVIGATION</div>
                    <div class='sidebar-title'>SYSTEM MENU</div>
                    <div class='sidebar-subtitle'>Unified command surface</div>
                </div>
                <nav class='sidebar-nav'>
                    <a href='/' class='sidebar-link {(active == "home" ? "active" : "")}' data-short='01'>
                        <span class='sidebar-link-meta'>Overview</span>
                        <span class='sidebar-link-label'>Dashboard</span>
                    </a>
                    <a href='/portal' class='sidebar-link {(active == "portal" ? "active" : "")}' data-short='02'>
                        <span class='sidebar-link-meta'>Workspace</span>
                        <span class='sidebar-link-label'>App Portal</span>
                    </a>
                    <a href='/geo' class='sidebar-link {(active == "geo" ? "active" : "")}' data-short='03'>
                        <span class='sidebar-link-meta'>Live tracing</span>
                        <span class='sidebar-link-label'>Geo Tracer</span>
                    </a>
                    <a href='/monitor' class='sidebar-link {(active == "monitor" ? "active" : "")}' data-short='04'>
                        <span class='sidebar-link-meta'>Telemetry</span>
                        <span class='sidebar-link-label'>Service Monitor</span>
                    </a>
                    <a href='/nms' class='sidebar-link {(active == "nms" ? "active" : "")}' data-short='05'>
                        <span class='sidebar-link-meta'>Network ops</span>
                        <span class='sidebar-link-label'>NMS Live</span>
                    </a>
                    <a href='/archive' class='sidebar-link {(active == "archive" ? "active" : "")}' data-short='06'>
                        <span class='sidebar-link-meta'>Actionable</span>
                        <span class='sidebar-link-label'>Incident Archive</span>
                    </a>
                    <a href='/reports' class='sidebar-link {(active == "reports" ? "active" : "")}' data-short='07'>
                        <span class='sidebar-link-meta'>Output</span>
                        <span class='sidebar-link-label'>Generate Reports</span>
                    </a>
                </nav>
            </div>
        </div>
        <div id='myOverlay' class='overlay' onclick='toggleNav()'></div>
        <button class='menu-btn' type='button' onclick='toggleNav()' aria-label='Toggle navigation'>
            <span></span><span></span><span></span>
        </button>
        <script>
            function toggleNav(){{
                const sidebar = document.getElementById('mySidebar');
                const overlay = document.getElementById('myOverlay');
                const button = document.querySelector('.menu-btn');
                if(!sidebar || !overlay || !button) return;
                sidebar.classList.toggle('open');
                overlay.classList.toggle('active');
                button.classList.toggle('open');
            }}
            if(!document.getElementById('particles-js')) {{
                const p = document.createElement('div'); p.id = 'particles-js';
                p.style.position = 'fixed'; p.style.top = '0'; p.style.left = '0'; p.style.width = '100%'; p.style.height = '100%'; p.style.zIndex = '-2';
                document.body.appendChild(p);
                particlesJS('particles-js', {{ 'particles': {{ 'number': {{ 'value': 40 }}, 'color': {{ 'value': '#00ffcc' }}, 'shape': {{ 'type': 'circle' }}, 'opacity': {{ 'value': 0.3, 'random': false }}, 'size': {{ 'value': 2, 'random': true }}, 'line_linked': {{ 'enable': true, 'distance': 150, 'color': '#00ffcc', 'opacity': 0.2, 'width': 1 }}, 'move': {{ 'enable': true, 'speed': 1 }} }}, 'interactivity': {{ 'detect_on': 'canvas', 'events': {{ 'onhover': {{ 'enable': true, 'mode': 'grab' }} }} }} }});
            }}
        </script>";
    }
}
