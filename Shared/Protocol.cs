using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;

public static class TcpHelpers
{
    // Отправка JSON-объекта с 4-байтной длиной (little-endian)
    public static async Task SendJsonAsync(NetworkStream stream, object obj)
    {
        var json = JsonConvert.SerializeObject(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        var len = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(len, 0, len.Length);
        await stream.WriteAsync(bytes, 0, bytes.Length);
        await stream.FlushAsync();
    }

    // Чтение JSON-объекта (ожидает 4-байтную длину)
    public static async Task<string> ReadJsonStringAsync(NetworkStream stream)
    {
        var lenBuf = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int r = await stream.ReadAsync(lenBuf, read, 4 - read);
            if (r == 0) throw new IOException("Socket closed");
            read += r;
        }
        int len = BitConverter.ToInt32(lenBuf, 0);
        var buf = new byte[len];
        int got = 0;
        while (got < len)
        {
            int r = await stream.ReadAsync(buf, got, len - got);
            if (r == 0) throw new IOException("Socket closed");
            got += r;
        }
        return Encoding.UTF8.GetString(buf);
    }

    public static async Task<T> ReadJsonAsync<T>(NetworkStream stream)
    {
        var s = await ReadJsonStringAsync(stream);
        return JsonConvert.DeserializeObject<T>(s);
    }
}

// Общие DTO
public class TextItem { public string id { get; set; } public string text { get; set; } }
public class SubmitMessage { public string type { get; set; } = "submit"; public string clientId { get; set; } public TextItem[] texts { get; set; } }
public class RegisterMessage { public string type { get; set; } = "register"; public string role { get; set; } public string slaveId { get; set; } }
public class TaskAssign { public string type { get; set; } = "task"; public string taskId { get; set; } public TextItem[] texts { get; set; } }
public class TaskResultItem { public string id { get; set; } public System.Collections.Generic.Dictionary<string, int> counts { get; set; } public long processingMs { get; set; } }
public class TaskResult { public string type { get; set; } = "result"; public string slaveId { get; set; } public string taskId { get; set; } public TaskResultItem[] results { get; set; } }
public class SimilarityResponse { public string type { get; set; } = "similarity"; public string clientId { get; set; } public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, double>> matrix { get; set; } }
