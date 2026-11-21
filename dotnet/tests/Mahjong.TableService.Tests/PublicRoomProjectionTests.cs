using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Mahjong.TableService;
using Xunit;

namespace Mahjong.TableService.Tests
{
    public sealed class PublicRoomProjectionTests
    {
        [Fact]
        public async Task PublicProjectionUsesCachedSnapshotAndHasStrictFieldAllowlistUnderConcurrentReads()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            await using TableTestClient player = await host.ConnectPlayerAsync("projection-room", "projection-account", 0);
            RoomHost room = host.GetRoomHost("projection-room");

            var cachedReads = Enumerable.Range(0, 256)
                .Select(_ => Task.Run(() => room.Actor.GetLatestSnapshotForObservation()))
                .ToArray();
            await Task.WhenAll(cachedReads);
            Assert.All(cachedReads, read => Assert.Equal("projection-room", read.Result.RoomId));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Task<HttpResponseMessage>[] reads = Enumerable.Range(0, 32)
                .Select(_ => http.GetAsync(host.BaseUrl + "/internal/rooms/projection-room/projection"))
                .ToArray();
            HttpResponseMessage[] responses = await Task.WhenAll(reads);
            try
            {
                Assert.All(responses, response => Assert.True(
                    response.StatusCode == HttpStatusCode.OK ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable));
                HttpResponseMessage success = responses.First(response => response.StatusCode == HttpStatusCode.OK);
                using JsonDocument json = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
                string[] fields = json.RootElement.EnumerateObject().Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray();
                Assert.Equal(new[]
                {
                    "lifecycle", "publicVersion", "roomId", "rulesetVersion", "seatsOccupied",
                    "spectatorCount", "updatedAt"
                }.OrderBy(name => name, StringComparer.Ordinal), fields);
                Assert.Equal("projection-room", json.RootElement.GetProperty("roomId").GetString());
                Assert.False(json.RootElement.TryGetProperty("scores", out _));
                Assert.False(json.RootElement.TryGetProperty("hands", out _));
                Assert.False(json.RootElement.TryGetProperty("seed", out _));
                Assert.False(json.RootElement.TryGetProperty("accountId", out _));
            }
            finally
            {
                foreach (HttpResponseMessage response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }
}
