﻿#region License Apache 2.0
/* Copyright 2019-2021, 2023 Octonica
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
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Octonica.ClickHouseClient.Exceptions;
using Octonica.ClickHouseClient.Protocol;
using Octonica.ClickHouseClient.Utils;

namespace Octonica.ClickHouseClient
{
    internal class ClickHouseBinaryProtocolReader: IDisposable
    {
        private readonly ReadWriteBuffer _buffer;
        private readonly Stream _stream;
        private readonly int _bufferSize;

        private CompressionAlgorithm _currentCompression;

        private CompressionDecoderBase? _compressionDecoder;

        public ClickHouseBinaryProtocolReader(Stream stream, int bufferSize)
        {
            _buffer = new ReadWriteBuffer(bufferSize);
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _bufferSize = bufferSize;
        }

        internal void BeginDecompress(CompressionAlgorithm algorithm)
        {
            if (algorithm != CompressionAlgorithm.None)
            {
                if (_compressionDecoder != null && _compressionDecoder.Algorithm != algorithm)
                {
                    _compressionDecoder?.Dispose();
                    _compressionDecoder = null;
                }
                else
                {
                    _compressionDecoder?.Reset();
                }
            }

            switch (algorithm)
            {
                case CompressionAlgorithm.None:
                    _currentCompression = algorithm;
                    return;
                case CompressionAlgorithm.Lz4:
                    if (_compressionDecoder == null)
                        _compressionDecoder = new Lz4CompressionDecoder(_bufferSize);

                    _currentCompression = algorithm;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null);
            }
        }

        internal void EndDecompress()
        {
            _currentCompression = CompressionAlgorithm.None;
        }

        public async ValueTask<string> ReadString(bool async, CancellationToken cancellationToken)
        {
            var size = await ReadSize(async, cancellationToken);
            if (size == 0)
                return string.Empty;

            ReadOnlySequence<byte> readResult;
            do
            {
                readResult = await Read(async, cancellationToken);
                if (readResult.Length >= size)
                    break;

                AdvanceReader(readResult, 0);
                await Advance(async, cancellationToken);
            } while (true);

            string result;
            var encoding = Encoding.UTF8;
            var stringSpan = readResult.Slice(readResult.Start, readResult.GetPosition(size));
            if (stringSpan.IsSingleSegment)
            {
                result = encoding.GetString(stringSpan.FirstSpan);
            }
            else
            {
                var buffer = stringSpan.ToArray();
                result = encoding.GetString(buffer);
            }

            AdvanceReader(readResult, (int) stringSpan.Length);
            return result;
        }

        public async ValueTask<int> Read7BitInt32(bool async, CancellationToken cancellationToken)
        {
            var longValue = await Read7BitInteger(async, cancellationToken);
            if (longValue > uint.MaxValue)
                throw new FormatException(); //TODO: exception

            return unchecked((int) longValue);
        }

        public ValueTask<ulong> Read7BitUInt64(bool async, CancellationToken cancellationToken)
        {
            return Read7BitInteger(async, cancellationToken);
        }

        public async ValueTask<int> ReadInt32(bool async, CancellationToken cancellationToken)
        {
            do
            {
                var readResult = await Read(async, cancellationToken);
                if (readResult.Length < sizeof(int))
                {
                    AdvanceReader(readResult, 0);
                    await Advance(async, cancellationToken);
                    continue;
                }

                int result;
                if (readResult.FirstSpan.Length >= sizeof(int))
                    result = BitConverter.ToInt32(readResult.FirstSpan);
                else
                {
                    var tmpArr = readResult.Slice(0, sizeof(int)).ToArray();
                    result = BitConverter.ToInt32(tmpArr, 0);
                }

                AdvanceReader(readResult, sizeof(int));
                return result;

            } while (true);
        }

        public async ValueTask<int> ReadSize(bool async, CancellationToken cancellationToken)
        {
            var longValue = await Read7BitInteger(async, cancellationToken);
            if (longValue > int.MaxValue)
                throw new FormatException(); //TODO: exception

            return (int) longValue;
        }

        public async ValueTask<bool> ReadBool(bool async, CancellationToken cancellationToken)
        {
            return await ReadByte(async, cancellationToken) != 0;
        }

        public async ValueTask<byte> ReadByte(bool async, CancellationToken cancellationToken)
        {
            var readResult = await Read(async, cancellationToken);
            var result = readResult.FirstSpan[0];
            AdvanceReader(readResult, 1);
            return result;
        }

        private async ValueTask<ulong> Read7BitInteger(bool async, CancellationToken cancellationToken)
        {
            do
            {
                var readResult = await Read(async, cancellationToken);
                if (!TryRead7BitInteger(readResult, out var result, out var bytesRead))
                {
                    AdvanceReader(readResult, 0);
                    await Advance(async, cancellationToken);
                }
                else
                {
                    AdvanceReader(readResult, bytesRead);
                    return result;
                }
            } while (true);
        }

        public async ValueTask<SequenceSize> ReadRaw(Func<ReadOnlySequence<byte>, SequenceSize> readBytes, bool async, CancellationToken cancellationToken)
        {
            if (readBytes == null)
                throw new ArgumentNullException(nameof(readBytes));

            var readResult = await Read(async, cancellationToken);
            var size = readBytes(readResult);
            AdvanceReader(readResult, size.Bytes);

            return size;
        }

        public async ValueTask SkipBytes(int bytesCount, bool async, CancellationToken cancellationToken)
        {
            if (bytesCount < 0)
                throw new ArgumentException("The number of bytes for is negative.", nameof(bytesCount));

            if (bytesCount == 0)
                return;

            var c = bytesCount;
            while (c > 0)
            {
                var readResult = await Read(async, cancellationToken);
                var consumed = Math.Min(c, (int)readResult.Length);
                AdvanceReader(readResult, consumed);
                c -= consumed;
            }
        }

        internal bool TryPeekByte(out byte value)
        {
            if (_currentCompression != CompressionAlgorithm.None)
                throw new NotImplementedException();

            var readResult = _buffer.Read();
            if (readResult.IsEmpty)
            {
                value = 0;
                return false;
            }

            value = readResult.FirstSpan[0];
            return true;
        }

        public async ValueTask<IServerMessage> ReadMessage(int protocolRevision, bool throwOnUnknownMessage, bool async, CancellationToken cancellationToken)
        {
            var messageCode = (ServerMessageCode) await Read7BitInt32(async, cancellationToken);
            switch (messageCode)
            {
                case ServerMessageCode.Hello:
                    return await ServerHelloMessage.Read(this, protocolRevision, async, cancellationToken);

                case ServerMessageCode.Data:
                case ServerMessageCode.Totals:
                case ServerMessageCode.Extremes:
                    return await ServerDataMessage.Read(this, messageCode, async, cancellationToken);

                case ServerMessageCode.Error:
                    return await ServerErrorMessage.Read(this, async, cancellationToken);

                case ServerMessageCode.Progress:
                    return await ServerProgressMessage.Read(this, protocolRevision, async, cancellationToken);

                case ServerMessageCode.Pong:
                    return ServerPongMessage.Instance;

                case ServerMessageCode.EndOfStream:
                    return ServerEndOfStreamMessage.Instance;

                case ServerMessageCode.ProfileInfo:
                    return await ServerProfileInfoMessage.Read(this, async, cancellationToken);

                case ServerMessageCode.TableColumns:
                    return await ServerTableColumnsMessage.Read(this, async, cancellationToken);

                case ServerMessageCode.TableStatusResponse:
                case ServerMessageCode.Log:
                case ServerMessageCode.PartUuids:
                case ServerMessageCode.ReadTaskRequest:
                case ServerMessageCode.MergeTreeAllRangesAnnouncement:
                case ServerMessageCode.MergeTreeReadTaskRequest:
                    throw new NotImplementedException($"A message of type \"{messageCode}\" is not supported.");

                case ServerMessageCode.ProfileEvents:
                    return await ServerDataMessage.Read(this, messageCode, async, cancellationToken);

                case ServerMessageCode.TimezoneUpdate:
                    return await ServerTimeZoneUpdateMessage.Read(this, async, cancellationToken);

                default:
                    if (throwOnUnknownMessage)
                        throw new ClickHouseException(ClickHouseErrorCodes.ProtocolUnexpectedResponse, $"Internal error. Not supported message code (0x{messageCode:X}) received from the server.");

                    return new UnknownServerMessage(messageCode);
            }
        }

        private async ValueTask<ReadOnlySequence<byte>> Read(bool async, CancellationToken cancellationToken)
        {
            if (_currentCompression == CompressionAlgorithm.None)
                return await ReadFromPipe(async, cancellationToken);

            if (_compressionDecoder == null)
                throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. An encoder is not initialized.");

            if (!_compressionDecoder.IsCompleted)
                await Advance(async, cancellationToken);

            var sequence = _compressionDecoder.Read();
            while (sequence.IsEmpty)
            {
                await Advance(async, cancellationToken);
                sequence = _compressionDecoder.Read();
            }

            return sequence;
        }

        private async ValueTask<ReadOnlySequence<byte>> ReadFromPipe(bool async, CancellationToken cancellationToken)
        {
            do
            {
                var readResult = _buffer.Read();
                if (!readResult.IsEmpty)
                    return readResult;

                await AdvanceBuffer(async, cancellationToken);
            } while (true);
        }

        private void AdvanceReader(ReadOnlySequence<byte> readResult, int consumedPosition)
        {
            if (_currentCompression == CompressionAlgorithm.None)
            {
                _buffer.ConfirmRead(consumedPosition);
            }
            else
            {
                if (_compressionDecoder == null)
                    throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. A decoder is not initialized.");

                _compressionDecoder.AdvanceReader(readResult.GetPosition(consumedPosition));
            }
        }

        internal async ValueTask Advance(bool async, CancellationToken cancellationToken)
        {
            if (_currentCompression != CompressionAlgorithm.None)
            {
                if (_compressionDecoder == null)
                    throw new ClickHouseException(ClickHouseErrorCodes.InternalError, "Internal error. A decoder is not initialized.");

                if (_compressionDecoder.IsCompleted)
                {
                    while (true)
                    {
                        var buffer = await ReadFromPipe(async, cancellationToken);
                        var size = _compressionDecoder.ReadHeader(buffer);
                        if (size >= 0)
                        {
                            _buffer.ConfirmRead(size);
                            break;
                        }

                        await AdvanceBuffer(async, cancellationToken);
                    }
                }

                while (!_compressionDecoder.IsCompleted)
                {
                    var sequence = await ReadFromPipe(async, cancellationToken);
                    var consumed = _compressionDecoder.ConsumeNext(sequence);
                    _buffer.ConfirmRead(consumed);
                }

                return;
            }

            await AdvanceBuffer(async, cancellationToken);
        }

        private async ValueTask AdvanceBuffer(bool async, CancellationToken cancellationToken)
        {
            var buffer = _buffer.GetMemory();

            int bytesRead;
            if (async)
            {
                if (cancellationToken == CancellationToken.None && _stream.ReadTimeout >= 0)
                {
                    var timeout = TimeSpan.FromMilliseconds(_stream.ReadTimeout);
                    using var tokenSource = new CancellationTokenSource(timeout);
                    try
                    {
                        bytesRead = await _stream.ReadAsync(buffer, tokenSource.Token);
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new IOException($"Unable to read data from the transport connection: timeout exceeded ({timeout}).", ex);
                    }
                }
                else
                {
                    bytesRead = await _stream.ReadAsync(buffer, cancellationToken);
                }
            }
            else
            {
                bytesRead = _stream.Read(buffer.Span);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (bytesRead == 0)
                throw new EndOfStreamException($"Reached an unexpected end of the server's response. {ClickHouseConnectionStringBuilder.DefaultClientName} expected at least one more byte in the response.");

            _buffer.ConfirmWrite(bytesRead);
            _buffer.Flush();
        }

        public static bool TryRead7BitInteger(ReadOnlySequence<byte> sequence, out ulong value, out int bytesRead)
        {
            ulong result = 0;
            int i = 0, shiftSize = 0;
            foreach (var slice in sequence)
            {
                for (int j = 0; j < slice.Length; j++)
                {
                    var byteValue = slice.Span[j];
                    result |= (byteValue & (ulong)0x7F) << shiftSize;
                    i++;

                    if ((byteValue & 0x80) == 0x80)
                    {
                        shiftSize += 7;
                        if (shiftSize > sizeof(ulong) * 8 - 7)
                            throw new FormatException(); //TODO: exception
                    }
                    else
                    {
                        value = result;
                        bytesRead = i;
                        return true;
                    }
                }
            }

            value = 0;
            bytesRead = 0;
            return false;
        }

        public void Dispose()
        {
            _compressionDecoder?.Dispose();
        }
    }
}
