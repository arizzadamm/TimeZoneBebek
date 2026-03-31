using System.Text.Json.Serialization;

namespace TimeZoneBebek.Models
{
       public class ThreatModel
        {
            // Sesuaikan nama property dengan field di ElasticSearch Anda
            // Gunakan atribut [PropertyName] jika nama di JSON beda (misal snake_case)
            public string? Ip { get; set; }
            public string? Country { get; set; }
            public string? City { get; set; }
            public string? Severity { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public int Count { get; set; } // Jumlah hits
            public string? CountryCode { get; set; }
            public bool IsNewEvent { get; set; }
            public string Type { get; set; } = "SUSPICIOUS";
            public string Organization { get; set; } = "Unknown";
            public string TargetWeb { get; set; } = "Unknown";
            public string AttackerQuery { get; set; } = "-";
            public string StatusCode { get; set; } = "0";
    }
    
}