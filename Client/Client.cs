using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;

class ClientApp
{
    private string masterHost;
    private int masterPort;
    public ClientApp(string host = "127.0.0.1", int port = 5000) { masterHost = host; masterPort = port; }

    public async Task RunAsync()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(masterHost, masterPort);
        var stream = client.GetStream();

        var texts = new[]
        {
            new TextItem{ id = "t1", text = "Кот ловит мышь в тёмной комнате" },
            new TextItem{ id = "t2", text = "Кошка охотится на мышь" },
            new TextItem{ id = "t3", text = "Автомобиль едет по дороге" }
        };
        var submit = new SubmitMessage { clientId = "client1", texts = texts };
        await TcpHelpers.SendJsonAsync(stream, submit);

        var respJson = await TcpHelpers.ReadJsonStringAsync(stream);
        var resp = JsonConvert.DeserializeObject<SimilarityResponse>(respJson);
        Console.WriteLine("Similarity matrix:");
        foreach (var a in resp.matrix.Keys)
        {
            foreach (var b in resp.matrix[a].Keys)
            {
                Console.WriteLine($"{a} - {b} : {resp.matrix[a][b]:0.000}");
            }
        }
    }

    static async Task Main(string[] args)
    {
        var c = new ClientApp();
        await c.RunAsync();
    }
}
