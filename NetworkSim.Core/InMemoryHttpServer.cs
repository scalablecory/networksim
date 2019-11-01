using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NetworkSim
{
    public sealed class InMemoryHttpServer
    {
        private readonly PropertyInfo _connectCallbackProperty = typeof(SocketsHttpHandler).GetProperty("ConnectCallback");
        private readonly InMemoryListenerFactory _listenerFactory = new InMemoryListenerFactory();
        private readonly Channel<string> _log = Channel.CreateUnbounded<string>();

        private IWebHost _host;

        public int ServerSendBytesPerSecond
        {
            get => _listenerFactory.ServerSendBytesPerSecond;
            set => _listenerFactory.ServerSendBytesPerSecond = value;
        }

        public int ClientSendBytesPerSecond
        {
            get => _listenerFactory.ClientSendBytesPerSecond;
            set => _listenerFactory.ClientSendBytesPerSecond = value;
        }

        public int ServerReceiveBufferBytes
        {
            get => _listenerFactory.ServerReceiveBufferBytes;
            set => _listenerFactory.ServerReceiveBufferBytes = value;
        }

        public int ClientReceiveBufferBytes
        {
            get => _listenerFactory.ClientReceiveBufferBytes;
            set => _listenerFactory.ClientReceiveBufferBytes = value;
        }

        public int LatencyMilliseconds
        {
            get => _listenerFactory.LatencyMilliseconds;
            set => _listenerFactory.LatencyMilliseconds = value;
        }

        public void Configure(int httpPort, int httpsPort, Action<IEndpointRouteBuilder> action)
        {
            var listenerFactory = _listenerFactory;

            _host =
                WebHost.CreateDefaultBuilder()
                .UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")
                .UseKestrel(ko =>
                {
                    ko.Listen(IPAddress.Loopback, httpPort);
                    ko.Listen(IPAddress.Loopback, httpsPort, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                        listenOptions.UseHttps(CreateSelfSignedCert());
                    });
                })
                .ConfigureServices(services =>
                {
                    if (_connectCallbackProperty != null)
                    {
                        services.AddSingleton<IConnectionListenerFactory>(listenerFactory);
                    }
                })
                .ConfigureLogging(logging =>
                {
                    logging
                        .ClearProviders()
                        .AddProvider(new InMemoryLoggingProvider(_log.Writer))
                        .SetMinimumLevel(LogLevel.Information);
                })
                .Configure(builder =>
                {
                    builder.UseRouting().UseEndpoints(action);
                })
                .Build();
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_connectCallbackProperty == null)
            {
                _log.Writer.TryWrite("Warning: wrong HttpClient version, unable to use in-memory connection. Bandwidth/Latency settings will not function.");
                _log.Writer.TryWrite($"HttpClient DLL: {typeof(SocketsHttpHandler).Assembly.Location}");
            }
            else
            {
                _log.Writer.TryWrite("Using in-memory connection provider.");
            }

            return _host.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return _host.StopAsync(cancellationToken);
        }

        public SocketsHttpHandler CreateHttpHandler()
        {
            var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = delegate { return true; };

            if (_connectCallbackProperty != null)
            {
                Func<string, int, CancellationToken, ValueTask<Stream>> dialer = async (host, port, cancellationToken) =>
                {
                    if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Only localhost is supported.");
                    }

                    return await _listenerFactory.ConnectAsync(port, cancellationToken).ConfigureAwait(false);
                };
                _connectCallbackProperty.SetValue(handler, dialer);
            }

            return handler;
        }

        public ValueTask<string> GetNextLogAsync(CancellationToken cancellationToken = default)
        {
            return _log.Reader.ReadAsync(cancellationToken);
        }

        private static X509Certificate2 CreateSelfSignedCert()
        {
            // Create self-signed cert for server.
            using RSA rsa = RSA.Create();
            var certReq = new CertificateRequest($"CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            certReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            X509Certificate2 cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
            }
            return cert;
        }

        sealed class InMemoryLoggingProvider : ILoggerProvider, ILogger
        {
            readonly ChannelWriter<string> _log;

            public InMemoryLoggingProvider(ChannelWriter<string> log)
            {
                _log = log;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return null;
            }

            public ILogger CreateLogger(string categoryName)
            {
                return this;
            }

            public void Dispose()
            {
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                _log.TryWrite(formatter(state, exception));
            }
        }
    }
}
