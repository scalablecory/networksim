using System;
using System.Net.Http;

namespace NetworkSim
{
    class Program
    {
        static void Main(string[] args)
        {
            HttpClient client = new HttpClient();
            client.GetAsync("http://www.microsoft.com").GetAwaiter().GetResult();
        }
    }
}
