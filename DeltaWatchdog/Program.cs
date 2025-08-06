using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WatchdogApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var config = Config.Load("watchdog.cfg");
            var okxClient = new OkxRestRequest(
                config.ApiKey, config.SecretKey, config.Passphrase);
            var notifier = new TelegramNotifier(config.TeleTok, config.TeleChat);
            var monitor = new DeltaMonitor(
                okxClient,
                config.Currency,
                config.RequestInterval,
                config.MaxDelta,
                TimeSpan.FromSeconds(config.DeviationTime),
                config.MainProcess,
                notifier);

            Console.WriteLine("Watchdog started...");
            await monitor.StartAsync();

            // Prevent application exit
            Console.WriteLine("Press Ctrl+C to exit.");
            await Task.Delay(Timeout.Infinite);
        }
    }

    class Config
    {
        public string Currency { get; private set; }
        public int RequestInterval { get; private set; }
        public double MaxDelta { get; private set; }
        public int DeviationTime { get; private set; }
        public string MainProcess { get; private set; }
        public string TeleTok { get; private set; }
        public string TeleChat { get; private set; }
        public string SecretKey { get; private set; }
        public string ApiKey { get; private set; }
        public string Passphrase { get; private set; }

        public static Config Load(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                var parts = trimmed.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim();
                var val = parts[1].Trim().Trim('"');
                dict[key] = val;
            }
            return new Config
            {
                Currency = dict["currency"],
                RequestInterval = int.Parse(dict["request_interval"]),
                MaxDelta = double.Parse(dict["max_delta"]),
                DeviationTime = int.Parse(dict["deviation_time"]),
                MainProcess = dict["main_process"],
                TeleTok = dict["tele_tok"],
                TeleChat = dict["tele_chat"],
                SecretKey = dict["secret_key"],
                ApiKey = dict["api_key"],
                Passphrase = dict["passphrase"]
            };
        }
    }

    class OkxRestRequest
    {
        private readonly string _apiKey;
        private readonly string _secretKey;
        private readonly string _passphrase;
        private readonly HttpClient _http;

        public OkxRestRequest(string apiKey, string secretKey, string passphrase)
        {
            _apiKey = apiKey;
            _secretKey = secretKey;
            _passphrase = passphrase;
            _http = new HttpClient { BaseAddress = new Uri("https://www.okx.com") };
            _http.DefaultRequestHeaders.Add("x-simulated-trading", "1");
        }

        public async Task<JsonDocument> GetRequest(string requestPath)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var preHash = timestamp + "GET" + requestPath;
            var sign = Sign(preHash, _secretKey);

            var req = new HttpRequestMessage(HttpMethod.Get, requestPath);
            req.Headers.Add("OK-ACCESS-KEY", _apiKey);
            req.Headers.Add("OK-ACCESS-SIGN", sign);
            req.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
            req.Headers.Add("OK-ACCESS-PASSPHRASE", _passphrase);

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc;
        }

        private static string Sign(string message, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return Convert.ToBase64String(sig);
        }
    }

    class DeltaMonitor
    {
        private readonly OkxRestRequest _client;
        private readonly string _currency;
        private readonly int _interval;
        private readonly double _maxDelta;
        private readonly TimeSpan _deviationDuration;
        private readonly string _processName;
        private readonly TelegramNotifier _notifier;
        private Timer _timer;
        private DateTime? _firstExceeded;

        public DeltaMonitor(
            OkxRestRequest client,
            string currency,
            int intervalSeconds,
            double maxDelta,
            TimeSpan deviationDuration,
            string processName,
            TelegramNotifier notifier)
        {
            _client = client;
            _currency = currency;
            _interval = intervalSeconds * 1000;
            _maxDelta = maxDelta;
            _deviationDuration = deviationDuration;
            _processName = processName;
            _notifier = notifier;
        }

        public Task StartAsync()
        {
            _timer = new Timer(async _ => await CheckAsync(), null, 0, _interval);
            return Task.CompletedTask;
        }

        private async Task CheckAsync()
        {
            try
            {
                var responce = await _client.GetRequest($"/api/v5/account/greeks?ccy={_currency}");
                var data = responce.RootElement.GetProperty("data")[0];
                var deltaStr = data.GetProperty("deltaPA").GetString();
                double delta = double.Parse(deltaStr);
                Console.WriteLine($"[{DateTime.Now}] DeltaPA={delta}");
                if (Math.Abs(delta) > _maxDelta)
                {
                    if (_firstExceeded == null)
                        _firstExceeded = DateTime.Now;

                    var elapsed = DateTime.Now - _firstExceeded.Value;
                    if (elapsed >= _deviationDuration)
                    {
                        Console.WriteLine("Threshold exceeded too long. Killing process...");
                        KillProcess();
                        await _notifier.SendAsync($"Watchdog: Killing process {_processName} due to deltaPA={delta}");
                        _firstExceeded = null;
                    }
                }
                else
                {
                    _firstExceeded = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during check: {ex.Message}");
            }
        }

        private void KillProcess()
        {
            var procs = Process.GetProcessesByName(_processName);
            foreach (var p in procs)
            {
                try { p.Kill(); Console.WriteLine($"Process {p.Id} killed."); }
                catch (Exception ex) { Console.WriteLine($"Failed to kill process {p.Id}: {ex.Message}"); }
            }
        }
    }

    class TelegramNotifier
    {
        private readonly string _token;
        private readonly string _chatId;
        private readonly HttpClient _http;

        public TelegramNotifier(string token, string chatId)
        {
            _token = token;
            _chatId = chatId;
            _http = new HttpClient();
        }

        public async Task SendAsync(string message)
        {
            var url = $"https://api.telegram.org/bot{_token}/sendMessage";
            var payload = new { chat_id = _chatId, text = message };
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8, "application/json");
            await _http.PostAsync(url, content);
        }
    }
}
