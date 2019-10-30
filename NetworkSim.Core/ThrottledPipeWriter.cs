using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using System.Diagnostics;

namespace NetworkSim
{
    public sealed class ThrottledPipeWriter : PipeWriter
    {
        private static readonly Exception s_successSentry = new Exception();

        readonly PipeWriter _baseWriter;
        readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create();
        readonly Queue<ReferenceCountedMemory> _flushBuffer = new Queue<ReferenceCountedMemory>();

        ReferenceCountedMemory _currentBuffer;
        readonly double _sendBytesPerMillisecond;
        double _sendBandwidthAvailable;
        long _lastFlushTicks;

        Task _previousSendTask = Task.CompletedTask, _pendingWriteTask = Task.CompletedTask;
        readonly int _sendDelayInMs;
        bool _isCompleted;

        public ThrottledPipeWriter(PipeWriter baseWriter, int bytesPerSecond, int delayInMilliseconds)
        {
            _baseWriter = baseWriter;
            _sendBytesPerMillisecond = bytesPerSecond * 0.001;
            _sendDelayInMs = delayInMilliseconds;
        }

        public override void Advance(int bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            _flushBuffer.Enqueue(_currentBuffer.Slice(0, bytes));
            _currentBuffer.SliceSelf(bytes);
        }

        public override void CancelPendingFlush()
        {
            //TODO.
        }

        public override void Complete(Exception exception = null)
        {
            CompleteAsync(exception).GetAwaiter().GetResult();
        }

        public override async ValueTask CompleteAsync(Exception exception = null)
        {
            if (_isCompleted)
            {
                throw new InvalidOperationException($"{nameof(ThrottledPipeWriter)} is already completed.");
            }

            // ensure everything is in our send buffer, so we can await it in the helper.
            await FlushAsync().ConfigureAwait(false);

            Task prev = _previousSendTask;

            if (prev.IsFaulted == true)
            {
                prev.GetAwaiter().GetResult();
            }

            _isCompleted = true;

            _previousSendTask = CompleteAsyncHelper();
            await _previousSendTask.ConfigureAwait(false);

            async Task CompleteAsyncHelper()
            {
                await prev.ConfigureAwait(false);
                prev = null;

                await _baseWriter.CompleteAsync(exception).ConfigureAwait(false);
            }
        }

        // TODO: cancellation is not supported.
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            while (_flushBuffer.TryDequeue(out ReferenceCountedMemory buffer))
            {
                do
                {
                    // if our writer is giving backpressure, wait for it to allow more data through.
                    await Volatile.Read(ref _pendingWriteTask).ConfigureAwait(false);

                    // wait until we have some amount of bandwidth available.
                    // use a minimum send size to reduce super "small" packets.
                    double desiredMinimumSend = Math.Clamp(_sendBytesPerMillisecond * 25.0, 1.0, buffer.Length);
                    while (_sendBandwidthAvailable < desiredMinimumSend)
                    {
                        long ticks = Environment.TickCount64;
                        long ticksSinceLastFlush = ticks - _lastFlushTicks;

                        if (ticksSinceLastFlush < 50)
                        {
                            await Task.Delay(50 - (int)ticksSinceLastFlush).ConfigureAwait(false);

                            ticks = Environment.TickCount64;
                            ticksSinceLastFlush = ticks - _lastFlushTicks;
                        }

                        _lastFlushTicks = ticks;
                        _sendBandwidthAvailable = Math.Max((int)Math.Min(1000, ticksSinceLastFlush) * _sendBytesPerMillisecond + _sendBandwidthAvailable, _sendBytesPerMillisecond * 1000.0);
                    }

                    int writeSize = (int)Math.Min((long)_sendBandwidthAvailable, buffer.Length);

                    SendWithDelay(buffer.Slice(0, writeSize));

                    buffer.SliceSelf(writeSize);
                    _sendBandwidthAvailable -= writeSize;
                }
                while (buffer.Length != 0);

                buffer.Dispose();
            }

            return new FlushResult(false, _isCompleted);
        }

