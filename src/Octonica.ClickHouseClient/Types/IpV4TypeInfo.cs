﻿#region License Apache 2.0
/* Copyright 2020-2021, 2023 Octonica
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Octonica.ClickHouseClient.Exceptions;
using Octonica.ClickHouseClient.Protocol;
using Octonica.ClickHouseClient.Utils;

namespace Octonica.ClickHouseClient.Types
{
    internal sealed class IpV4TypeInfo : SimpleTypeInfo
    {
        public IpV4TypeInfo()
            : base("IPv4")
        {
        }

        public override IClickHouseColumnReader CreateColumnReader(int rowCount)
        {
            return new IpV4Reader(rowCount);
        }

        public override IClickHouseColumnReaderBase CreateSkippingColumnReader(int rowCount)
        {
            return new SimpleSkippingColumnReader(sizeof(uint), rowCount);
        }

        public override IClickHouseColumnWriter CreateColumnWriter<T>(string columnName, IReadOnlyList<T> rows, ClickHouseColumnSettings? columnSettings)
        {
            var type = typeof(T);
            IReadOnlyList<uint> preparedRows;

            if (typeof(IPAddress).IsAssignableFrom(type))
                preparedRows = MappedReadOnlyList<IPAddress, uint>.Map((IReadOnlyList<IPAddress>)rows, IpAddressToUInt32);
            else if (type == typeof(string))
                preparedRows = MappedReadOnlyList<string, uint>.Map((IReadOnlyList<string>)rows, IpAddressStringToUInt32);
            else if (type == typeof(uint))
                preparedRows = (IReadOnlyList<uint>)rows;
            else if (type == typeof(int))
                preparedRows = MappedReadOnlyList<int, uint>.Map((IReadOnlyList<int>)rows, v => unchecked((uint)v));
            else
                throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The type \"{typeof(T)}\" can't be converted to the ClickHouse type \"{ComplexTypeName}\".");

            return new IpV4Writer(columnName, TypeName, preparedRows);
        }

        public override IClickHouseLiteralWriter<T> CreateLiteralWriter<T>()
        {
            var type = typeof(T);
            if (type == typeof(DBNull))
                throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The ClickHouse type \"{ComplexTypeName}\" does not allow null values.");

            const string valueType = "UInt32";
            object writer;
            if (type == typeof(IPAddress))
                writer = new SimpleLiteralWriter<IPAddress, uint>(valueType, this, null, true, IpAddressToUInt32);
            else if (type == typeof(string))
                writer = new SimpleLiteralWriter<string, uint>(valueType, this, null, true, IpAddressStringToUInt32);
            else if (type == typeof(uint))
                writer = new SimpleLiteralWriter<uint>(valueType, this, appendTypeCast: true);
            else if (type == typeof(int))
                writer = new SimpleLiteralWriter<int, uint>(valueType, this, null, true, v => unchecked((uint)v));
            else
                throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The type \"{type}\" can't be converted to the ClickHouse type \"{ComplexTypeName}\".");

            return (IClickHouseLiteralWriter<T>)writer;
        }

        public override Type GetFieldType()
        {
            return typeof(IPAddress);
        }

        public override ClickHouseDbType GetDbType()
        {
            return ClickHouseDbType.IpV4;
        }

        private static uint IpAddressStringToUInt32(string? address)
        {
            if (address == null)
                return 0;

            if (!IPAddress.TryParse(address, out var ipAddress))
                throw new InvalidCastException($"The string \"{address}\" is not a valid IPv4 address.");

            return IpAddressToUInt32(ipAddress);
        }

        private static uint IpAddressToUInt32(IPAddress? address)
        {
            if (address == null)
                return 0;

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidCastException($"The network address \"{address}\" is not a IPv4 address.");

            Span<uint> result = stackalloc uint[1];
            if (!address.TryWriteBytes(MemoryMarshal.AsBytes(result), out var written) || written != sizeof(uint))
                throw new InvalidCastException($"The network address \"{address}\" is not a IPv4 address.");

            return unchecked((uint) IPAddress.HostToNetworkOrder((int) result[0]));
        }

        private sealed class IpV4Reader : IpColumnReaderBase
        {
            public IpV4Reader(int rowCount)
                : base(rowCount, sizeof(uint))
            {
            }

            protected override IClickHouseTableColumn<IPAddress> EndRead(ReadOnlyMemory<byte> buffer)
            {
                return new IpV4TableColumn(buffer);
            }
        }

        private sealed class IpV4Writer : StructureWriterBase<uint>
        {
            protected override bool BitwiseCopyAllowed => true;

            public IpV4Writer(string columnName, string columnType, IReadOnlyList<uint> rows)
                : base(columnName, columnType, sizeof(uint), rows)
            {
            }

            protected override void WriteElement(Span<byte> writeTo, in uint value)
            {
                var success = BitConverter.TryWriteBytes(writeTo, value);
                Debug.Assert(success);
            }
        }
    }
}
