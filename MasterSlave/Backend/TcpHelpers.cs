using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MasterSlave.Backend
{
    public static class TcpHelpers
    {
        public static async Task SendJsonAsync(NetworkStream stream, object obj)
        {
            var json = JsonConvert.SerializeObject(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            var len = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(len, 0, len.Length);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

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
    }

}
