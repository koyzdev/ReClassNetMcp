using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReClassNetMcp.Abstractions;
using ReClassNetMcp.Configuration;
using ReClassNetMcp.Protocol;

namespace ReClassNetMcp.Server
{
    internal sealed class McpHttpServer : IDisposable
    {
        private const string EndpointPath = "/mcp";

        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);

        private readonly IReClassHost host;

        private readonly McpDispatcher dispatcher;

        private readonly ServerSettings settings;

        private HttpListener listener;

        private CancellationTokenSource shutdown;

        private Task loop;

        public McpHttpServer(IReClassHost host, McpDispatcher dispatcher, ServerSettings settings)
        {
            this.host = host;
            this.dispatcher = dispatcher;
            this.settings = settings;
        }

        public int Port { get; private set; }

        public string Url => $"http://127.0.0.1:{Port}{EndpointPath}";

        public bool IsRunning => listener != null && listener.IsListening;

        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            HttpListenerException lastFailure = null;

            for (var offset = 0; offset <= ServerSettings.PortScanRange; ++offset)
            {
                var candidate = settings.PreferredPort + offset;
                if (candidate > ushort.MaxValue)
                {
                    break;
                }

                var attempt = new HttpListener();
                attempt.Prefixes.Add($"http://127.0.0.1:{candidate}/");

                try
                {
                    attempt.Start();
                }
                catch (HttpListenerException ex)
                {
                    lastFailure = ex;
                    attempt.Close();
                    continue;
                }

                listener = attempt;
                Port = candidate;
                break;
            }

            if (listener == null)
            {
                throw new InvalidOperationException(
                    $"No free port in {settings.PreferredPort}..{settings.PreferredPort + ServerSettings.PortScanRange}",
                    lastFailure);
            }

