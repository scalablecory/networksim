using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NetworkSim
{
    /// <summary>
    /// An listener for Kestrel that operates completely in memory.
    /// </summary>
    public sealed class InMemoryListenerFactory : IConnectionListenerFactory
    {
        readonly ConcurrentDictionary<int, InMemoryListener> _listeners = new ConcurrentDictionary<int, InMemoryListener>();
        readonly State _state = new State();

        public int ServerSendBytesPerSecond
        {
            get => _state._serverSendBytesPerSecond;
            set => _state._serverSendBytesPerSecond = value;
        }

        public int ClientSendBytesPerSecond
        {
            get => _state._clientSendBytesPerSecond;
            set => _state._clientSendBytesPerSecond = value;
        }

        public int ServerReceiveBufferBytes
        {
            get => _state._serverReceiveBufferSize;
            set => _state._serverReceiveBufferSize = value;
        }

        public int ClientReceiveBufferBytes
        {
            get => _state._clientReceiveBufferSize;
            set => _state._clientReceiveBufferSize = value;
        }

        public int LatencyMilliseconds
        {
            get => _state._latency;
            set => _state._latency = value;
        }

        public async ValueTask<DuplexPipeStream> ConnectAsync(int port, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            InMemoryListener listener = await GetListenerAsync(port).ConfigureAwait(false);
            return await listener.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            if (!(endpoint is IPEndPoint ipEndPoint)) throw new ArgumentException($"{nameof(endpoint)} must be an {nameof(IPEndPoint)}.");
            if (ipEndPoint.Address != IPAddress.Loopback) throw new ArgumentException($"{nameof(endpoint)} must be {nameof(IPAddress)}.{nameof(IPAddress.Loopback)}.", nameof(endpoint));
            cancellationToken.ThrowIfCancellationRequested();

            InMemoryListener listener = await GetListenerAsync(ipEndPoint.Port).ConfigureAwait(false);
            return listener;
        }

        private async ValueTask<InMemoryListener> GetListenerAsync(int port)
        {
            if (_listeners.TryGetValue(port, out InMemoryListener listener))
            {
                return listener;
            }

            listener = new InMemoryListener(_state, new IPEndPoint(IPAddress.Any, port));
            if (_listeners.TryAdd(port, listener))
            {
                return listener;
            }

            await listener.DisposeAsync().ConfigureAwait(false);
            return _listeners[port];
        }

        private sealed class InMemoryListener : IConnectionListener
        {
            readonly Channel<InMemoryConnectionContext> _accepts = Channel.CreateUnbounded<InMemoryConnectionContext>();
            readonly Channel<InMemoryConnectionContext> _connects = Channel.CreateUnbounded<InMemoryConnectionContext>();
            readonly CancellationTokenSource _cancellationTokenSource;
            readonly State _state;

            public EndPoint EndPoint { get; }

            public InMemoryListener(State state, EndPoint endPoint)
            {
                _state = state;
                _cancellationTokenSource = new CancellationTokenSource();
                EndPoint = endPoint;
            }

            public async ValueTask<DuplexPipeStream> ConnectAsync(CancellationToken cancellationToken = default)
            {
                using CancellationTokenSource opCancellationSource = CreateLinkedSource(cancellationToken);
                CancellationToken opToken = (opCancellationSource ?? _cancellationTokenSource).Token;

                InMemoryConnectionContext ctx = await _accepts.Reader.ReadAsync(opToken);
                await _connects.Writer.WriteAsync(ctx, opToken).ConfigureAwait(false);
                return ctx.ClientTransport;
            }

            public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
            {
                using CancellationTokenSource opCancellationSource = CreateLinkedSource(cancellationToken);
                CancellationToken opToken = (opCancellationSource ?? _cancellationTokenSource).Token;

                var ctx = new InMemoryConnectionContext(_state);
                await _accepts.Writer.WriteAsync(ctx, opToken).ConfigureAwait(false);
                return await _connects.Reader.ReadAsync(opToken).ConfigureAwait(false);
            }

            CancellationTokenSource CreateLinkedSource(CancellationToken cancellationToken)
            {
                return cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken) : null;
            }

            public ValueTask DisposeAsync()
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                return default;
            }

            public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
            {
                _cancellationTokenSource.Cancel();
                return default;
            }
        }

        private sealed class InMemoryConnectionContext
            : ConnectionContext
            , IFeatureCollection
            , IConnectionIdFeature
            , IConnectionTransportFeature
            , IConnectionItemsFeature
            , IMemoryPoolFeature
            , IConnectionLifetimeFeature
        {
            static int s_ids;

            readonly Dictionary<Type, object> _features = new Dictionary<Type, object>();
            int _featuresRevision = 0;

            IDictionary<object, object> _items;
            string _connectionId;

            public override string ConnectionId
            {
                get => _connectionId ??= Interlocked.Increment(ref s_ids).ToString(CultureInfo.InvariantCulture);
                set => _connectionId = value;
            }

            public override IFeatureCollection Features => this;

            public override IDictionary<object, object> Items
            {
                get => _items ??= new Dictionary<object, object>();
                set => _items = value;
            }

            public override IDuplexPipe Transport { get; set; }
            public DuplexPipeStream ClientTransport { get; }

            bool IFeatureCollection.IsReadOnly => false;

            int IFeatureCollection.Revision => _featuresRevision;

            public MemoryPool<byte> MemoryPool => null;

            object IFeatureCollection.this[Type key]
            {
                get
                {
                    if (_features.TryGetValue(key, out object instance))
                    {
                        return instance;
                    }

                    if (key.IsAssignableFrom(GetType()))
                    {
                        return this;
                    }

                    return null;
                }
                set
                {
                    _features[key] = value;
                    ++_featuresRevision;
                }
            }

            public InMemoryConnectionContext(State state)
            {
                var serverOptions = new PipeOptions(pauseWriterThreshold: state._serverReceiveBufferSize);
                var clientOptions = new PipeOptions(pauseWriterThreshold: state._clientReceiveBufferSize);

                var server = new Pipe(serverOptions);
                var client = new Pipe(clientOptions);

                Transport = new DuplexPipeStream(server.Reader, new ThrottledPipeWriter(client.Writer, state._serverSendBytesPerSecond, state._latency));
                ClientTransport = new DuplexPipeStream(client.Reader, new ThrottledPipeWriter(server.Writer, state._clientSendBytesPerSecond, state._latency));
            }

            TFeature IFeatureCollection.Get<TFeature>()
            {
                return (TFeature)((IFeatureCollection)this)[typeof(TFeature)];
            }

            void IFeatureCollection.Set<TFeature>(TFeature instance)
            {
                _features[typeof(TFeature)] = instance;
                ++_featuresRevision;
            }

            IEnumerator<KeyValuePair<Type, object>> IEnumerable<KeyValuePair<Type, object>>.GetEnumerator()
            {
                IFeatureCollection features = this;

                return _features.Keys
                    .Union(new[] { typeof(IConnectionIdFeature), typeof(IConnectionTransportFeature), typeof(IConnectionItemsFeature), typeof(IMemoryPoolFeature), typeof(IConnectionLifetimeFeature) })
                    .Select(type => new KeyValuePair<Type, object>(type, features[type]))
                    .GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return ((IEnumerable<KeyValuePair<Type, object>>)this).GetEnumerator();
            }
        }

        sealed class State
        {
            public int
                _serverSendBytesPerSecond, _clientSendBytesPerSecond,
                _serverReceiveBufferSize, _clientReceiveBufferSize,
                _latency;
        }
    }
}
