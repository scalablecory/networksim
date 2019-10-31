using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
        private const int HttpPort = 54321;
        private const int HttpsPort = 43210;

        private CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly InMemoryHttpServer _server = new InMemoryHttpServer();

        public event PropertyChangingEventHandler PropertyChanging;
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly float[] _samples = new float[60];
        private int _sampleStart, _sampleCount;

        private PathGeometry _samplesCache;
        public PathGeometry Samples
        {
            get
            {
                if (_samplesCache != null)
                {
                    return _samplesCache;
                }

                double minSample = 0.0;
                double maxSample = 0.0;
                double currentSample = 0.0;
                double currentSpeed = 0.0;

                for (int i = 0; i < _sampleCount; ++i)
                {
                    double sample = _samples[(i + _sampleStart) % _samples.Length];
                    if (sample > maxSample) maxSample = sample;
                }

                PathSegmentCollection segments = new PathSegmentCollection();

                Point startPoint = default;

                for (int i = 0; i < _sampleCount; ++i)
                {
                    double sample = _samples[(i + _sampleStart) % _samples.Length];

                    double y = maxSample - sample;
                    double x = 5.0 * i;

                    var p = new Point(x, y);

                    if (i != 0)
                    {
                        segments.Add(new LineSegment(p, true));
                    }
                    else
                    {
                        startPoint = p;
                    }

                    currentSample = y;
                    currentSpeed = sample;
                    if (y > minSample) minSample = y;
                }

                _legendGeometry = new PathGeometry
                {
                    Figures = new PathFigureCollection
                    {
                        // base line
                        new PathFigure
                        {
                            StartPoint = new Point(0.0, minSample),
                            Segments = new PathSegmentCollection
                            {
                                new LineSegment(new Point(_sampleCount * 5.0, minSample), false)
                            }
                        },
                        
                        // maximum line
                        new PathFigure
                        {
                            StartPoint = new Point(0.0, 0.0),
                            Segments = new PathSegmentCollection
                            {
                                new LineSegment(new Point(_sampleCount * 5.0, 0.0), false)
                            }
                        },
                        
                        // current line
                        new PathFigure
                        {
                            StartPoint = new Point(0.0, currentSample),
                            Segments = new PathSegmentCollection
                            {
                                new LineSegment(new Point(_sampleCount * 5.0, currentSample), true)
                            }
                        }
                    }
                };

                string currentSpeedUnits;

                if (currentSpeed > 1024 * 1024)
                {
                    currentSpeed /= 1024 * 1024;
                    currentSpeedUnits = "GiB/s";
                }
                else if (currentSpeed > 1024)
                {
                    currentSpeed /= 1024;
                    currentSpeedUnits = "MiB/s";
                }
                else
                {
                    currentSpeedUnits = "KiB/s";
                }

                _currentSpeed = $"{currentSpeed:N1} {currentSpeedUnits}";

                _samplesCache = new PathGeometry
                {
                    Figures = new PathFigureCollection
                    {
                        new PathFigure { StartPoint = startPoint, Segments = segments }
                    }
                };

                return _samplesCache;
            }
        }

        private PathGeometry _legendGeometry;
        public PathGeometry LegendGeometry
        {
            get
            {
                _ = Samples;
                return _legendGeometry;
            }
        }

        private string _currentSpeed;
        public string CurrentSpeed
        {
            get
            {
                _ = Samples;
                return _currentSpeed;
            }
        }

        private bool _isStopped = true;
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

        public double ClientUploadBandwidth
        {
            get => _server.ClientSendBytesPerSecond / 1024 / 1024;
            set
            {
                if (value != _server.ClientSendBytesPerSecond)
                {
                    OnPropertyChanging();
                    _server.ClientSendBytesPerSecond = Convert.ToInt32(value * 1024 * 1024);
                    OnPropertyChanged();
                }
            }
        }

        public double ClientDownloadBandwidth
        {
            get => _server.ServerSendBytesPerSecond / 1024 / 1024;
            set
            {
                if (value != _server.ServerSendBytesPerSecond)
                {
                    OnPropertyChanging();
                    _server.ServerSendBytesPerSecond = Convert.ToInt32(value * 1024 * 1024);
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
            _server.Configure(HttpPort, HttpsPort, builder =>
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
                IsStopped = false;

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
            TaskScheduler uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();

            using var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = delegate { return true; }
                }
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var req = new HttpRequestMessage
            {
                Method = System.Net.Http.HttpMethod.Get,
                RequestUri = new Uri($"https://localhost:{HttpsPort}/"),
                Version = System.Net.HttpVersion.Version20,
            };

            using HttpResponseMessage res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            using Stream baseStream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using InstrumentedStream stream = new InstrumentedStream(baseStream);

            Task previousMeasurementUpdate = Task.CompletedTask;

            StreamMeasurementEvent onMeasurement = (bytesReadPerSecond, bytesWrittenPerSecond) =>
            {
                double kbps = bytesReadPerSecond / 1024.0;
                Debug.WriteLine($"NetworkSimUI: measuring {kbps:N1} KiB/sec.");

                previousMeasurementUpdate = previousMeasurementUpdate
                    .ContinueWith((_, o) =>
                    {
                        OnPropertyChanging(nameof(Samples));
                        OnPropertyChanging(nameof(LegendGeometry));
                        OnPropertyChanging(nameof(CurrentSpeed));

                        _samples[(_sampleStart + _sampleCount) % _samples.Length] = (float)kbps;
                        ++(_sampleCount < _samples.Length ? ref _sampleCount : ref _sampleStart);
                        _samplesCache = null;

                        OnPropertyChanged(nameof(Samples));
                        OnPropertyChanged(nameof(LegendGeometry));
                        OnPropertyChanged(nameof(CurrentSpeed));
                    }, this, _cancellation.Token, TaskContinuationOptions.None, uiScheduler);
            };

            stream.Measurement += onMeasurement;
            try
            {
                byte[] buffer = new byte[4096];
                int len;

                while ((len = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                {
                }
            }
            finally
            {
                stream.Measurement -= onMeasurement;
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
