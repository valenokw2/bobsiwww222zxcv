using System.Collections.Generic;
using System.Threading.Tasks;

namespace PixelOverlay.Services.Media
{
    public interface IMediaSearchProvider
    {
        string ProviderName { get; }
        string Attribution { get; }
        Task<IList<MediaSearchResult>> SearchAsync(string query, int limit = 25);
        Task<IList<MediaSearchResult>> GetTrendingAsync(int limit = 25);
    }

    public sealed class MediaSearchResult
    {
        public string Id { get; set; }
        public string Provider { get; set; }
        public string Title { get; set; }
        public string PageUrl { get; set; }
        public string Rating { get; set; }
    }
}