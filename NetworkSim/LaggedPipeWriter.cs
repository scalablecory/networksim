using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkSim
{
    sealed class LaggedPipeWriter : PipeWriter
    {
        readonly PipeWriter _baseWriter;
        readonly Pipe _buffer = new Pipe();
        readonly double _sendBytesPerMillisecond;
        double _sendBandwidthAvailable;
        long _flushLength, _lastFlushTicks;
        bool _complete;

        public LaggedPipeWriter(PipeWriter baseWriter, int bytesPerSecond)
        {
            _baseWriter = baseWriter;
            _sendBytesPerMillisecond = bytesPerSecond * 0.001;
        }

        public override void Advance(int bytes)
        {
            throw new NotImplementedException();
        }

        public override void CancelPendingFlush()
        {
            throw new NotImplementedException();
        }

        public override void Complete(Exception exception = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            throw new NotImplementedException();
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            throw new NotImplementedException();
        }

        readonly struct WriteOp
        {
            public int Size { get; }
            public TaskCompletionSource<int> CompletionSource { get; }

            public WriteOp(int size)
            {
                Size = size;
                CompletionSource = new TaskCompletionSource<int>();
            }
        }
    }
}