            shutdown = new CancellationTokenSource();
            loop = Task.Run(() => AcceptLoop(shutdown.Token));
        }

        public void Stop()
        {
            shutdown?.Cancel();

            if (listener != null)
            {
                try
                {
                    listener.Stop();
                    listener.Close();
                }
                catch (ObjectDisposedException)
                {
                }

                listener = null;
            }

            try
            {
                loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            loop = null;
            shutdown?.Dispose();
            shutdown = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener != null && listener.IsListening)
            {
                HttpListenerContext context;

                //
                // Stop() races this call, and which exception surfaces depends on whether
                // the listener was stopped, closed or already disposed when the pending
                // accept unwound. All three mean the same thing here: nothing is left to
                // accept, so leave the loop instead of trying again.
                //
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                var captured = context;
                ThreadPool.QueueUserWorkItem(_ => Serve(captured));
            }
        }

        private void Serve(HttpListenerContext context)
        {
            try
            {
                Handle(context);
            }
            catch (Exception ex)
            {
                host.Log(HostLogLevel.Error, $"mcp: request handling failed: {ex}");

                try
                {
                    WriteStatus(context, HttpStatusCode.InternalServerError);
                }
                catch (Exception)
                {
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var request = context.Request;

            //
            // Host and Origin are both checked, and this is DNS-rebinding protection
            // rather than CORS. Origin alone is not enough: a page can point a name it
            // controls at 127.0.0.1 and then reach this listener with an Origin the
            // browser considers its own, so the Host the request arrived with is validated
            // as well.
            //
            // A missing Origin is allowed. A CLI client sends none at all, and a browser
            // never omits it on a cross-origin request, so absent is the safe case.
            //
            if (!IsLoopbackHost(request))
            {
                WriteStatus(context, HttpStatusCode.Forbidden, "Invalid Host header");
                return;
            }

            var origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
            {
                WriteStatus(context, HttpStatusCode.Forbidden, "Invalid Origin header");
                return;
            }

            if (!IsAuthorized(request))
            {
                context.Response.AddHeader("WWW-Authenticate", "Bearer realm=\"reclass.net\"");
                WriteStatus(context, HttpStatusCode.Unauthorized, "Missing or invalid bearer token");
                return;
            }

            var path = request.Url.AbsolutePath.TrimEnd('/');
            if (path.Length == 0)
            {
                path = EndpointPath;
            }

            if (!string.Equals(path, EndpointPath, StringComparison.OrdinalIgnoreCase))
            {
                WriteStatus(context, HttpStatusCode.NotFound, $"The MCP endpoint is {EndpointPath}");
                return;
            }

            //
            // 405 for both, and that is the complete answer rather than a gap. The
            // declared capabilities carry no listChanged, no subscribe and no logging, so
            // there is nothing this server could ever push and no stream worth opening.
            // A client that tries a background GET for SSE after initialize drops it again
            // on 405, which is the outcome we want. DELETE has nothing to act on either,
            // since no Mcp-Session-Id is ever handed out and there is no session to end.
            //
            if (string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                WriteStatus(context, HttpStatusCode.MethodNotAllowed);
                return;
            }

            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteStatus(context, HttpStatusCode.MethodNotAllowed);
                return;
            }

            var protocolVersion = request.Headers["MCP-Protocol-Version"];
            if (!string.IsNullOrEmpty(protocolVersion) && !ProtocolVersions.IsSupported(protocolVersion))
            {
                WriteStatus(context, HttpStatusCode.BadRequest, $"Unsupported MCP-Protocol-Version '{protocolVersion}'");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            //
            // A malformed body is still a completed HTTP exchange: the failure belongs in
            // the JSON-RPC envelope under a null id, not in the status line, so this
            // answers 200 with -32700. The two 202 paths below are the opposite case, a
            // payload that must not be answered at all: a notification, or the client's
            // own response object.
            //
            JToken payload;
            try
            {
                payload = JToken.Parse(body);
            }
            catch (JsonException)
            {
                WriteJson(context, HttpStatusCode.OK, JsonRpc.Error(null, JsonRpcErrorCode.ParseError, "Invalid JSON payload"));
                return;
            }

            if (!JsonRpcRequest.TryParse(payload, out var rpc, out var error))
            {
                if (error == null)
                {
                    WriteStatus(context, HttpStatusCode.Accepted);
                    return;
                }

                WriteJson(context, HttpStatusCode.OK, error);
                return;
            }

            var response = dispatcher.Handle(rpc);

            if (response == null)
            {
                WriteStatus(context, HttpStatusCode.Accepted);
                return;
            }

            WriteJson(context, HttpStatusCode.OK, response);
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var header = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(header))
            {
                return false;
            }

            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TokenComparer.Matches(settings.Token, header.Substring(prefix.Length).Trim());
        }

        private static bool IsLoopbackHost(HttpListenerRequest request)
        {
            var host = request.UserHostName;
            if (string.IsNullOrEmpty(host))
            {
                return true;
            }

            //
            // UserHostName is the Host header verbatim, so it still carries the port, and
            // an IPv6 literal arrives bracketed as [::1]:15850. Split on the last colon
            // only when that colon sits outside the brackets, then drop them.
            //
            var separator = host.LastIndexOf(':');
            if (separator > 0 && host.IndexOf(']') < separator)
            {
                host = host.Substring(0, separator);
            }

            host = host.Trim('[', ']');

            return string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
                string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::1", StringComparison.Ordinal);
        }

        private static bool IsLoopbackOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.IsLoopback;
        }

        private static void WriteStatus(HttpListenerContext context, HttpStatusCode status, string message = null)
        {
            var response = context.Response;
            response.StatusCode = (int)status;

            if (string.IsNullOrEmpty(message))
            {
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            var payload = Utf8WithoutBom.GetBytes(message);
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = payload.Length;
            response.OutputStream.Write(payload, 0, payload.Length);
            response.Close();
        }

        private static void WriteJson(HttpListenerContext context, HttpStatusCode status, JToken document)
        {
            var payload = Utf8WithoutBom.GetBytes(document.ToString(Formatting.None));
            var response = context.Response;

            response.StatusCode = (int)status;
            response.ContentType = "application/json";
            response.ContentLength64 = payload.Length;
            response.OutputStream.Write(payload, 0, payload.Length);
            response.Close();
        }
    }
}
