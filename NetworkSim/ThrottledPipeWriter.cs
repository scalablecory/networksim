using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NetworkSim
{
    sealed class ThrottledPipeWriter : PipeWriter
    {
        private static readonly Exception s_successSentry = new Exception();

        readonly PipeWriter _baseWriter;
        readonly double _sendBytesPerMillisecond;
        readonly int _delayMs;

        readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create();
        readonly Queue<(byte[] buffer, int length)> _flushBuffer = new Queue<(byte[] buffer, int length)>();

        byte[] _currentBuffer;
        double _sendBandwidthAvailable;
        long _lastFlushTicks;
        Task _previousSendTask;
        bool _isCompleted;

        public ThrottledPipeWriter(PipeWriter baseWriter, int bytesPerSecond, int delayInMilliseconds)
        {
            _baseWriter = baseWriter;
            _sendBytesPerMillisecond = bytesPerSecond * 0.001;
            _delayMs = delayInMilliseconds;
        }

        public override void Advance(int bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            _flushBuffer.Enqueue((_currentBuffer, bytes));
            _currentBuffer = null;
        }

        public override void CancelPendingFlush()
        {
        }

        public override void Complete(Exception exception = null)
        {
            CompleteAsync(exception).GetAwaiter().GetResult();
        }

        public override ValueTask CompleteAsync(Exception exception = null)
        {
            if (_isCompleted)
            {
                throw new InvalidOperationException($"{nameof(ThrottledPipeWriter)} is already completed.");
            }

            _isCompleted = true;

            Task prev = _previousSendTask;
            _previousSendTask = CompleteAsyncHelper();
            return new ValueTask(_previousSendTask);

            async Task CompleteAsyncHelper()
            {
                await prev.ConfigureAwait(false);
                await _baseWriter.CompleteAsync(exception).ConfigureAwait(false);
            }
        }

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            while (_flushBuffer.TryDequeue(out (byte[], int) tuple))
            {
                (byte[] buffer, int bufferLength) = tuple;
                int bufferOffset = 0;

                do
                {
                    long ticks;

                    while (_sendBandwidthAvailable < 1.0)
                    {
                        ticks = Environment.TickCount64;
                        long ticksSinceLastFlush = ticks - _lastFlushTicks;

                        if (ticksSinceLastFlush < 50)
                        {
                            await Task.Delay(100 - (int)ticksSinceLastFlush, cancellationToken).ConfigureAwait(false);

                            ticks = Environment.TickCount64;
                            ticksSinceLastFlush = ticks - _lastFlushTicks;
                        }

                        _lastFlushTicks = ticks;
                        _sendBandwidthAvailable = Math.Max((int)Math.Min(1000, ticksSinceLastFlush) * _sendBytesPerMillisecond + _sendBandwidthAvailable, _sendBytesPerMillisecond * 1000.0);
                    }

                    int writeSize = (int)Math.Min((long)_sendBandwidthAvailable, bufferLength);

                    bufferLength -= writeSize;
                    SendWithDelay(new ArraySegment<byte>(buffer, bufferOffset, writeSize), bufferLength == 0);
                    bufferOffset += writeSize;
                }
                while (bufferLength != 0);
            }

            return new FlushResult(false, _isCompleted);
        }

        private void SendWithDelay(ArraySegment<byte> buffer, bool isLastBufferUse)
        {
            long sendTicks = Environment.TickCount64 + _delayMs;

            Task prev = _previousSendTask;

            if (prev.IsFaulted)
            {
                prev.GetAwaiter().GetResult();
            }

            _previousSendTask = SendWithDelayAsync();

            async Task SendWithDelayAsync()
            {
                await prev.ConfigureAwait(false);

                long ticksLeft = sendTicks - Environment.TickCount64;
                if (ticksLeft < 0)
                {
                    await Task.Delay((int)ticksLeft).ConfigureAwait(false);
                }

                await _baseWriter.WriteAsync(buffer).ConfigureAwait(false);

                if (isLastBufferUse)
                {
                    _pool.Return(buffer.Array);
                }
            }
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            return GetBuffer(sizeHint);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            return GetBuffer(sizeHint);
        }

        private byte[] GetBuffer(int sizeHint)
        {
            if (_currentBuffer?.Length >= sizeHint)
            {
                return _currentBuffer;
            }

            _pool.Return(_currentBuffer);
            _currentBuffer = _pool.Rent(sizeHint);
            return _currentBuffer;
        }
    }
}
