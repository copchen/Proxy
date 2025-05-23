using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class SimpleHttpProxy
{
    static async Task Main()
    {
        var listener = new TcpListener(IPAddress.Parse("127.0.0.2"), 8080);
        listener.Start();
        Console.WriteLine("HTTP-прокси запущен на 127.0.0.2:8080");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    static async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            var clientStream = client.GetStream();
            var reader = new StreamReader(clientStream, Encoding.ASCII, false, 8192, true);
            var writer = new StreamWriter(clientStream, Encoding.ASCII) { AutoFlush = true };

            string requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine))
                return;

            var tokens = requestLine.Split(' ');
            if (tokens.Length < 3)
                return;

            string method = tokens[0];
            string fullUrl = tokens[1];
            string version = tokens[2];

            if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http")
                return;

            string host = uri.Host;
            int port = uri.IsDefaultPort ? 80 : uri.Port;
            string pathAndQuery = uri.PathAndQuery;

            var server = new TcpClient();
            await server.ConnectAsync(host, port);
            var serverStream = server.GetStream();
            var serverWriter = new StreamWriter(serverStream, Encoding.ASCII) { AutoFlush = true };
            var serverReader = new StreamReader(serverStream, Encoding.ASCII, false, 8192, true);

            
            await serverWriter.WriteLineAsync($"{method} {pathAndQuery} {version}");

            
            string line;
            int contentLength = 0;
            bool isChunked = false;

            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(15).Trim(), out contentLength);

                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    line.ToLower().Contains("chunked"))
                    isChunked = true;

                await serverWriter.WriteLineAsync(line);
            }

            await serverWriter.WriteLineAsync(); 

            
            if (contentLength > 0)
                await CopyContentAsync(clientStream, serverStream, contentLength);
            else if (isChunked)
                await TransferChunkedAsync(clientStream, serverStream);

           
            string responseLine = await serverReader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(responseLine))
            {
                var statusParts = responseLine.Split(new[] { ' ' }, 3, StringSplitOptions.None);
                if (statusParts.Length >= 2)
                    Console.WriteLine($"{fullUrl} - {statusParts[1]}");

                await writer.WriteLineAsync(responseLine); 
            }

           
            while (!string.IsNullOrEmpty(line = await serverReader.ReadLineAsync()))
            {
                await writer.WriteLineAsync(line);
            }
            await writer.WriteLineAsync();

           
            await serverStream.CopyToAsync(clientStream);
        }
    }

    static async Task CopyContentAsync(Stream input, Stream output, int contentLength)
    {
        byte[] buffer = new byte[8192];
        int remaining = contentLength;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            await output.WriteAsync(buffer, 0, read);
            remaining -= read;
        }
    }

    static async Task TransferChunkedAsync(Stream input, Stream output)
    {
        byte[] buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await output.WriteAsync(buffer, 0, bytesRead);
            if (Encoding.ASCII.GetString(buffer, 0, bytesRead).Contains("0\r\n\r\n"))
                break;
        }
    }
}
