using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkSim
{
    public sealed class InMemoryHttpServer
    {
        private IWebHost _host;
        private InMemoryListenerFactory _listenerFactory;

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
            var listenerFactory = new InMemoryListenerFactory();

            _listenerFactory = listenerFactory;
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
                    //services.AddSingleton<IConnectionListenerFactory>(listenerFactory);
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddFilter("Microsoft.AspNetCore", LogLevel.Information);
                })
                .Configure(builder =>
                {
                    builder.UseRouting().UseEndpoints(action);
                })
                .Build();
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return _host.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return _host.StopAsync(cancellationToken);
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
    }
}
