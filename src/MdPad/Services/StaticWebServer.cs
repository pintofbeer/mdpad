using System.Net;
using System.Net.Sockets;

namespace MdPad.Services;

public sealed class StaticWebServer : IDisposable
{
    private readonly string _root;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _serverTask;

    public StaticWebServer(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Port = GetFreePort();
        BaseUri = new Uri($"http://127.0.0.1:{Port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
    }

    public int Port { get; }

    public Uri BaseUri { get; }

    public void Start()
    {
        _listener.Start();
        _serverTask = Task.Run(ServerLoopAsync);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        _stopping.Dispose();
    }

    private async Task ServerLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context));
            }
            catch when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var relativePath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath.TrimStart('/') ?? "");
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = "index.html";
            }

            var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
            if (!fullPath.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            context.Response.ContentType = GetContentType(fullPath);
            context.Response.StatusCode = 200;
            await using var file = File.OpenRead(fullPath);
            context.Response.ContentLength64 = file.Length;
            await file.CopyToAsync(context.Response.OutputStream);
        }
        catch
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = 500;
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }
}
