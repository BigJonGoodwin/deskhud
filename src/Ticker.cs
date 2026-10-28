// =================================================================================================
// Ticker.cs: the weather and network text in YASB's "ticker" widget.
//
// Every 5 s: sample network throughput, refresh the weather when due (Open-Meteo, no API key), and
// write ticker.json, which the YASB custom widget reads. The label is HTML spans with inline colours.
// Network arrows are green when that rate rose since the last sample and red when it fell.
// The weather city is "weather.city" in settings.json.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace DeskHud
{
    /// Feeds the YASB "ticker" custom widget through ticker.json: the weather icon stays put while the
    /// text alternates between temperature-with-trend and network throughput.
    sealed class Ticker : IDisposable
    {
        const int PhaseMs = 5000;

        /// The last forecast, cached in weather.json so the bar has weather straight after a restart.
        sealed class Weather
        {
            public string City = "", Place = "";
            public double Latitude, Longitude, Temperature;
            public int Code, Trend;
            public bool IsDay = true, Fahrenheit = true;
            public DateTime FetchedUtc;
        }

        readonly Func<Settings> settings;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = PhaseMs };
        PaletteColors colors;
        Weather weather;
        bool fetching, weatherPhase;
        long lastReceived = -1, lastSent = -1;
        DateTime lastSample;
        double receiveRate, sendRate;
        // Each arrow is green while its rate went up at the last sample, red while it went down.
        bool receiveRising = true, sendRising = true;
        string lastWritten;

        public Ticker(Func<Settings> settings, PaletteColors colors)
        {
            this.settings = settings;
            this.colors = colors;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            weather = LoadCache();
            timer.Tick += delegate { Tick(); };
        }

        public void Start()
        {
            Tick();
            timer.Start();
        }

        public void SetPalette(PaletteColors palette)
        {
            colors = palette;
            Write();
        }

        /// Fetches now, ignoring the refresh interval (click on the widget, or the city changed).
        public void Refresh()
        {
            FetchWeather(true);
        }

        void Tick()
        {
            SampleNetwork();
            FetchWeather(false);
            weatherPhase = !weatherPhase;
            Write();
        }

        void SampleNetwork()
        {
            long received = 0, sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Interfaces with a gateway carry internet traffic; this skips loopback and the WSL/Hyper-V switches.
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var properties = nic.GetIPProperties();
                if (properties.GatewayAddresses.Count == 0) continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            DateTime now = DateTime.UtcNow;
            if (lastReceived >= 0)
            {
                double seconds = Math.Max(0.5, (now - lastSample).TotalSeconds);
                double receive = Math.Max(0, received - lastReceived) / seconds;
                double send = Math.Max(0, sent - lastSent) / seconds;
                // An unchanged rate keeps its colour.
                if (receive != receiveRate) receiveRising = receive > receiveRate;
                if (send != sendRate) sendRising = send > sendRate;
                receiveRate = receive;
                sendRate = send;
            }
            lastReceived = received;
            lastSent = sent;
            lastSample = now;
        }

        void FetchWeather(bool force)
        {
            var current = settings();
            if (fetching || string.IsNullOrEmpty(current.WeatherCity)) return;

            bool sameRequest = weather != null
                && string.Equals(weather.City, current.WeatherCity, StringComparison.OrdinalIgnoreCase)
                && weather.Fahrenheit == current.Fahrenheit;
            if (!force && sameRequest && DateTime.UtcNow - weather.FetchedUtc < TimeSpan.FromHours(current.WeatherRefreshHours)) return;

            fetching = true;
            Weather previous = sameRequest ? weather : null;
            string city = current.WeatherCity;
            bool fahrenheit = current.Fahrenheit;
            Task.Factory.StartNew(() => Download(city, fahrenheit, previous)).ContinueWith(task =>
            {
                fetching = false;
                if (task.IsFaulted)
                {
                    Log.Error("weather", task.Exception.GetBaseException());
                    return;
                }
                if (task.Result == null) return;
                weather = task.Result;
                SaveCache(weather);
                Write();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static Weather Download(string city, bool fahrenheit, Weather previous)
        {
            var result = new Weather { City = city, Fahrenheit = fahrenheit, FetchedUtc = DateTime.UtcNow };
            using (var web = new WebClient())
            {
                web.Encoding = System.Text.Encoding.UTF8;
                if (previous != null)
                {
                    result.Latitude = previous.Latitude;
                    result.Longitude = previous.Longitude;
                    result.Place = previous.Place;
                }
                else if (!Geocode(web, city, result))
                {
                    Log.Write("weather: no place found for \"" + city + "\"");
                    return null;
                }

                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                    "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m&forecast_hours=4" +
                    "&temperature_unit={2}&timezone=auto",
                    result.Latitude, result.Longitude, fahrenheit ? "fahrenheit" : "celsius");
                object root = Json.Parse(web.DownloadString(url));
                var now = Json.Obj(root, "current");
                result.Temperature = Json.Double(now, "temperature_2m", 0);
                result.Code = (int)Json.Long(now, "weather_code", 0);
                result.IsDay = Json.Long(now, "is_day", 1) == 1;

                // Rising, falling or steady over the next three hours.
                object[] hourly = Json.Arr(Json.Obj(root, "hourly"), "temperature_2m");
                double? later = hourly.Length > 3 ? Json.Number(hourly[3]) : null;
                double threshold = fahrenheit ? 2.0 : 1.0;
                if (later.HasValue)
                    result.Trend = later.Value - result.Temperature >= threshold ? 1 : result.Temperature - later.Value >= threshold ? -1 : 0;
            }
            return result;
        }

        /// "Lisbon" or "Springfield, Illinois": the text after the comma picks between same-named places.
        static bool Geocode(WebClient web, string city, Weather result)
        {
            string[] parts = city.Split(new[] { ',' }, 2);
            string name = parts[0].Trim();
            string hint = parts.Length > 1 ? parts[1].Trim() : "";
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + Uri.EscapeDataString(name);
            object[] results = Json.Arr(Json.Parse(web.DownloadString(url)), "results");
            if (results.Length == 0) return false;

            object match = results.FirstOrDefault(r => hint.Length > 0 && new[] { "admin1", "country", "country_code" }
                .Any(k => Json.Str(r, k, "").IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)) ?? results[0];
            result.Latitude = Json.Double(match, "latitude", 0);
            result.Longitude = Json.Double(match, "longitude", 0);
            result.Place = Json.Str(match, "name", name);
            return true;
        }

        void Write()
        {
            string label, tip;
            string network = NetworkText();
            string networkTip = "Network  ↓ " + Rate(receiveRate) + "/s   ↑ " + Rate(sendRate) + "/s"
                + "\nArrows: green when the rate went up since the last update, red when it went down (every 5 s)";
            if (weather != null && !string.IsNullOrEmpty(settings().WeatherCity))
            {
                string glyph = Colour(WeatherGlyph(weather.Code, weather.IsDay), WeatherColour(weather.Code, weather.IsDay));
                label = glyph + "  " + (weatherPhase ? TemperatureText() : network);
                tip = string.Format("{0}  {1:0}°{2}, {3}\nUpdated {4:t}, click to refresh\n{5}",
                    weather.Place, weather.Temperature, weather.Fahrenheit ? "F" : "C",
                    weather.Trend > 0 ? "rising" : weather.Trend < 0 ? "falling" : "steady",
                    weather.FetchedUtc.ToLocalTime(), networkTip);
            }
            else
            {
                string city = settings().WeatherCity;
                label = Colour("\U000F04E1", colors.InkSoft) + "  " + network;
                tip = networkTip + (string.IsNullOrEmpty(city)
                    ? "\nSet weather.city in ~/.config/deskhud/settings.json for weather"
                    : "\nNo weather for \"" + city + "\" yet, see deskhud.log");
            }

            string json = Json.Stringify(new Dictionary<string, object> { { "label", label }, { "tip", tip } });
            if (json == lastWritten) return;
            try
            {
                Paths.WriteAtomic(Paths.TickerFile, json);
                lastWritten = json;
            }
            catch (IOException ex) { Log.Error("ticker", ex); }
        }

        string TemperatureText()
        {
            string arrow = weather.Trend > 0 ? "↗" : weather.Trend < 0 ? "↘" : "→";
            return string.Format(CultureInfo.InvariantCulture, "{0:0}° ", weather.Temperature) + Colour(arrow, colors.InkSoft);
        }

        string NetworkText()
        {
            return Colour("↓", TrendColour(receiveRising)) + Rate(receiveRate) + " " + Colour("↑", TrendColour(sendRising)) + Rate(sendRate);
        }

        static System.Drawing.Color TrendColour(bool rising)
        {
            return rising ? PaletteBuilder.Hsl(140, 0.6, 0.62) : PaletteBuilder.Hsl(2, 0.8, 0.66);
        }

        static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024) return "0K";
            if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024).ToString("0", CultureInfo.InvariantCulture) + "K";
            double mb = bytesPerSecond / (1024 * 1024);
            return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "M";
        }

        static string Colour(string text, System.Drawing.Color colour)
        {
            return "<span style=\"color:" + PaletteStore.Hex(colour) + "\">" + text + "</span>";
        }

        /// WMO weather codes to Nerd Font weather glyphs.
        static string WeatherGlyph(int code, bool day)
        {
            if (code == 0) return day ? "" : "";
            if (code <= 2) return day ? "" : "";
            if (code == 3) return "";
            if (code == 45 || code == 48) return day ? "" : "";
            if (code >= 51 && code <= 57) return "";
            if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return day ? "" : "";
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return day ? "" : "";
            if (code >= 95) return day ? "" : "";
            return "";
        }

        System.Drawing.Color WeatherColour(int code, bool day)
        {
            if (code == 0) return day ? System.Drawing.Color.FromArgb(0xf6, 0xc1, 0x77) : System.Drawing.Color.FromArgb(0xc4, 0xb5, 0xfd);
            if (code <= 2) return day ? System.Drawing.Color.FromArgb(0xf0, 0xd0, 0x9a) : System.Drawing.Color.FromArgb(0xb8, 0xc0, 0xe0);
            if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) return System.Drawing.Color.FromArgb(0x7d, 0xb4, 0xf5);
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return System.Drawing.Color.FromArgb(0xdd, 0xec, 0xff);
            if (code >= 95) return System.Drawing.Color.FromArgb(0xff, 0xd8, 0x6b);
            return colors.InkSoft;
        }

        static Weather LoadCache()
        {
            try
            {
                if (!File.Exists(Paths.WeatherCache)) return null;
                object root = Json.Parse(File.ReadAllText(Paths.WeatherCache));
                return new Weather
                {
                    City = Json.Str(root, "city", ""),
                    Place = Json.Str(root, "place", ""),
                    Latitude = Json.Double(root, "latitude", 0),
                    Longitude = Json.Double(root, "longitude", 0),
                    Temperature = Json.Double(root, "temperature", 0),
                    Code = (int)Json.Long(root, "code", 0),
                    Trend = (int)Json.Long(root, "trend", 0),
                    IsDay = Json.Bool(root, "is_day", true),
                    Fahrenheit = Json.Bool(root, "fahrenheit", true),
                    FetchedUtc = new DateTime(long.Parse(Json.Str(root, "fetched", "0"), CultureInfo.InvariantCulture), DateTimeKind.Utc),
                };
            }
            catch (Exception ex)
            {
                Log.Error("weather cache", ex);
                return null;
            }
        }

        static void SaveCache(Weather w)
        {
            var map = new Dictionary<string, object>
            {
                { "city", w.City }, { "place", w.Place }, { "latitude", w.Latitude }, { "longitude", w.Longitude },
                { "temperature", w.Temperature }, { "code", w.Code }, { "trend", w.Trend }, { "is_day", w.IsDay },
                { "fahrenheit", w.Fahrenheit }, { "fetched", w.FetchedUtc.Ticks.ToString(CultureInfo.InvariantCulture) },
            };
            try { Paths.WriteAtomic(Paths.WeatherCache, Json.Stringify(map)); }
            catch (IOException ex) { Log.Error("weather cache", ex); }
        }

        public void Dispose()
        {
            timer.Dispose();
        }
    }
}