        // simulates connection latency by waiting a set number of milliseconds before writing.
        private void SendWithDelay(ReferenceCountedMemory buffer)
        {
            long sendTicks = Environment.TickCount64 + _sendDelayInMs;

            Task prev = _previousSendTask;

            if (prev.IsFaulted == true)
            {
                prev.GetAwaiter().GetResult();
            }

            _previousSendTask = SendWithDelayAsync();

            async Task SendWithDelayAsync()
            {
                await prev.ConfigureAwait(false);
                prev = null;

                long ticksLeft = sendTicks - Environment.TickCount64;
                if (ticksLeft > 0)
                {
                    await Task.Delay((int)ticksLeft).ConfigureAwait(false);
                }

                ValueTask<FlushResult> writeValueTask = _baseWriter.WriteAsync(buffer.Memory);
                Task writeTask = writeValueTask.IsCompletedSuccessfully ? Task.CompletedTask : writeValueTask.AsTask();
                Volatile.Write(ref _pendingWriteTask, writeTask);

                await writeTask.ConfigureAwait(false);
                buffer.Dispose();
            }
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_isCompleted)
            {
                throw new InvalidOperationException($"{nameof(ThrottledPipeWriter)} is completed.");
            }

            if (_currentBuffer.Length != 0 && _currentBuffer.Length >= sizeHint)
            {
                return _currentBuffer.Memory;
            }

            // buffer is too small, dispose it and create another one.
            _currentBuffer.Dispose();

            if (sizeHint == 0)
            {
                sizeHint = 4096;
            }

            _currentBuffer = new ReferenceCountedMemory(_pool, sizeHint);
            Debug.Assert(_currentBuffer.Length >= sizeHint);

            return _currentBuffer.Memory;
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            return GetMemory(sizeHint).Span;
        }

        // a sliceable pooled buffer.
        struct ReferenceCountedMemory : IMemoryOwner<byte>
        {
            State _state;
            int _offset, _length;

            public int Offset => _offset;

            public int Length => _length;

            private bool IsDisposed => _state == null;

            public Memory<byte> Memory
            {
                get
                {
                    if (IsDisposed) throw new InvalidOperationException($"{nameof(ReferenceCountedMemory)} is already disposed.");
                    return _state._buffer.AsMemory(_offset, _length);
                }
            }

            public ReferenceCountedMemory(ArrayPool<byte> pool, int sizeHint)
            {
                _state = new State(pool, sizeHint);
                _offset = 0;
                _length = _state._buffer.Length;
            }

            private ReferenceCountedMemory(State state, int offset, int length)
            {
                _state = state;
                _offset = offset;
                _length = length;
            }

            public ReferenceCountedMemory Slice(int offset)
            {
                return Slice(offset, _length - offset);
            }

            public ReferenceCountedMemory Slice(int offset, int length)
            {
                if (IsDisposed) throw new InvalidOperationException($"{nameof(ReferenceCountedMemory)} is already disposed.");
                Interlocked.Increment(ref _state._references);
                return new ReferenceCountedMemory(_state, _offset + offset, length);
            }

            public void SliceSelf(int offset)
            {
                SliceSelf(offset, _length - offset);
            }

            public void SliceSelf(int offset, int length)
            {
                _offset += offset;
                _length = length;
            }

            public ReferenceCountedMemory Move()
            {
                ReferenceCountedMemory ret = this;
                this = default;
                return ret;
            }

            public void Dispose()
            {
                if (IsDisposed)
                {
                    return;
                }

                if (Interlocked.Decrement(ref _state._references) == 0)
                {
                    lock (_state._pool)
                    {
                        _state._pool.Return(_state._buffer);
                    }
                }

                _state = null;
            }

            sealed class State
            {
                public readonly ArrayPool<byte> _pool;
                public readonly byte[] _buffer;
                public int _references;

                public State(ArrayPool<byte> pool, int sizeHint)
                {
                    _pool = pool;
                    _references = 1;

                    lock (pool)
                    {
                        _buffer = pool.Rent(sizeHint);
                    }
                }
            }
        }
    }
}
