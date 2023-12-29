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

#if NETCOREAPP3_1_OR_GREATER && !NET6_0_OR_GREATER

using Octonica.ClickHouseClient.Exceptions;
using Octonica.ClickHouseClient.Protocol;
using System;
using System.Collections.Generic;

namespace Octonica.ClickHouseClient.Types
{
    partial class DateTypeInfo
    {
        public override IClickHouseColumnWriter CreateColumnWriter<T>(string columnName, IReadOnlyList<T> rows, ClickHouseColumnSettings? columnSettings)
        {
            if (typeof(T) != typeof(DateTime))
                throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The type \"{typeof(T)}\" can't be converted to the ClickHouse type \"{ComplexTypeName}\".");

            return new DateWriter(columnName, ComplexTypeName, (IReadOnlyList<DateTime>)rows);
        }

        public override IClickHouseLiteralWriter<T> CreateLiteralWriter<T>()
        {
            var type = typeof(T);
            if (type == typeof(DBNull))
                throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The ClickHouse type \"{ComplexTypeName}\" does not allow null values.");

            if (type == typeof(DateTime))
                return (IClickHouseLiteralWriter<T>)(object)new DateTimeLiteralWriter(this);

            throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The type \"{type}\" can't be converted to the ClickHouse type \"{ComplexTypeName}\".");
        }

        private static ushort DateTimeToDays(DateTime value)
        {
            if (value == default)
                return 0;

            var days = (value - DateTime.UnixEpoch).TotalDays;
            if (days < 0 || days > ushort.MaxValue)
                throw new OverflowException("The value must be in range [1970-01-01, 2149-06-06].");

            return (ushort)days;
        }

        public override Type GetFieldType()
        {
            return typeof(DateTime);
        }

        partial class DateReader : StructureReaderBase<DateTime>
        {
            static readonly DateTime UnixEpochUnspecified = new DateTime(DateTime.UnixEpoch.Ticks, DateTimeKind.Unspecified);

            public DateReader(int rowCount)
                : base(sizeof(ushort), rowCount)
            {
            }

            protected override DateTime ReadElement(ReadOnlySpan<byte> source)
            {
                var value = BitConverter.ToUInt16(source);
                if (value == 0)
                    return default;

                return UnixEpochUnspecified.AddDays(value);
            }
        }

        partial class DateWriter : StructureWriterBase<DateTime, ushort>
        {
            public DateWriter(string columnName, string columnType, IReadOnlyList<DateTime> rows)
                : base(columnName, columnType, sizeof(ushort), rows)
            {
            }

            protected override ushort Convert(DateTime value)
            {
                return DateTimeToDays(value);
            }
        }
    }
}

#endif