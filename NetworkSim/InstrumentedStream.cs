using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkSim
{
    delegate void StreamMeasurementEvent(double bytesReadPerSecond, double bytesWrittenPerSecond);

    sealed class InstrumentedStream : Stream
    {
        readonly Stream _baseStream;
        readonly Timer _timer;
        readonly State _state = new State();

        public event StreamMeasurementEvent Measurement
        {
            add => _state.MeasurementEvent += value;
            remove => _state.MeasurementEvent -= value;
        }

        public override bool CanRead => _baseStream.CanRead;

        public override bool CanSeek => _baseStream.CanSeek;

        public override bool CanWrite => _baseStream.CanWrite;

        public override long Length => _baseStream.Length;

        public override long Position { get => _baseStream.Position; set => _baseStream.Position = value; }

        public InstrumentedStream(Stream baseStream)
        {
            _baseStream = baseStream;
            _timer = new Timer(o => ((State)o).OnMeasurement(), _state, 1000, 1000);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int len = await _baseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _state.BytesRead, len);

            return len;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length != 0)
            {
                int sendLength = Math.Min(buffer.Length, 1024);

                await _baseStream.WriteAsync(buffer.Slice(0, sendLength), cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _state.BytesWritten, sendLength);

                buffer = buffer.Slice(sendLength);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _baseStream.FlushAsync(cancellationToken);
        }

        sealed class State
        {
            public event StreamMeasurementEvent MeasurementEvent;
            public long BytesRead, BytesWritten;

            long _lastMeasurement;

            public void OnMeasurement()
            {
                long bytesRead = Interlocked.Exchange(ref BytesRead, 0);
                long bytesWritten = Interlocked.Exchange(ref BytesWritten, 0);
                long curTicks = Environment.TickCount64;
                
                long lastTicks = Volatile.Read(ref _lastMeasurement);
                Volatile.Write(ref _lastMeasurement, curTicks);

                double rCurTicks = 1000.0 / (lastTicks - curTicks);
                MeasurementEvent?.Invoke(bytesRead * rCurTicks, bytesWritten * rCurTicks);
            }
        }
    }
}
