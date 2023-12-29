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
using System.Collections.Generic;
using System.Diagnostics;
using Octonica.ClickHouseClient.Exceptions;
using Octonica.ClickHouseClient.Protocol;
using Octonica.ClickHouseClient.Utils;

namespace Octonica.ClickHouseClient.Types
{
    internal sealed class Int32TypeInfo : SimpleTypeInfo
    {
        public Int32TypeInfo()
            : base("Int32")
        {
        }

        public override IClickHouseColumnReader CreateColumnReader(int rowCount)
        {
            return new Int32Reader(rowCount);
        }

        public override IClickHouseColumnReaderBase CreateSkippingColumnReader(int rowCount)
        {
            return new SimpleSkippingColumnReader(sizeof(int), rowCount);
        }

        public override IClickHouseColumnWriter CreateColumnWriter<T>(string columnName, IReadOnlyList<T> rows, ClickHouseColumnSettings? columnSettings)
        {
            var type = typeof(T);
            IReadOnlyList<int> intRows;
            if (type == typeof(int))
                intRows = (IReadOnlyList<int>)rows;
            else if (type == typeof(short))
                intRows = MappedReadOnlyList<short, int>.Map((IReadOnlyList<short>)rows, v => v);
            else if (type == typeof(ushort))
                intRows = MappedReadOnlyList<ushort, int>.Map((IReadOnlyList<ushort>)rows, v => v);
            else if (type == typeof(sbyte))
                intRows = MappedReadOnlyList<sbyte, int>.Map((IReadOnlyList<sbyte>)rows, v => v);
            else if (type == typeof(byte))
                intRows = MappedReadOnlyList<byte, int>.Map((IReadOnlyList<byte>)rows, v => v);
            else
                throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The type \"{typeof(T)}\" can't be converted to the ClickHouse type \"{ComplexTypeName}\".");
            
            return new Int32Writer(columnName, ComplexTypeName, intRows);
        }

        public override IClickHouseParameterWriter<T> CreateParameterWriter<T>()
        {
            var type = typeof(T);
            if (type == typeof(DBNull))
                throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The ClickHouse type \"{ComplexTypeName}\" does not allow null values.");

            object writer = default(T) switch
            {
                int _ => new SimpleParameterWriter<int>(this, appendTypeCast: true),
                short _ => new SimpleParameterWriter<short>(this, appendTypeCast: true),
                ushort _ => new SimpleParameterWriter<ushort>(this, appendTypeCast: true),
                sbyte _ => new SimpleParameterWriter<sbyte>(this, appendTypeCast: true),
                byte _ => new SimpleParameterWriter<byte>(this, appendTypeCast: true),
                _ => throw new ClickHouseException(ClickHouseErrorCodes.TypeNotSupported, $"The type \"{type}\" can't be converted to the ClickHouse type \"{ComplexTypeName}\".")
            };

            return (IClickHouseParameterWriter<T>)writer;
        }

        public override Type GetFieldType()
        {
            return typeof(int);
        }

        public override ClickHouseDbType GetDbType()
        {
            return ClickHouseDbType.Int32;
        }

        private sealed class Int32Reader : StructureReaderBase<int>
        {
            protected override bool BitwiseCopyAllowed => true;

            public Int32Reader(int rowCount)
                : base(sizeof(int), rowCount)
            {
            }

            protected override int ReadElement(ReadOnlySpan<byte> source)
            {
                return BitConverter.ToInt32(source);
            }

            protected override IClickHouseTableColumn<int> EndRead(ClickHouseColumnSettings? settings, ReadOnlyMemory<int> buffer)
            {
                return new Int32TableColumn(buffer);
            }
        }

        private sealed class Int32Writer : StructureWriterBase<int>
        {
            protected override bool BitwiseCopyAllowed => true;

            public Int32Writer(string columnName, string columnType, IReadOnlyList<int> rows)
                : base(columnName, columnType, sizeof(int), rows)
            {
            }

            protected override void WriteElement(Span<byte> writeTo, in int value)
            {
                var success = BitConverter.TryWriteBytes(writeTo, value);
                Debug.Assert(success);
            }
        }
    }
}
