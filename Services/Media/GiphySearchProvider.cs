using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace PixelOverlay.Services.Media
{
    // GIPHY search metadata only. API responses, media URLs and media files are
    // intentionally not persisted or copied into the local overlay library.
    public sealed class GiphySearchProvider : IMediaSearchProvider
    {
        private const int MaxResponseBytes = 2 * 1024 * 1024;
        private static readonly HttpClient Client = new HttpClient();
        private readonly string _apiKey;

        public string ProviderName => "GIPHY";
        public string Attribution => "Powered by GIPHY";

        public GiphySearchProvider(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("A GIPHY API key is required.", nameof(apiKey));
            _apiKey = apiKey.Trim();
        }

        public Task<IList<MediaSearchResult>> SearchAsync(string query, int limit = 25)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Enter a search phrase.", nameof(query));
            return RequestAsync("search", query.Trim(), limit);
        }

        public Task<IList<MediaSearchResult>> GetTrendingAsync(int limit = 25)
        {
            return RequestAsync("trending", null, limit);
        }

        private async Task<IList<MediaSearchResult>> RequestAsync(string endpoint, string query, int limit)
        {
            limit = Math.Max(1, Math.Min(25, limit));
            var uri = "https://api.giphy.com/v1/gifs/" + endpoint +
                "?api_key=" + Uri.EscapeDataString(_apiKey) +
                "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "&rating=g";
            if (!string.IsNullOrWhiteSpace(query))
                uri += "&q=" + Uri.EscapeDataString(query);

            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri)))
            using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value > MaxResponseBytes)
                    throw new InvalidDataException("The GIPHY response exceeded the 2 MiB metadata limit.");

                byte[] payload;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[16384];
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaxResponseBytes)
                            throw new InvalidDataException("The GIPHY response exceeded the 2 MiB metadata limit.");
                        output.Write(buffer, 0, read);
                    }
                    payload = output.ToArray();
                }

                GiphyResponse parsed;
                var serializer = new DataContractJsonSerializer(typeof(GiphyResponse));
                using (var stream = new MemoryStream(payload))
                    parsed = (GiphyResponse)serializer.ReadObject(stream);

                var results = new List<MediaSearchResult>();
                if (parsed?.Data == null) return results;

                foreach (var gif in parsed.Data)
                {
                    if (gif == null || string.IsNullOrWhiteSpace(gif.Url)) continue;
                    results.Add(new MediaSearchResult
                    {
                        Id = gif.Id,
                        Provider = ProviderName,
                        Title = string.IsNullOrWhiteSpace(gif.Title) ? "Untitled GIF" : gif.Title,
                        PageUrl = gif.Url,
                        Rating = gif.Rating
                    });
                }
                return results;
            }
        }

        [DataContract]
        private sealed class GiphyResponse
        {
            [DataMember(Name = "data")] public List<GiphyGif> Data { get; set; }
        }

        [DataContract]
        private sealed class GiphyGif
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "title")] public string Title { get; set; }
            [DataMember(Name = "url")] public string Url { get; set; }
            [DataMember(Name = "rating")] public string Rating { get; set; }
        }
    }
}