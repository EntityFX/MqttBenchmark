using System.Net.Http.Json;
using System.Net;
using Microsoft.Extensions.Logging;

public class MqttCounterClient
{
    private readonly ILogger logger;
    private readonly HttpClient httpClient;

    private TimeSpan retryPeriod = TimeSpan.FromSeconds(1);

    public MqttCounterClient(ILogger<MqttCounterClient> logger, HttpClient httpClientFactory)
    {
        this.logger = logger;
        this.httpClient = httpClientFactory;
    }

    public async Task<int> GetCounterAndValidate(string broker, string topic, int attempts = 25, TimeSpan? retryPeriod = null)
    {
        int counterValue = 0;

        TimeSpan actualPeriod = retryPeriod ?? this.retryPeriod;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var currentCounter = await GetCounter(broker, topic);
            logger.LogInformation($"Got counter={currentCounter} [Prev counter={counterValue}, Attempt={attempt}, Broker={broker}, Topic={topic}]");
            Console.WriteLine($"Got counter={currentCounter} [Prev counter={counterValue}, Attempt={attempt}, Broker={broker}, Topic={topic}]");
            if (attempt > 1 && currentCounter == counterValue) {
                return currentCounter;
            }

            if (currentCounter >= counterValue) {
                counterValue = currentCounter;
            }

            await Task.Delay(actualPeriod);
            
        }

        return counterValue;
    }

    public async Task<int> GetCounter(string broker, string topic)
    {
        try
        {
            var counters = await httpClient.GetAsync($"counter/{WebUtility.UrlEncode(broker)}/{WebUtility.UrlEncode(topic)}");
            if (counters?.IsSuccessStatusCode != true)
            {
                return 0;
            }

            return await counters!.Content.ReadFromJsonAsync<int>();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public async Task<bool> ClearCounter(string broker, string topic)
    {
        try
        {
            var counters = await httpClient.DeleteAsync($"counter/{WebUtility.UrlEncode(broker)}/{WebUtility.UrlEncode(topic)}");
            if (counters?.IsSuccessStatusCode != true)
            {
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<Dictionary<string, int>> GetCounters(string broker)
    {
        try
        {
            var counters = await httpClient.GetAsync($"counter/{WebUtility.UrlEncode(broker)}");
            if (counters?.IsSuccessStatusCode != true)
            {
                return new Dictionary<string, int>();
            }

            return await counters!.Content.ReadFromJsonAsync<Dictionary<string, int>>()
                ?? new Dictionary<string, int>();
        }
        catch (Exception)
        {
            return new Dictionary<string, int>();
        }
    }

    public async Task<Dictionary<string, Dictionary<string, int>>> GetCounters()
    {
        try
        {
            var counters = await httpClient.GetAsync("counter");
            if (counters?.IsSuccessStatusCode != true)
            {
                return new Dictionary<string, Dictionary<string, int>>();
            }

            return await counters!.Content.ReadFromJsonAsync<Dictionary<string, Dictionary<string, int>>>()
                ?? new Dictionary<string, Dictionary<string, int>>();
        }
        catch (Exception)
        {
            return new Dictionary<string, Dictionary<string, int>>();
        }
    }


    public async Task<bool> ClearCounters()
    {
        try
        {
            var counters = await httpClient.DeleteAsync("counters");
            if (counters?.IsSuccessStatusCode != true)
            {
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
