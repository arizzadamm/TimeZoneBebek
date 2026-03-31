namespace TimeZoneBebek.Helpers
{
    public static class UIHelpers
    {
        public static string GetPreloader() => @"<div id='preloader'><div class='duck-loader'>🦆</div><div class='loading-text'>INITIALIZING SYSTEM...</div></div><script>window.addEventListener('load', ()=>{ setTimeout(()=>{ document.body.classList.add('loaded'); setTimeout(()=>{ document.getElementById('preloader').style.display='none';},800); },500); });</script>";

        public static string GetSidebar(string active) => $@"
        <script src='https://cdn.jsdelivr.net/npm/particles.js@2.0.0/particles.min.js'></script>
        <script src='https://unpkg.com/typed.js@2.0.16/dist/typed.umd.js'></script>
        <link rel='stylesheet' type='text/css' href='https://cdn.jsdelivr.net/npm/toastify-js/src/toastify.min.css'>
        <script type='text/javascript' src='https://cdn.jsdelivr.net/npm/toastify-js'></script>
        <div id='mySidebar' class='sidebar'>
            <div style='text-align:center; margin-bottom:20px; color:#fff; font-weight:bold;'>SYSTEM MENU</div>
            <a href='/' class='{(active == "home" ? "active" : "")}'>DASHBOARD</a>
            <a href='/portal' class='{(active == "portal" ? "active" : "")}'>APP PORTAL</a>
            <a href='/geo' class='{(active == "geo" ? "active" : "")}'>GEO TRACER</a>
            <a href='/monitor' class='{(active == "monitor" ? "active" : "")}'>SERVICE MONITOR</a>
            <a href='/archive' class='{(active == "archive" ? "active" : "")}'>INCIDENT ARCHIVE</a>
            <a href='/reports' class='{(active == "reports" ? "active" : "")}'>GENERATE REPORTS</a>
        </div>
        <div id='myOverlay' class='overlay' onclick='toggleNav()'></div>
        <span class='menu-btn' onclick='toggleNav()'>&#9776;</span>
        <script>
            function toggleNav(){{document.getElementById('mySidebar').classList.toggle('open');document.getElementById('myOverlay').classList.toggle('active');}}
            if(!document.getElementById('particles-js')) {{
                const p = document.createElement('div'); p.id = 'particles-js';
                p.style.position = 'fixed'; p.style.top = '0'; p.style.left = '0'; p.style.width = '100%'; p.style.height = '100%'; p.style.zIndex = '-2';
                document.body.appendChild(p);
                particlesJS('particles-js', {{ 'particles': {{ 'number': {{ 'value': 40 }}, 'color': {{ 'value': '#00ffcc' }}, 'shape': {{ 'type': 'circle' }}, 'opacity': {{ 'value': 0.3, 'random': false }}, 'size': {{ 'value': 2, 'random': true }}, 'line_linked': {{ 'enable': true, 'distance': 150, 'color': '#00ffcc', 'opacity': 0.2, 'width': 1 }}, 'move': {{ 'enable': true, 'speed': 1 }} }}, 'interactivity': {{ 'detect_on': 'canvas', 'events': {{ 'onhover': {{ 'enable': true, 'mode': 'grab' }} }} }} }});
            }}
        </script>";
    }
}