﻿using System.Net;
using System.Net.Http.Json;

namespace EntityFX.MqttBenchmark;

class MqttCounterClient
{
    private readonly HttpClient httpClient;

    public MqttCounterClient(HttpClient httpClientFactory)
    {
        this.httpClient = httpClientFactory;
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