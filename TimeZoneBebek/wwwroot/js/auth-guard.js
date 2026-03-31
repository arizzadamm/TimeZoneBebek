// auth-guard.js
const SAVED_KEY = localStorage.getItem("CSIRT_KEY");

if (!SAVED_KEY) {
    alert("ACCESS DENIED: Please Login First via Archive Dashboard.");
    window.location.href = "/archive";
}

const API_KEY = SAVED_KEY.trim();