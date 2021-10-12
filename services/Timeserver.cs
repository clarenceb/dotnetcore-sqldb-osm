using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;

namespace DotNetCoreSqlDb.Services
{
    public class Timeserver
    {
        private readonly String timeserverUri;

        public Timeserver ()
        {
            this.timeserverUri = Environment.GetEnvironmentVariable("TIMESERVER_API");
        }

        public async Task<String> GetTimeOfDay()
        {
            string apiResponse = "";
            using (var httpClient = new HttpClient())
            {
                try
                {
                    using (var response = await httpClient.GetAsync(timeserverUri))
                    {
                        apiResponse = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (HttpRequestException ex)
                {
                    apiResponse = "Time unavailable: " + ex.Message;
                }
            }
            return apiResponse;
        }
    }
}
