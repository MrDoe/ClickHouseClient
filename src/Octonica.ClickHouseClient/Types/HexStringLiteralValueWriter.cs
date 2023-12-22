﻿#region License Apache 2.0
/* Copyright 2023 Octonica
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

using Octonica.ClickHouseClient.Protocol;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Octonica.ClickHouseClient.Types
{
    internal sealed class HexStringLiteralValueWriter : IClickHouseParameterValueWriter
    {
        public const string HexDigits = "0123456789ABCDEF";

        private static readonly byte[] ForbiddenBytes = new[] { (byte)'\t', (byte)10, (byte)'\\' };

        private readonly ReadOnlyMemory<byte> _value;
        private readonly bool _includeQuotes;

        public int Length => (_includeQuotes ? 2 : 0) + 4 * _value.Length;

        private HexStringLiteralValueWriter(ReadOnlyMemory<byte> value, bool includeQuotes)
        {
            _value = value;
            _includeQuotes = includeQuotes;
        }

        public int Write(Memory<byte> buffer)
        {
            Debug.Assert(buffer.Length >= Length);

            int count = 0;
            if (_includeQuotes)
                buffer.Span[count++] = (byte)'\'';

            foreach (var byteValue in _value.Span)
            {
                buffer.Span[count++] = (byte)'\\';
                buffer.Span[count++] = (byte)'x';
                buffer.Span[count++] = (byte)HexDigits[byteValue >> 4];
                buffer.Span[count++] = (byte)HexDigits[byteValue & 0xF];
            }

            if (_includeQuotes)
                buffer.Span[count++] = (byte)'\'';

            Debug.Assert(count == Length);
            return count;
        }

        public static bool TryCreate(ReadOnlyMemory<byte> value, bool isNested, [MaybeNullWhen(false)] out IClickHouseParameterValueWriter writer)
        {
            var indexOfForbidden = value.Span.IndexOfAny(ForbiddenBytes);
            if (indexOfForbidden == -1)
            {
                writer = new HexStringLiteralValueWriter(value, isNested);
                return true;
            }

            writer = null;
            return false;
        }
    }
}
