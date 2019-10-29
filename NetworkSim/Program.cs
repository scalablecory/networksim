using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkSim
{
    class Program
    {
        const int PingInMilliseconds = 200;
        const int ClientBandwidthInBytes = 200;
        const int ServerBandwidthInBytes = 200;

        static async Task Main(string[] args)
        {
            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Cancellation requested. CTRL+C again to force close.");
                    cts.Cancel();
                    e.Cancel = true;
                }
            };

            var pipeOptions = new PipeOptions(pauseWriterThreshold: 256, resumeWriterThreshold: 64, useSynchronizationContext: false);
            var clientBuffer = new Pipe(pipeOptions);
            var serverBuffer = new Pipe(pipeOptions);

            PipeReader clientReader = clientBuffer.Reader;
            PipeWriter clientWriter = new ThrottledPipeWriter(serverBuffer.Writer, ClientBandwidthInBytes, PingInMilliseconds);

            PipeReader serverReader = serverBuffer.Reader;
            PipeWriter serverWriter = new ThrottledPipeWriter(clientBuffer.Writer, ServerBandwidthInBytes, PingInMilliseconds);

            await Task.WhenAll(RunClientSenderAsync(), RunClientReceiverAsync(), RunServerAsync()).ConfigureAwait(false);

            async Task RunClientSenderAsync()
            {
                long request = 0;

                try
                {
                    await using var sw = new StreamWriter(clientWriter.AsStream());

                    while (!cts.IsCancellationRequested)
                    {
                        await sw.WriteLineAsync($"[{++request}] sent {Environment.TickCount64}").ConfigureAwait(false);
                        await sw.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"client sender error: {ex}").ConfigureAwait(false);
                }
                finally
                {
                    await Console.Error.WriteLineAsync($"client sender shutdown. last request: {request}").ConfigureAwait(false);
                }
            }

            async Task RunClientReceiverAsync()
            {
                try
                {
                    using var sr = new StreamReader(clientReader.AsStream());

                    while (await sr.ReadLineAsync().ConfigureAwait(false) is string line)
                    {
                        long ticks = Environment.TickCount64;
                        Match m = Regex.Match(line, @"\[\d+\]\s+sent\s+(\d+),\s+reply\s+sent\s+\d+");
                        long firstTicks = long.Parse(m.Groups[1].Value);

                        await Console.Out.WriteLineAsync($"{line}, reply received {ticks} (total latency {ticks - firstTicks:N0})").ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"client receiver error: {ex}").ConfigureAwait(false);
                }
                finally
                {
                    await Console.Error.WriteLineAsync("client receiver shutdown").ConfigureAwait(false);
                }
            }

            async Task RunServerAsync()
            {
                try
                {
                    using var sr = new StreamReader(serverReader.AsStream());
                    await using var sw = new StreamWriter(serverWriter.AsStream());

                    while (await sr.ReadLineAsync().ConfigureAwait(false) is string line)
                    {
                        await sw.WriteLineAsync($"{line}, reply sent {Environment.TickCount64}").ConfigureAwait(false);
                        await sw.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"server error: {ex}").ConfigureAwait(false);
                }
                finally
                {
                    await Console.Error.WriteLineAsync("server shutdown").ConfigureAwait(false);
                }
            }
        }
    }
}
