using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace NetworkSim.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        static readonly DependencyPropertyKey ModelKey = DependencyProperty.RegisterReadOnly(nameof(Model), typeof(MainWindowModel), typeof(MainWindow), new PropertyMetadata());
        public static readonly DependencyProperty ModelProperty = ModelKey.DependencyProperty;
        public MainWindowModel Model
        {
            get => (MainWindowModel)GetValue(ModelProperty);
            private set => SetValue(ModelKey, value);
        }

        public MainWindow()
        {
            InitializeComponent();
            Model = new MainWindowModel();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Model.Start();
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            Model.Stop();
        }
    }

    public sealed class MainWindowModel : INotifyPropertyChanging, INotifyPropertyChanged
    {
        private CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly InMemoryHttpServer _server = new InMemoryHttpServer();

        public event PropertyChangingEventHandler PropertyChanging;
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isStopped = false;
        public bool IsStopped
        {
            get => _isStopped;
            private set
            {
                if (value != _isStopped)
                {
                    OnPropertyChanging();
                    _isStopped = value;
                    OnPropertyChanged();
                }
            }
        }

        public int ClientSendBandwidth
        {
            get => _server.ClientSendBytesPerSecond;
            set
            {
                if (value != _server.ClientSendBytesPerSecond)
                {
                    OnPropertyChanging();
                    _server.ClientSendBytesPerSecond = value;
                    OnPropertyChanged();
                }
            }
        }

        public int ServerSendBandwidth
        {
            get => _server.ServerSendBytesPerSecond;
            set
            {
                if (value != _server.ServerSendBytesPerSecond)
                {
                    OnPropertyChanging();
                    _server.ServerSendBytesPerSecond = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Latency
        {
            get => _server.LatencyMilliseconds;
            set
            {
                if (value != _server.LatencyMilliseconds)
                {
                    OnPropertyChanging();
                    _server.LatencyMilliseconds = value;
                    OnPropertyChanged();
                }
            }
        }

        private void OnPropertyChanging([CallerMemberName] string propertyName = null)
        {
            PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(propertyName));
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public MainWindowModel()
        {
            _server.Configure(54321, 43210, builder =>
            {
                builder.MapGet("/", async ctx =>
                {
                    ctx.Response.Headers.Add("content-type", "application/octet-stream");
                    await ctx.Response.StartAsync().ConfigureAwait(false);

                    PipeWriter writer = ctx.Response.BodyWriter;
                    while (!_cancellation.IsCancellationRequested)
                    {
                        Memory<byte> memory = writer.GetMemory();
                        writer.Advance(Math.Min(memory.Length, 4096));
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                });
            });
        }

        public async void Start()
        {
            if (!IsStopped)
            {
                return;
            }

            try
            {
                IsStopped = true;

                await _server.StartAsync(_cancellation.Token);
                await RunClientAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Stop();
                await _server.StopAsync();

                _cancellation.Dispose();
                _cancellation = new CancellationTokenSource();

                IsStopped = true;
            }
        }

        private async Task RunClientAsync()
        {
            TaskScheduler scheduler = TaskScheduler.FromCurrentSynchronizationContext();

            using var client = new HttpClient();
            using var req = new HttpRequestMessage
            {
                Method = System.Net.Http.HttpMethod.Post,
                RequestUri = new Uri($"https://127.0.0.1:43210/"),
                Version = System.Net.HttpVersion.Version20
            };

            using HttpResponseMessage res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            using Stream baseStream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using InstrumentedStream stream = new InstrumentedStream(baseStream);

            StreamMeasurementEvent onMeasurement = (bytesReadPerSecond, bytesWrittenPerSecond) =>
            {
                _ = Task.Factory.StartNew(o =>
                {

                }, this, _cancellation.Token, TaskCreationOptions.None, scheduler);
            };

            byte[] buffer = new byte[4096];
            int len;

            while ((len = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
            }
        }

        sealed class DuplexContent : HttpContent
        {
            private TaskCompletionSource<Stream> _waitForStream;
            private TaskCompletionSource<bool> _waitForCompletion;

            public DuplexContent()
            {
                _waitForStream = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                _waitForCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitForStream.SetResult(stream);
                await _waitForCompletion.Task;
            }

            public Task<Stream> WaitForStreamAsync()
            {
                return _waitForStream.Task;
            }

            public void Complete()
            {
                _waitForCompletion.SetResult(true);
                _waitForCompletion = null;
            }

            public void Fail(Exception e)
            {
                _waitForCompletion.SetException(e);
                _waitForCompletion = null;
            }
        }

        public void Stop()
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
            }
        }
    }
}
