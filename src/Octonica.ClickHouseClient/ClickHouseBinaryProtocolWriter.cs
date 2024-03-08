﻿#region License Apache 2.0
/* Copyright 2019-2021, 2023-2024 Octonica
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
#endregion

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Octonica.ClickHouseClient.Exceptions;
using Octonica.ClickHouseClient.Protocol;
using Octonica.ClickHouseClient.Utils;

namespace Octonica.ClickHouseClient
{
    internal class ClickHouseBinaryProtocolWriter : IDisposable
    {
        private readonly int _bufferSize;

        private readonly ReadWriteBuffer _buffer;
        private readonly Stream _stream;

        private CompressionAlgorithm _currentCompression;

        private CompressionEncoderBase? _compressionEncoder;

        public ClickHouseBinaryProtocolWriter(Stream stream, int bufferSize)
        {
            _buffer = new ReadWriteBuffer(bufferSize);
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _bufferSize = bufferSize;
        }

        public async ValueTask Flush(bool async, CancellationToken cancellationToken)
        {
            if (_currentCompression != CompressionAlgorithm.None)
                throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. The stream can't be flushed because it's compression is not completed.");

            _buffer.Flush();

            var readResult = _buffer.Read();
            if (readResult.IsEmpty)
                return;

            foreach (var buffer in readResult)
            {
                if (async)
                    await WriteWithTimeoutAsync((networkStream, ct) => networkStream.WriteAsync(buffer, ct).AsTask(), cancellationToken);
                else
                    _stream.Write(buffer.Span);
            }

            _buffer.ConfirmRead((int) readResult.Length);

            if (async)
                await WriteWithTimeoutAsync((networkStream, ct) => networkStream.FlushAsync(ct), cancellationToken);
            else
                _stream.Flush();
        }

        public void Discard()
        {
            _buffer.Discard();

            var readResult = _buffer.Read();
            if (!readResult.IsEmpty)
                _buffer.ConfirmRead((int) readResult.Length);

            _currentCompression = CompressionAlgorithm.None;
        }

        public void BeginCompress(CompressionAlgorithm algorithm, int compressionBlockSize)
        {
            if (_compressionEncoder != null)
            {
                if (_compressionEncoder.Algorithm != algorithm)
                {
                    _compressionEncoder.Dispose();
                    _compressionEncoder = null;
                }
                else
                {
                    _currentCompression = _compressionEncoder.Algorithm;
                    _compressionEncoder.Reset();
                    return;
                }
            }

            switch (algorithm)
            {
                case CompressionAlgorithm.None:
                    break;

                case CompressionAlgorithm.Lz4:
                    _currentCompression = algorithm;
                    _compressionEncoder = new Lz4CompressionEncoder(_bufferSize, compressionBlockSize);
                    break;

                default:
                    throw new NotSupportedException($"Compression algorithm \"{algorithm}\" is not supported.");
            }
        }

        public void EndCompress()
        {
            _compressionEncoder?.Complete(_buffer);
            _currentCompression = CompressionAlgorithm.None;
        }

        public void WriteString(string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            var encoding = Encoding.UTF8;
            var length = encoding.GetByteCount(value);
            Write7BitInteger((uint) length);
            if (length == 0)
                return;

            var charSpan = value.AsSpan();
            var byteSpan = GetSpan(length);
            var count = Encoding.UTF8.GetBytes(charSpan, byteSpan);
            Debug.Assert(count == length);

            Advance(length);
        }

        public SequenceSize WriteRaw(Func<Memory<byte>, SequenceSize> writeBytes)
        {
            return WriteRaw(0, writeBytes);
        }

        public SequenceSize WriteRaw(int sizeHint, Func<Memory<byte>, SequenceSize> writeBytes)
        {
            if (writeBytes == null)
                throw new ArgumentNullException(nameof(writeBytes));

            SequenceSize size;
            var memory = sizeHint > 0 ? GetMemory(sizeHint) : GetMemory();
            if (!memory.IsEmpty)
            {
                try
                {
                    size = writeBytes(memory);
                }
                catch
                {
                    Advance(0);
                    throw;
                }

                if (size.Bytes > 0 || size.Elements > 0)
                {
                    Advance(size.Bytes);
                    return size;
                }
            }

            var bufferSize = _bufferSize;
            do
            {
                Advance(0);

                memory = GetMemory(bufferSize);
                try
                {
                    size = writeBytes(memory);
                }
                catch
                {
                    Advance(0);
                    throw;
                }

                bufferSize *= 2;

            } while (size.Bytes == 0 && size.Elements == 0);

            Advance(size.Bytes);
            return size;
        }

        public void WriteInt32(int value)
        {
            var span = GetSpan(sizeof(int));
            var success = BitConverter.TryWriteBytes(span, value);
            Debug.Assert(success);

            Advance(sizeof(int));
        }

        public void WriteBool(bool value)
        {
            WriteByte(value ? (byte) 1 : (byte) 0);
        }

        public void WriteByte(byte value)
        {
            var buffer = GetSpan(1);
            buffer[0] = value;
            Advance(1);
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            var span = GetSpan(bytes.Length);
            bytes.CopyTo(span);
            Advance(bytes.Length);
        }

        private async Task WriteWithTimeoutAsync(Func<Stream, CancellationToken, Task> writeAsync, CancellationToken cancellationToken)
        {
            if (cancellationToken == CancellationToken.None && _stream.WriteTimeout >= 0)
            {
                var timeout = TimeSpan.FromMilliseconds(_stream.WriteTimeout);
                using var tokenSource = new CancellationTokenSource(timeout);
                try
                {
                    await writeAsync(_stream, tokenSource.Token);
                }
                catch (OperationCanceledException ex)
                {
                    throw new IOException($"Unable to write data to the transport connection: timeout exceeded ({timeout}).", ex);
                }
            }
            else
            {
                await writeAsync(_stream, cancellationToken);
            }
        }

        public void Write7BitInt32(int value)
        {
            var ulongValue = (ulong) unchecked((uint) value);
            Write7BitInteger(ulongValue);
        }

        private void Write7BitInteger(ulong value)
        {
            ulong v = value;
            int totalLength = 0;

            var buffer = GetSpan(10);
            for (int i = 0; i < buffer.Length; i++)
            {
                ++totalLength;

                if (v >= 0x80)
                {
                    buffer[i] = (byte) (v | 0x80);
                    v >>= 7;
                }
                else
                {
                    buffer[i] = (byte) v;
                    Advance(totalLength);
                    return;
                }
            }

            Advance(totalLength);
        }

        private Span<byte> GetSpan(int sizeHint)
        {
            if (_currentCompression != CompressionAlgorithm.None)
            {
                if (_compressionEncoder == null)
                    throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. An encoder is not initialized.");

                return _compressionEncoder.GetSpan(sizeHint);
            }

            return _buffer.GetMemory(sizeHint).Span;
        }

        private Memory<byte> GetMemory(int sizeHint)
        {
            if (_currentCompression == CompressionAlgorithm.None)
                return _buffer.GetMemory(sizeHint);

            if (_compressionEncoder == null)
                throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. An encoder is not initialized.");

            return _compressionEncoder.GetMemory(sizeHint);

        }

        private Memory<byte> GetMemory()
        {
            if (_currentCompression == CompressionAlgorithm.None)
                return _buffer.GetMemory();

            if (_compressionEncoder == null)
                throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. An encoder is not initialized.");

            return _compressionEncoder.GetMemory();
        }

        private void Advance(int bytes)
        {
            if (_currentCompression != CompressionAlgorithm.None)
            {
                if (_compressionEncoder == null)
                    throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. An encoder is not initialized.");

                _compressionEncoder.Advance(bytes);
            }
            else
            {
                _buffer.ConfirmWrite(bytes);
            }
        }

        public static int TryWrite7BitInteger(Span<byte> buffer, ulong value)
        {
            ulong v = value;
            int count = 0;

            while (true)
            {
                if (buffer.Length == count)
                    return 0;

                if (v >= 0x80)
                {
                    buffer[count++] = (byte) (v | 0x80);
                    v >>= 7;
                }
                else
                {
                    buffer[count++] = (byte) v;
                    break;
                }
            }

            return count;
        }

        public void Dispose()
        {
            _compressionEncoder?.Dispose();
        }
    }
}
