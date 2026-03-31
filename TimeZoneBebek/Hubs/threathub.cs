using Microsoft.AspNetCore.SignalR;

namespace TimeZoneBebek.Hubs
{
    public class ThreatHub : Hub
    {
        // Kita bisa kosongkan karena server yang akan aktif mengirim pesan (Push),
        // bukan client yang request.
    }
}
