using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Services;

public sealed record GeoMatch(double Latitude, double Longitude, string Label, string Method);

// A Google answer that makes further lookups pointless (bad key, API not enabled, quota used up).
public sealed class GeocoderException(string message) : Exception(message);

public sealed record LocationLookupStatus(
    bool Configured, bool Running, string? Scope, int Total, int Done, int Found, int NotFound, int Failed,
    string? LastError, DateTime? StartedAt, DateTime? FinishedAt, string? StartedBy);

// Finds customers on Google from their name, district and city (Places text search) and, failing that, from their
// address (Geocoding). Only the customer's name and address are sent. What is found is saved as not verified; a
// verified location is never replaced by a lookup.
public static class CustomerGeocoder
{
    public const string KeyVariable = "GOOGLE_MAPS_API_KEY";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static string? ApiKey => Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } key ? key.Trim() : null;

    public static bool Configured => ApiKey is not null;

    // Places is skipped for a while after Google says it is not enabled for the key, so Geocoding alone keeps working.
    private static DateTime _placesOffUntil = DateTime.MinValue;

    public static async Task<GeoMatch?> FindAsync(Customer customer, CancellationToken cancel = default)
    {
        string key = ApiKey ?? throw new GeocoderException("Google anahtarı tanımlı değil (GOOGLE_MAPS_API_KEY).");
        string city = (customer.City ?? string.Empty).Trim();
        string district = (customer.District ?? string.Empty).Trim();
        if (city.Length == 0)
        {
            return null;
        }

        if (DateTime.UtcNow >= _placesOffUntil && !string.IsNullOrWhiteSpace(customer.CompanyName))
        {
            string query = string.Join(", ", new[] { customer.CompanyName.Trim(), district, city }.Where(x => x.Length > 0));
            GeoMatch? place = await PlaceAsync(key, query, city, district, cancel);
            if (place is not null)
            {
                return place;
            }
        }

        string address = (customer.Address ?? string.Empty).Trim();
        if (address.Length < 5)
        {
            return null;
        }

        string full = string.Join(", ", new[] { address, district, city, "Türkiye" }.Where(x => x.Length > 0));
        return await GeocodeAsync(key, full, city, cancel);
    }

    private static async Task<GeoMatch?> PlaceAsync(string key, string query, string city, string district, CancellationToken cancel)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText");
        request.Headers.Add("X-Goog-Api-Key", key);
        request.Headers.Add("X-Goog-FieldMask", "places.displayName,places.formattedAddress,places.location");
        request.Content = JsonContent.Create(new
        {
            textQuery = query,
            languageCode = "tr",
            regionCode = "TR",
            locationRestriction = new { rectangle = new { low = new { latitude = 35.8, longitude = 25.6 }, high = new { latitude = 42.2, longitude = 44.9 } } }
        });
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            string message = ErrorMessage(text);
            if (message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase))
            {
                throw new GeocoderException("Google anahtarı geçersiz.");
            }

            _placesOffUntil = DateTime.UtcNow.AddMinutes(30);
            return null;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new GeocoderException("Google Places kotası doldu; daha sonra tekrar deneyin.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google Places {(int)response.StatusCode}: {ErrorMessage(text)}");
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("places", out JsonElement places))
        {
            return null;
        }

        foreach (JsonElement place in places.EnumerateArray().Take(3))
        {
            if (!place.TryGetProperty("location", out JsonElement loc))
            {
                continue;
            }

            double lat = loc.GetProperty("latitude").GetDouble();
            double lng = loc.GetProperty("longitude").GetDouble();
            string address = place.TryGetProperty("formattedAddress", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
            string name = place.TryGetProperty("displayName", out JsonElement d) && d.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
            // A shop with the same name elsewhere in the city is not taken: the district has to match too.
            if (TripPlans.InTurkey(lat, lng) && Mentions(address, city) && (district.Length == 0 || Mentions(address, district)))
            {
                return new GeoMatch(lat, lng, name.Length > 0 ? $"{name} — {address}" : address, "isim");
            }
        }

        return null;
    }

    // Answers that only pin the city, district or postcode are useless for an approach warning.
    private static readonly HashSet<string> CoarseTypes =
        ["country", "administrative_area_level_1", "administrative_area_level_2", "administrative_area_level_3", "locality", "postal_code", "political"];

    private static async Task<GeoMatch?> GeocodeAsync(string key, string address, string city, CancellationToken cancel)
    {
        string url = "https://maps.googleapis.com/maps/api/geocode/json?language=tr&region=tr&components=country:TR&address="
            + Uri.EscapeDataString(address) + "&key=" + Uri.EscapeDataString(key);
        using HttpResponseMessage response = await Http.GetAsync(url, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google Geocoding {(int)response.StatusCode}");
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        string status = doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
        switch (status)
        {
            case "OK":
                break;
            case "ZERO_RESULTS":
                return null;
            case "REQUEST_DENIED":
                string detail = doc.RootElement.TryGetProperty("error_message", out JsonElement e) ? e.GetString() ?? string.Empty : string.Empty;
                throw new GeocoderException(detail.Contains("not authorized", StringComparison.OrdinalIgnoreCase) || detail.Contains("not activated", StringComparison.OrdinalIgnoreCase)
                    ? "Google anahtarında Geocoding API etkin değil."
                    : "Google isteği reddetti; anahtarı ve kısıtlamalarını kontrol edin.");
            case "OVER_QUERY_LIMIT":
            case "OVER_DAILY_LIMIT":
                throw new GeocoderException("Google Geocoding kotası doldu; daha sonra tekrar deneyin.");
            default:
                throw new HttpRequestException($"Google Geocoding: {status}");
        }

        foreach (JsonElement result in doc.RootElement.GetProperty("results").EnumerateArray().Take(3))
        {
            List<string> types = result.TryGetProperty("types", out JsonElement ty) ? ty.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : [];
            if (types.Count > 0 && types.All(CoarseTypes.Contains))
            {
                continue;
            }

            JsonElement loc = result.GetProperty("geometry").GetProperty("location");
            double lat = loc.GetProperty("lat").GetDouble();
            double lng = loc.GetProperty("lng").GetDouble();
            string label = result.TryGetProperty("formatted_address", out JsonElement f) ? f.GetString() ?? string.Empty : string.Empty;
            if (TripPlans.InTurkey(lat, lng) && Mentions(label, city))
            {
                return new GeoMatch(lat, lng, label, "adres");
            }
        }

        return null;
    }

    private static string ErrorMessage(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out JsonElement e) && e.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // "Sarıçam/Adana" in Google's answer for city "ADANA" or "adana": compared without case and Turkish letters.
    public static bool Mentions(string text, string part)
    {
        string p = Fold(part);
        return p.Length > 0 && Fold(text).Contains(p, StringComparison.Ordinal);
    }

    public static string Fold(string? text)
    {
        StringBuilder sb = new();
        foreach (char ch in (text ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("tr-TR")))
        {
            sb.Append(ch switch
            {
                'İ' or 'I' => 'I',
                'Ş' => 'S',
                'Ğ' => 'G',
                'Ü' => 'U',
                'Ö' => 'O',
                'Ç' => 'C',
                'Â' => 'A',
                'Î' => 'I',
                'Û' => 'U',
                _ => ch
            });
        }

        return sb.ToString();
    }

    // ---- Lookup for many customers in the background ----

    private static readonly object Gate = new();
    private static LocationLookupStatus _status = new(false, false, null, 0, 0, 0, 0, 0, null, null, null, null);
    private static CancellationTokenSource? _cancel;

    public static LocationLookupStatus Status
    {
        get
        {
            lock (Gate)
            {
                return _status with { Configured = Configured };
            }
        }
    }

    private static void Update(Func<LocationLookupStatus, LocationLookupStatus> change)
    {
        lock (Gate)
        {
            _status = change(_status);
        }
    }

    // Starts finding every active customer without a location (or only the given ones). False when a lookup is running.
    public static bool Start(IReadOnlyCollection<int>? customerIds, string scope, string by, Action<string> log)
    {
        if (!Configured)
        {
            throw new GeocoderException("Google anahtarı tanımlı değil (GOOGLE_MAPS_API_KEY).");
        }

        CancellationTokenSource cancel;
        lock (Gate)
        {
            if (_status.Running)
            {
                return false;
            }

            cancel = new CancellationTokenSource();
            _cancel = cancel;
            _status = new LocationLookupStatus(true, true, scope, 0, 0, 0, 0, 0, null, DateTime.Now, null, by);
        }

        _ = Task.Run(() => RunAsync(customerIds, by, log, cancel.Token));
        return true;
    }

    public static bool Stop()
    {
        lock (Gate)
        {
            if (!_status.Running || _cancel is null)
            {
                return false;
            }

            _cancel.Cancel();
            return true;
        }
    }

    private static async Task RunAsync(IReadOnlyCollection<int>? customerIds, string by, Action<string> log, CancellationToken cancel)
    {
        string? error = null;
        try
        {
            List<Customer> customers;
            await using (TupTakipDbContext db = new())
            {
                IQueryable<Customer> query = db.Customers.AsNoTracking()
                    .Where(c => c.IsActive && !db.CustomerLocations.Any(l => l.CustomerId == c.Id));
                if (customerIds is not null)
                {
                    List<int> ids = customerIds.ToList();
                    query = query.Where(c => ids.Contains(c.Id));
                }

                customers = await query.OrderBy(c => c.Id).ToListAsync(cancel);
            }

            Update(s => s with { Total = customers.Count });
            foreach (Customer customer in customers)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    GeoMatch? match = await FindAsync(customer, cancel);
                    if (match is null)
                    {
                        Update(s => s with { Done = s.Done + 1, NotFound = s.NotFound + 1 });
                    }
                    else
                    {
                        await TripPlans.SetFoundLocationAsync(customer.Id, match.Latitude, match.Longitude, match.Label, by, false);
                        Update(s => s with { Done = s.Done + 1, Found = s.Found + 1 });
                    }
                }
                catch (GeocoderException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ArgumentException && !cancel.IsCancellationRequested)
                {
                    Update(s => s with { Done = s.Done + 1, Failed = s.Failed + 1, LastError = ex.Message });
                    log($"Konum bulma #{customer.Id}: {ex.Message}");
                }

                await Task.Delay(150, cancel);
            }
        }
        catch (OperationCanceledException)
        {
            error = "Durduruldu.";
        }
        catch (GeocoderException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log($"Konum bulma durdu: {ex}");
        }
        finally
        {
            Update(s => s with { Running = false, FinishedAt = DateTime.Now, LastError = error ?? s.LastError });
            LocationLookupStatus done = Status;
            log($"Konum bulma bitti: {done.Done}/{done.Total}, bulunan {done.Found}, bulunamayan {done.NotFound}, hata {done.Failed}{(error is null ? "" : " — " + error)}");
        }
    }
}
