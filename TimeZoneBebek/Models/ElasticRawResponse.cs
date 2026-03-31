using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace TimeZoneBebek.Models
{
    // --- 1. ROOT AGGREGATION MAPPING ---
    public class ElasticRawResponse
    {
        [JsonPropertyName("aggregations")]
        public AggregationsData Aggregations { get; set; }
    }

    public class AggregationsData
    {
        [JsonPropertyName("top_attackers")]
        public TopAttackersAgg TopAttackers { get; set; }
    }

    public class TopAttackersAgg
    {
        [JsonPropertyName("buckets")]
        public List<BucketItem> Buckets { get; set; }
    }

    public class BucketItem
    {
        [JsonPropertyName("key")]
        public string Ip { get; set; }

        [JsonPropertyName("doc_count")]
        public long DocCount { get; set; }

        [JsonPropertyName("geo_details")]
        public GeoDetailsAgg GeoDetails { get; set; }
    }

    public class GeoDetailsAgg
    {
        [JsonPropertyName("hits")]
        public HitsContainer Hits { get; set; }
    }

    public class HitsContainer
    {
        [JsonPropertyName("hits")]
        public List<HitItem> HitList { get; set; }
    }

    public class HitItem
    {
        [JsonPropertyName("_source")]
        public SourceData Source { get; set; }
    }

    // --- 2. SOURCE DATA MAPPING (MENGAMBIL DARI JSON ANDA) ---
    public class SourceData
    {
        // Kibana Alerts
        [JsonPropertyName("kibana.alert.rule.name")]
        public string RuleName { get; set; }

        [JsonPropertyName("kibana.alert.rule.severity")]
        public string Severity { get; set; }

        // Data Source (Geo & ISP)
        [JsonPropertyName("source")]
        public SourceInfo SourceExt { get; set; }

        // Data Web Target
        [JsonPropertyName("host")]
        public HostInfo Host { get; set; }

        // Data Payload / Query
        [JsonPropertyName("url")]
        public UrlInfo Url { get; set; }

        // Data Status Code HTTP
        [JsonPropertyName("http")]
        public HttpInfo Http { get; set; }
    }

    // --- 3. NESTED CLASSES ---
    public class SourceInfo
    {
        [JsonPropertyName("geo")]
        public GeoInfo Geo { get; set; }

        // Karena 'as' adalah keyword bawaan C#, kita namakan property-nya 'As'
        [JsonPropertyName("as")]
        public AsInfo As { get; set; }
    }

    public class GeoInfo
    {
        [JsonPropertyName("country_iso_code")]
        public string CountryIsoCode { get; set; }

        [JsonPropertyName("country_name")]
        public string CountryName { get; set; }

        [JsonPropertyName("location")]
        public LocationInfo Location { get; set; }
    }

    public class LocationInfo
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }
    }

    public class AsInfo
    {
        [JsonPropertyName("organization")]
        public OrganizationInfo Organization { get; set; }
    }

    public class OrganizationInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class HostInfo
    {
        [JsonPropertyName("hostname")]
        public string Hostname { get; set; }
    }

    public class UrlInfo
    {
        [JsonPropertyName("query")]
        public string Query { get; set; }

        [JsonPropertyName("original")]
        public string Original { get; set; }
    }

    public class HttpInfo
    {
        [JsonPropertyName("response")]
        public HttpResponseInfo Response { get; set; }
    }

    public class HttpResponseInfo
    {
        // Perhatikan JSON Anda, status_code berbentuk angka (integer) yaitu 200, bukan string "200"
        [JsonPropertyName("status_code")]
        public int StatusCode { get; set; }
    }
}